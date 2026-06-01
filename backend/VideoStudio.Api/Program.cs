using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VideoStudio.Api.Contracts;
using VideoStudio.Api.Data;
using VideoStudio.Api.Domain;
using VideoStudio.Api.Options;
using VideoStudio.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<RenderSettings>(builder.Configuration.GetSection("DefaultRenderSettings"));

builder.Services.AddDbContext<VideoStudioDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddHttpClient<OllamaStoryPlanner>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromMinutes(3);
});
builder.Services.AddScoped<ProductionPlanJsonService>();
builder.Services.AddScoped<ProductionPlanNormalizer>();
builder.Services.AddScoped<ProductionPlanMapper>();
builder.Services.AddScoped<PromptCompiler>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("LocalFrontend", policy =>
    {
        policy
            .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "VideoStudio API v1");
        options.RoutePrefix = "swagger";
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

if (app.Environment.IsDevelopment())
{
    app.UseCors("LocalFrontend");
}

app.MapPost("/api/projects", async (CreateProjectRequest request, VideoStudioDbContext db) =>
{
    var title = string.IsNullOrWhiteSpace(request.Title) ? request.Name : request.Title;
    if (string.IsNullOrWhiteSpace(title))
    {
        return Results.BadRequest("Project title is required.");
    }

    var project = new Project
    {
        Name = title.Trim(),
        StoryText = request.StoryText ?? request.Description,
        TargetDurationSeconds = request.TargetDurationSeconds is > 0 ? request.TargetDurationSeconds.Value : 60
    };
    db.Projects.Add(project);
    await db.SaveChangesAsync();

    return Results.Created($"/api/projects/{project.Id}", ToSummary(project));
});

app.MapGet("/api/projects", async (VideoStudioDbContext db) =>
{
    var projects = await db.Projects
        .OrderByDescending(p => p.UpdatedAt)
        .Select(p => new ProjectSummaryDto(p.Id, p.Name, p.StoryText, p.TargetDurationSeconds, p.Status, p.CreatedAt, p.UpdatedAt))
        .ToListAsync();

    return Results.Ok(projects);
});

app.MapGet("/api/projects/{id:guid}", async (Guid id, VideoStudioDbContext db) =>
{
    var project = await db.Projects
        .Include(p => p.Characters)
        .Include(p => p.Scenes).ThenInclude(s => s.Shots)
        .Include(p => p.Assets)
        .FirstOrDefaultAsync(p => p.Id == id);

    return project is null ? Results.NotFound() : Results.Ok(ToDetails(project));
});

app.MapPost("/api/projects/{id:guid}/story", async (Guid id, StoryRequest request, VideoStudioDbContext db, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    try
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (project is null)
        {
            return Results.NotFound();
        }

        project.StoryText = request.StoryText;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToSummary(project));
    }
    catch (DbUpdateConcurrencyException ex)
    {
        var conflictEntities = string.Join(", ", ex.Entries.Select(e => e.Entity.GetType().Name).Distinct());
        logger.LogWarning(ex, "Concurrency conflict while updating story for project {ProjectId}. Entities: {Entities}", id, conflictEntities);
        return Results.Conflict("Project was updated by another operation. Please retry.");
    }
});

app.MapPost("/api/projects/{id:guid}/analyze", async (Guid id, VideoStudioDbContext db, OllamaStoryPlanner planner, ProductionPlanMapper mapper, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    var snapshot = await db.Projects
        .AsNoTracking()
        .Where(p => p.Id == id)
        .Select(p => new
        {
            p.Id,
            p.Name,
            p.StoryText,
            p.TargetDurationSeconds,
            p.Status
        })
        .FirstOrDefaultAsync(cancellationToken);

    if (snapshot is null)
    {
        return Results.NotFound();
    }

    if (string.IsNullOrWhiteSpace(snapshot.StoryText))
    {
        return Results.BadRequest("Project storyText is required before analysis.");
    }

    logger.LogInformation("Analyze started for project {ProjectId}", id);
    var planningUpdated = await db.Projects
        .Where(p => p.Id == id)
        .ExecuteUpdateAsync(updates => updates
            .SetProperty(p => p.Status, ProjectStatus.Planning)
            .SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);

    if (planningUpdated == 0)
    {
        return Results.NotFound();
    }

    db.ChangeTracker.Clear();

    logger.LogInformation("Calling Ollama planner for project {ProjectId}", id);
    StoryResultDto result;
    try
    {
        result = await planner.CreatePlanAsync(snapshot.Name, snapshot.StoryText, snapshot.TargetDurationSeconds, cancellationToken);
        logger.LogInformation("Ollama planner completed for project {ProjectId}", id);
    }
    catch (Exception ex)
    {
        await MarkProjectStatusAsync(db, id, ProjectStatus.Failed, cancellationToken);
        logger.LogWarning(ex, "Analyze failed during Ollama call for project {ProjectId}", id);
        return Results.BadRequest(new StoryResultDto(false, null, $"Ollama request failed: {ex.Message}"));
    }

    if (!result.Success || result.Plan is null)
    {
        await MarkProjectStatusAsync(db, id, ProjectStatus.Failed, cancellationToken);
        logger.LogWarning("Analyze returned invalid plan for project {ProjectId}", id);
        return Results.BadRequest(result);
    }

    logger.LogInformation(
        "Saving production plan for project {ProjectId}: Characters={CharacterCount}, Scenes={SceneCount}, Shots={ShotCount}",
        id,
        result.Plan.Characters.Count,
        result.Plan.Scenes.Count,
        result.Plan.Scenes.Sum(s => s.Shots.Count));

    try
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.RenderJobs
            .Where(j => j.ProjectId == id && j.Status != RenderJobStatus.Pending && j.Status != RenderJobStatus.Failed)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(j => j.SceneId, (Guid?)null)
                .SetProperty(j => j.ShotId, (Guid?)null)
                .SetProperty(j => j.CharacterId, (Guid?)null), cancellationToken);

        await db.RenderJobs
            .Where(j => j.ProjectId == id && (j.Status == RenderJobStatus.Pending || j.Status == RenderJobStatus.Failed))
            .ExecuteDeleteAsync(cancellationToken);

        var sceneIds = db.Scenes.Where(s => s.ProjectId == id).Select(s => s.Id);
        await db.Shots.Where(s => sceneIds.Contains(s.SceneId)).ExecuteDeleteAsync(cancellationToken);
        await db.DialogueLines.Where(d => d.ProjectId == id).ExecuteDeleteAsync(cancellationToken);
        await db.Scenes.Where(s => s.ProjectId == id).ExecuteDeleteAsync(cancellationToken);
        await db.Characters.Where(c => c.ProjectId == id).ExecuteDeleteAsync(cancellationToken);

        var projectUpdate = mapper.BuildProjectUpdate(result.Plan);
        var updatedRows = await db.Projects
            .Where(p => p.Id == id)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(p => p.Name, projectUpdate.Title)
                .SetProperty(p => p.Logline, projectUpdate.Logline)
                .SetProperty(p => p.Genre, projectUpdate.Genre)
                .SetProperty(p => p.TargetDurationSeconds, projectUpdate.TargetDurationSeconds)
                .SetProperty(p => p.VisualStylePrompt, projectUpdate.VisualStylePrompt)
                .SetProperty(p => p.NegativePrompt, projectUpdate.NegativePrompt)
                .SetProperty(p => p.CameraStyle, projectUpdate.CameraStyle)
                .SetProperty(p => p.LightingStyle, projectUpdate.LightingStyle)
                .SetProperty(p => p.ColorPalette, projectUpdate.ColorPalette)
                .SetProperty(p => p.AudioCuesJson, projectUpdate.AudioCuesJson)
                .SetProperty(p => p.Status, ProjectStatus.ReadyForRender)
                .SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);

        if (updatedRows == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Results.NotFound();
        }

        var characters = mapper.BuildCharacters(id, result.Plan);
        var scenes = mapper.BuildScenes(id, result.Plan);
        var dialogueLines = mapper.BuildDialogueLines(id, result.Plan, scenes);
        db.Characters.AddRange(characters);
        db.Scenes.AddRange(scenes);
        db.DialogueLines.AddRange(dialogueLines);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Production plan replacement completed for project {ProjectId}", id);
        db.ChangeTracker.Clear();
        var savedProject = await db.Projects.Include(p => p.Characters).Include(p => p.Scenes).ThenInclude(s => s.Shots).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (savedProject is null)
        {
            return Results.NotFound();
        }
        return Results.Ok(mapper.FromProject(savedProject));
    }
    catch (DbUpdateConcurrencyException ex)
    {
        var conflictEntities = string.Join(", ", ex.Entries.Select(e => e.Entity.GetType().Name).Distinct());
        logger.LogWarning(ex, "Concurrency conflict while analyzing project {ProjectId}. Entities: {Entities}", id, conflictEntities);
        return Results.Conflict("Project plan update conflicted with another operation. Please retry.");
    }
});

app.MapGet("/api/projects/{id:guid}/production-plan", async (Guid id, VideoStudioDbContext db, ProductionPlanMapper mapper) =>
{
    var project = await db.Projects.Include(p => p.Characters).Include(p => p.Scenes).ThenInclude(s => s.Shots).FirstOrDefaultAsync(p => p.Id == id);
    if (project is null)
    {
        return Results.NotFound();
    }

    if (project.Scenes.Count == 0)
    {
        return Results.NotFound("No production plan exists for this project.");
    }

    return Results.Ok(mapper.FromProject(project));
});

app.MapPost("/api/projects/{id:guid}/preproduction/prepare", async (Guid id, VideoStudioDbContext db) =>
{
    var project = await db.Projects
        .Include(p => p.Characters)
        .Include(p => p.Scenes).ThenInclude(s => s.Shots)
        .Include(p => p.RenderJobs)
        .FirstOrDefaultAsync(p => p.Id == id);
    if (project is null)
    {
        return Results.NotFound();
    }

    if (project.Scenes.Count == 0)
    {
        return Results.BadRequest("Analyze the story before preparing visuals.");
    }

    foreach (var character in project.Characters)
    {
        character.ReferenceImagePrompt = RequiredText(character.ReferenceImagePrompt, BuildCharacterReferencePrompt(project, character));
        character.ReferenceImageNegativePrompt = RequiredText(character.ReferenceImageNegativePrompt, ProductionPlanNormalizer.DefaultImageNegativePrompt());
        character.ReferenceStatus = string.IsNullOrWhiteSpace(character.ReferenceImagePath) ? "PromptReady" : "Ready";
    }

    foreach (var scene in project.Scenes)
    {
        foreach (var shot in scene.Shots)
        {
            shot.StartImagePrompt = RequiredText(shot.StartImagePrompt, BuildShotStartImagePrompt(project, scene, shot));
            shot.StartImageNegativePrompt = RequiredText(shot.StartImageNegativePrompt, ProductionPlanNormalizer.DefaultImageNegativePrompt());
            shot.StartImageStatus = string.IsNullOrWhiteSpace(shot.StartImagePath) ? "PromptReady" : "Ready";
        }
    }

    project.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();

    return Results.Ok(BuildPreproductionDto(project));
});

app.MapGet("/api/projects/{id:guid}/preproduction", async (Guid id, VideoStudioDbContext db) =>
{
    var project = await db.Projects
        .Include(p => p.Characters)
        .Include(p => p.Scenes).ThenInclude(s => s.Shots)
        .Include(p => p.RenderJobs)
        .FirstOrDefaultAsync(p => p.Id == id);
    if (project is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(BuildPreproductionDto(project));
});

app.MapPatch("/api/projects/{projectId:guid}/characters/{characterId:guid}/reference-prompt", async (Guid projectId, Guid characterId, CharacterReferencePromptRequest request, VideoStudioDbContext db) =>
{
    var character = await db.Characters.FirstOrDefaultAsync(c => c.Id == characterId && c.ProjectId == projectId);
    if (character is null)
    {
        return Results.NotFound();
    }

    character.ReferenceImagePrompt = request.ReferenceImagePrompt?.Trim();
    character.ReferenceImageNegativePrompt = request.ReferenceImageNegativePrompt?.Trim();
    character.ReferenceStatus = string.IsNullOrWhiteSpace(character.ReferenceImagePath) ? "PromptReady" : "Ready";
    await db.Projects.Where(p => p.Id == projectId).ExecuteUpdateAsync(updates => updates.SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow));
    await db.SaveChangesAsync();
    return Results.Ok(new { character.Id, character.ReferenceImagePrompt, character.ReferenceImageNegativePrompt, character.ReferenceStatus });
});

app.MapPatch("/api/projects/{projectId:guid}/scenes/{sceneId:guid}/shots/{shotId:guid}/start-image-prompt", async (Guid projectId, Guid sceneId, Guid shotId, ShotStartImagePromptRequest request, VideoStudioDbContext db) =>
{
    var shot = await db.Shots.FirstOrDefaultAsync(s => s.Id == shotId && s.SceneId == sceneId && s.ProjectId == projectId);
    if (shot is null)
    {
        return Results.NotFound();
    }

    shot.StartImagePrompt = request.StartImagePrompt?.Trim();
    shot.StartImageNegativePrompt = request.StartImageNegativePrompt?.Trim();
    shot.StartImageStatus = string.IsNullOrWhiteSpace(shot.StartImagePath) ? "PromptReady" : "Ready";
    await db.Projects.Where(p => p.Id == projectId).ExecuteUpdateAsync(updates => updates.SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow));
    await db.SaveChangesAsync();
    return Results.Ok(new { shot.Id, shot.StartImagePrompt, shot.StartImageNegativePrompt, shot.StartImageStatus });
});

app.MapGet("/api/projects/{id:guid}/scenes", async (Guid id, VideoStudioDbContext db) =>
{
    var exists = await db.Projects.AnyAsync(p => p.Id == id);
    if (!exists)
    {
        return Results.NotFound();
    }

    var scenes = await db.Scenes
        .Where(s => s.ProjectId == id)
        .Include(s => s.Shots)
        .OrderBy(s => s.Index)
        .Select(s => new
        {
            id = s.Id,
            projectId = s.ProjectId,
            sceneIndex = s.Index,
            title = s.Title,
            summary = s.Summary,
            location = s.Location,
            mood = s.Mood,
            targetDurationSeconds = s.EstimatedDurationSeconds,
            shotCount = s.Shots.Count
        })
        .ToListAsync();

    return Results.Ok(scenes);
});

app.MapGet("/api/projects/{id:guid}/shots", async (Guid id, VideoStudioDbContext db) =>
{
    var exists = await db.Projects.AnyAsync(p => p.Id == id);
    if (!exists)
    {
        return Results.NotFound();
    }

    var shots = await db.Shots
        .Where(s => s.ProjectId == id)
        .Include(s => s.Scene)
        .OrderBy(s => s.Scene!.Index)
        .ThenBy(s => s.Index)
        .Select(s => new
        {
            id = s.Id,
            projectId = s.ProjectId,
            sceneId = s.SceneId,
            sceneIndex = s.Scene != null ? s.Scene.Index : 0,
            shotIndex = s.Index,
            title = s.ShotType,
            description = s.Action,
            visualPrompt = s.Prompt,
            negativePrompt = s.NegativePrompt,
            cameraDirection = s.ShotType,
            motionDirection = s.CameraMotion,
            targetDurationSeconds = s.DurationSeconds,
            continuityNotes = s.ContinuityNotes,
            startImagePrompt = s.StartImagePrompt,
            startImageNegativePrompt = s.StartImageNegativePrompt,
            startImageStatus = s.StartImageStatus,
            startImagePath = s.StartImagePath,
            startImageUrl = s.StartImageUrl,
            outputPath = s.OutputPath
        })
        .ToListAsync();

    return Results.Ok(shots);
});

app.MapPatch("/api/projects/{projectId:guid}/scenes/{sceneId:guid}", async (Guid projectId, Guid sceneId, ScenePatchRequest request, VideoStudioDbContext db) =>
{
    var scene = await db.Scenes.FirstOrDefaultAsync(s => s.Id == sceneId && s.ProjectId == projectId);
    if (scene is null)
    {
        return Results.NotFound();
    }

    if (request.SceneIndex is > 0) { scene.Index = request.SceneIndex.Value; scene.Order = request.SceneIndex.Value; }
    if (request.Title is not null) { scene.Title = request.Title; }
    if (request.Summary is not null) { scene.Summary = request.Summary; scene.Description = request.Summary; }
    if (request.Location is not null) { scene.Location = request.Location; }
    if (request.Mood is not null) { scene.Mood = request.Mood; }
    if (request.TargetDurationSeconds is > 0) { scene.EstimatedDurationSeconds = request.TargetDurationSeconds.Value; }

    await db.Projects.Where(p => p.Id == projectId).ExecuteUpdateAsync(updates => updates.SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow));
    await db.SaveChangesAsync();
    return Results.Ok(new { scene.Id, scene.ProjectId, sceneIndex = scene.Index, scene.Title, scene.Summary, scene.Location, scene.Mood, targetDurationSeconds = scene.EstimatedDurationSeconds });
});

app.MapPatch("/api/projects/{projectId:guid}/scenes/{sceneId:guid}/shots/{shotId:guid}", async (Guid projectId, Guid sceneId, Guid shotId, ShotPatchRequest request, VideoStudioDbContext db) =>
{
    var shot = await db.Shots.FirstOrDefaultAsync(s => s.Id == shotId && s.SceneId == sceneId && s.ProjectId == projectId);
    if (shot is null)
    {
        return Results.NotFound();
    }

    if (request.SceneId is Guid newSceneId && newSceneId != shot.SceneId)
    {
        var sceneExists = await db.Scenes.AnyAsync(s => s.Id == newSceneId && s.ProjectId == projectId);
        if (!sceneExists) { return Results.BadRequest("Target scene does not exist for this project."); }
        shot.SceneId = newSceneId;
    }
    if (request.ShotIndex is > 0) { shot.Index = request.ShotIndex.Value; shot.Order = request.ShotIndex.Value; }
    if (request.Title is not null) { shot.ShotType = request.Title; }
    if (request.Description is not null) { shot.Action = request.Description; }
    if (request.VisualPrompt is not null) { shot.Prompt = request.VisualPrompt; }
    if (request.NegativePrompt is not null) { shot.NegativePrompt = request.NegativePrompt; }
    if (request.CameraDirection is not null) { shot.ShotType = request.CameraDirection; }
    if (request.MotionDirection is not null) { shot.CameraMotion = request.MotionDirection; }
    if (request.TargetDurationSeconds is > 0) { shot.DurationSeconds = request.TargetDurationSeconds.Value; }
    if (request.ContinuityNotes is not null) { shot.ContinuityNotes = request.ContinuityNotes; }
    if (request.StartImagePath is not null) { shot.StartImagePath = request.StartImagePath; shot.StartImageUrl = TryBuildMediaUrl(request.StartImagePath, "assets", projectId); }

    await db.Projects.Where(p => p.Id == projectId).ExecuteUpdateAsync(updates => updates.SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow));
    await db.SaveChangesAsync();
    return Results.Ok(new
    {
        shot.Id,
        shot.ProjectId,
        shot.SceneId,
        shotIndex = shot.Index,
        title = shot.ShotType,
        description = shot.Action,
        visualPrompt = shot.Prompt,
        shot.NegativePrompt,
        motionDirection = shot.CameraMotion,
        targetDurationSeconds = shot.DurationSeconds,
        shot.ContinuityNotes,
        shot.StartImagePrompt,
        shot.StartImageNegativePrompt,
        shot.StartImageStatus,
        shot.StartImagePath,
        shot.StartImageUrl
    });
});

app.MapGet("/api/projects/{id:guid}/dialogue-lines", async (Guid id, VideoStudioDbContext db) =>
{
    var exists = await db.Projects.AnyAsync(p => p.Id == id);
    if (!exists)
    {
        return Results.NotFound();
    }

    var lineEntities = await db.DialogueLines
        .Where(d => d.ProjectId == id)
        .Include(d => d.Scene)
        .OrderBy(d => d.Scene!.Index)
        .ThenBy(d => d.EstimatedStartSecond)
        .ToListAsync();
    var lines = lineEntities
        .Select(d => new DialogueLineDtoResponse(
            d.Id,
            d.SceneId,
            d.Scene != null ? d.Scene.Index : 0,
            d.Speaker,
            d.Text,
            d.Emotion,
            d.EstimatedStartSecond,
            d.EstimatedEndSecond,
            d.AudioPath,
            TryBuildMediaUrl(d.AudioPath, "audio", id)))
        .ToList();

    return Results.Ok(lines);
});

app.MapGet("/api/diagnostics/ollama", async (OllamaStoryPlanner planner, CancellationToken cancellationToken) =>
{
    return Results.Ok(await planner.CheckHealthAsync(cancellationToken));
});

app.MapPost("/api/projects/{id:guid}/render", async (Guid id, RenderRequestDto? request, VideoStudioDbContext db, PromptCompiler promptCompiler) =>
{
    var project = await db.Projects
        .Include(p => p.Characters)
        .Include(p => p.Scenes).ThenInclude(s => s.Shots)
        .Include(p => p.RenderJobs)
        .FirstOrDefaultAsync(p => p.Id == id);
    if (project is null)
    {
        return Results.NotFound();
    }

    var preset = request?.Preset ?? RenderPreset.FastPreview;
    var maxShotsDefault = preset == RenderPreset.FastPreview ? 1 : 3;
    var maxShots = request?.MaxShots is > 0 ? request.MaxShots.Value : maxShotsDefault;
    var force = request?.Force ?? false;
    var useCharacterReferenceInPrompt = (request?.UseCharacterReferenceInPrompt ?? false) || (request?.UseCharacterReference ?? false);
    var useShotStartImage = request?.UseShotStartImage ?? false;
    var shots = project.Scenes
        .OrderBy(s => s.Index)
        .SelectMany(s => s.Shots.OrderBy(sh => sh.Index))
        .Where(sh => request?.ShotIds is null || request.ShotIds.Count == 0 || request.ShotIds.Contains(sh.Id))
        .Where(sh => request?.SceneIndex is null || sh.Scene!.Index == request.SceneIndex.Value)
        .Where(sh => request?.ShotIndex is null || sh.Index == request.ShotIndex.Value)
        .ToList();
    if (shots.Count == 0)
    {
        return Results.BadRequest("Project has no shots to render.");
    }

    var settings = GetPresetSettings(preset);
    var hasExplicitTarget = request?.ShotIds?.Count > 0 || request?.SceneIndex is not null || request?.ShotIndex is not null;
    var limitedShots = hasExplicitTarget ? shots : shots.Take(maxShots).ToList();
    var queuedShotDtos = new List<RenderQueuedShotDto>();
    var jobs = new List<RenderJob>();
    foreach (var shot in limitedShots)
    {
        if (!force)
        {
            var hasExisting = project.RenderJobs.Any(j => j.ShotId == shot.Id && (j.Status == RenderJobStatus.Pending || j.Status == RenderJobStatus.Rendering));
            if (hasExisting)
            {
                continue;
            }
        }

        var compiled = promptCompiler.Compile(project, shot.Scene!, shot, project.Characters, preset, useCharacterReferenceInPrompt);
        var inputImagePath = useShotStartImage ? shot.StartImagePath : null;
        var generationMode = string.IsNullOrWhiteSpace(inputImagePath)
            ? VideoGenerationMode.TextToVideo
            : VideoGenerationMode.ImageToVideo;
        jobs.Add(new RenderJob
        {
            ProjectId = project.Id,
            SceneId = shot.SceneId,
            ShotId = shot.Id,
            JobType = RenderJobType.RenderVideo,
            Preset = preset,
            GenerationMode = generationMode,
            Prompt = shot.Prompt,
            CompiledPrompt = compiled.prompt,
            NegativePrompt = compiled.negativePrompt,
            Size = settings.size,
            FrameNum = settings.frameNum,
            SampleSteps = settings.sampleSteps,
            Seed = null,
            InputImagePath = inputImagePath,
            InputVideoPath = shot.InputVideoPath,
            InputAudioPath = shot.InputAudioPath,
            Status = RenderJobStatus.Pending
        });
        queuedShotDtos.Add(new RenderQueuedShotDto(shot.Id, shot.Scene!.Index, shot.Index));
    }

    if (jobs.Count == 0)
    {
        return Results.Ok(new RenderQueuedDto(project.Id, 0, preset, maxShots, queuedShotDtos));
    }

    db.RenderJobs.AddRange(jobs);
    foreach (var shot in limitedShots)
    {
        shot.Status = ShotStatus.Queued;
    }
    project.Status = ProjectStatus.Rendering;
    project.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();

    return Results.Accepted($"/api/projects/{id}/render-status", new RenderQueuedDto(project.Id, jobs.Count, preset, maxShots, queuedShotDtos));
});

app.MapPost("/api/projects/{id:guid}/audio/generate", async (Guid id, GenerateAudioRequest request, VideoStudioDbContext db) =>
{
    var project = await db.Projects.Include(p => p.DialogueLines).Include(p => p.RenderJobs).FirstOrDefaultAsync(p => p.Id == id);
    if (project is null)
    {
        return Results.NotFound();
    }

    var lines = project.DialogueLines.OrderBy(d => d.CreatedAt).ToList();
    if (lines.Count == 0)
    {
        return Results.BadRequest("Project has no dialogue lines.");
    }

    var jobs = new List<RenderJob>();
    foreach (var line in lines)
    {
        var hasAudio = !string.IsNullOrWhiteSpace(line.AudioPath);
        var hasPending = project.RenderJobs.Any(j => j.DialogueLineId == line.Id && j.JobType == RenderJobType.GenerateAudio && (j.Status == RenderJobStatus.Pending || j.Status == RenderJobStatus.Rendering));
        if (!request.Force && (hasAudio || hasPending))
        {
            continue;
        }

        var outputPath = Path.GetFullPath(Path.Combine("../../storage/audio", id.ToString(), $"{line.Id}.mp3"));
        jobs.Add(new RenderJob
        {
            ProjectId = id,
            SceneId = line.SceneId,
            DialogueLineId = line.Id,
            JobType = RenderJobType.GenerateAudio,
            Preset = RenderPreset.FastPreview,
            GenerationMode = VideoGenerationMode.SpeechToVideo,
            Prompt = line.Text,
            TextContent = line.Text,
            Speaker = line.Speaker,
            Emotion = line.Emotion,
            Language = request.Language,
            Voice = request.Voice,
            OutputPath = outputPath,
            Status = RenderJobStatus.Pending
        });
    }

    if (jobs.Count == 0)
    {
        return Results.Ok(new { createdJobs = 0 });
    }

    db.RenderJobs.AddRange(jobs);
    project.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Accepted($"/api/projects/{id}/dialogue-lines", new { createdJobs = jobs.Count });
});

app.MapPost("/api/projects/{id:guid}/visuals/generate-character-references", async (Guid id, VisualGenerationRequest? request, VideoStudioDbContext db, IOptions<StorageOptions> storageOptions) =>
{
    var project = await db.Projects.Include(p => p.Characters).Include(p => p.RenderJobs).FirstOrDefaultAsync(p => p.Id == id);
    if (project is null)
    {
        return Results.NotFound();
    }

    var force = request?.Force ?? false;
    var storageRoot = ResolveStorageRoot(storageOptions.Value.RootPath);
    var jobs = new List<RenderJob>();
    var requestedCharacterIds = request?.CharacterIds is { Count: > 0 } ? request.CharacterIds.ToHashSet() : null;
    foreach (var character in project.Characters.OrderBy(c => c.Name).Where(c => requestedCharacterIds is null || requestedCharacterIds.Contains(c.Id)))
    {
        character.ReferenceImagePrompt = RequiredText(character.ReferenceImagePrompt, BuildCharacterReferencePrompt(project, character));
        character.ReferenceImageNegativePrompt = RequiredText(character.ReferenceImageNegativePrompt, ProductionPlanNormalizer.DefaultImageNegativePrompt());
        var hasImage = !string.IsNullOrWhiteSpace(character.ReferenceImagePath);
        var hasPending = project.RenderJobs.Any(j => j.CharacterId == character.Id && j.JobType == RenderJobType.GenerateCharacterReferenceImage && (j.Status == RenderJobStatus.Pending || j.Status == RenderJobStatus.Rendering));
        if (!force && (hasImage || hasPending))
        {
            continue;
        }

        var outputPath = Path.GetFullPath(Path.Combine(storageRoot, "assets", id.ToString(), "characters", character.Id.ToString(), "reference.png"));
        jobs.Add(new RenderJob
        {
            ProjectId = id,
            CharacterId = character.Id,
            JobType = RenderJobType.GenerateCharacterReferenceImage,
            Preset = RenderPreset.FastPreview,
            GenerationMode = VideoGenerationMode.TextToVideo,
            Prompt = character.ReferenceImagePrompt,
            NegativePrompt = character.ReferenceImageNegativePrompt,
            Size = "1280*704",
            OutputPath = outputPath,
            Status = RenderJobStatus.Pending
        });
        character.ReferenceStatus = "Queued";
    }

    if (jobs.Count == 0)
    {
        await db.SaveChangesAsync();
        return Results.Ok(new { createdJobs = 0, message = "No missing character reference images to queue." });
    }

    db.RenderJobs.AddRange(jobs);
    project.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Accepted($"/api/projects/{id}/render-jobs", new { createdJobs = jobs.Count, jobIds = jobs.Select(j => j.Id).ToList() });
});

app.MapPost("/api/projects/{id:guid}/visuals/generate-shot-start-images", async (Guid id, VisualGenerationRequest? request, VideoStudioDbContext db, IOptions<StorageOptions> storageOptions) =>
{
    var project = await db.Projects
        .Include(p => p.Characters)
        .Include(p => p.Scenes).ThenInclude(s => s.Shots)
        .Include(p => p.RenderJobs)
        .FirstOrDefaultAsync(p => p.Id == id);
    if (project is null)
    {
        return Results.NotFound();
    }

    var force = request?.Force ?? false;
    var storageRoot = ResolveStorageRoot(storageOptions.Value.RootPath);
    var jobs = new List<RenderJob>();
    var requestedShotIds = request?.ShotIds is { Count: > 0 } ? request.ShotIds.ToHashSet() : null;
    foreach (var scene in project.Scenes.OrderBy(s => s.Index))
    {
        foreach (var shot in scene.Shots.OrderBy(s => s.Index).Where(s => requestedShotIds is null || requestedShotIds.Contains(s.Id)))
        {
            shot.StartImagePrompt = RequiredText(shot.StartImagePrompt, BuildShotStartImagePrompt(project, scene, shot));
            shot.StartImageNegativePrompt = RequiredText(shot.StartImageNegativePrompt, ProductionPlanNormalizer.DefaultImageNegativePrompt());
            var hasImage = !string.IsNullOrWhiteSpace(shot.StartImagePath);
            var hasPending = project.RenderJobs.Any(j => j.ShotId == shot.Id && j.JobType == RenderJobType.GenerateShotStartImage && (j.Status == RenderJobStatus.Pending || j.Status == RenderJobStatus.Rendering));
            if (!force && (hasImage || hasPending))
            {
                continue;
            }

            var outputPath = Path.GetFullPath(Path.Combine(storageRoot, "assets", id.ToString(), "shots", shot.Id.ToString(), "start.png"));
            jobs.Add(new RenderJob
            {
                ProjectId = id,
                SceneId = scene.Id,
                ShotId = shot.Id,
                JobType = RenderJobType.GenerateShotStartImage,
                Preset = RenderPreset.FastPreview,
                GenerationMode = VideoGenerationMode.TextToVideo,
                Prompt = shot.StartImagePrompt,
                NegativePrompt = shot.StartImageNegativePrompt,
                Size = "1280*704",
                OutputPath = outputPath,
                Status = RenderJobStatus.Pending
            });
            shot.StartImageStatus = "Queued";
        }
    }

    if (jobs.Count == 0)
    {
        await db.SaveChangesAsync();
        return Results.Ok(new { createdJobs = 0, message = "No missing shot start images to queue." });
    }

    db.RenderJobs.AddRange(jobs);
    project.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Accepted($"/api/projects/{id}/render-jobs", new { createdJobs = jobs.Count, jobIds = jobs.Select(j => j.Id).ToList() });
});

app.MapPost("/api/projects/{id:guid}/assemble", async (Guid id, AssembleRequest? request, VideoStudioDbContext db) =>
{
    var project = await db.Projects.Include(p => p.RenderJobs).FirstOrDefaultAsync(p => p.Id == id);
    if (project is null)
    {
        return Results.NotFound();
    }

    var force = request?.Force ?? false;
    var outputPath = Path.GetFullPath(Path.Combine("../../storage/finals", id.ToString(), "assembled.mp4"));
    if (!force && File.Exists(outputPath))
    {
        return Results.Ok(new { createdJobId = (Guid?)null, localPath = outputPath, mediaUrl = TryBuildMediaUrl(outputPath, "finals", id), alreadyExists = true });
    }

    var existingActiveJob = project.RenderJobs
        .Where(j => j.JobType == RenderJobType.AssembleVideo && (j.Status == RenderJobStatus.Pending || j.Status == RenderJobStatus.Rendering))
        .OrderByDescending(j => j.CreatedAt)
        .FirstOrDefault();
    if (!force && existingActiveJob is not null)
    {
        return Results.Ok(new { createdJobId = existingActiveJob.Id, reusedExistingJob = true, localPath = existingActiveJob.OutputPath, mediaUrl = TryBuildMediaUrl(existingActiveJob.OutputPath, "finals", id) });
    }

    var completedShotVideos = await db.RenderJobs
        .Where(j => j.ProjectId == id && j.JobType == RenderJobType.RenderVideo && j.Status == RenderJobStatus.Completed && !string.IsNullOrWhiteSpace(j.OutputPath))
        .Include(j => j.Scene)
        .Include(j => j.Shot)
        .OrderBy(j => j.Scene != null ? j.Scene.Index : int.MaxValue)
        .ThenBy(j => j.Shot != null ? j.Shot.Index : int.MaxValue)
        .Select(j => j.OutputPath!)
        .ToListAsync();

    if (completedShotVideos.Count == 0)
    {
        return Results.BadRequest("No completed shot videos are available to assemble.");
    }

    var job = new RenderJob
    {
        ProjectId = id,
        JobType = RenderJobType.AssembleVideo,
        Preset = RenderPreset.FastPreview,
        GenerationMode = VideoGenerationMode.VideoToVideo,
        Prompt = "Assemble completed shot renders into full movie.",
        InputVideoPath = JsonSerializer.Serialize(completedShotVideos),
        OutputPath = outputPath,
        Status = RenderJobStatus.Pending
    };
    db.RenderJobs.Add(job);
    await db.SaveChangesAsync();
    return Results.Accepted($"/api/projects/{id}/render-jobs", new { createdJobId = job.Id, videoCount = completedShotVideos.Count, localPath = outputPath, mediaUrl = TryBuildMediaUrl(outputPath, "finals", id) });
});

app.MapPost("/api/projects/{id:guid}/finalize", async (Guid id, FinalizeRequest? request, VideoStudioDbContext db) =>
{
    var project = await db.Projects.Include(p => p.DialogueLines).Include(p => p.RenderJobs).FirstOrDefaultAsync(p => p.Id == id);
    if (project is null)
    {
        return Results.NotFound();
    }

    var assembledPath = Path.GetFullPath(Path.Combine("../../storage/finals", id.ToString(), "assembled.mp4"));
    var videoPath = File.Exists(assembledPath)
        ? assembledPath
        : project.RenderJobs
        .Where(j => j.JobType == RenderJobType.RenderVideo && j.Status == RenderJobStatus.Completed && !string.IsNullOrWhiteSpace(j.OutputPath))
        .OrderByDescending(j => j.FinishedAt)
        .Select(j => j.OutputPath)
        .FirstOrDefault();
    var audioPath = project.DialogueLines.Where(d => !string.IsNullOrWhiteSpace(d.AudioPath)).OrderBy(d => d.EstimatedStartSecond).Select(d => d.AudioPath).FirstOrDefault();
    if (string.IsNullOrWhiteSpace(videoPath) || string.IsNullOrWhiteSpace(audioPath))
    {
        return Results.BadRequest("A completed video and generated dialogue audio are required.");
    }

    var force = request?.Force ?? false;
    var existingActiveJob = project.RenderJobs
        .Where(j => j.JobType == RenderJobType.MuxAudio && (j.Status == RenderJobStatus.Pending || j.Status == RenderJobStatus.Rendering))
        .OrderByDescending(j => j.CreatedAt)
        .FirstOrDefault();
    if (!force && existingActiveJob is not null)
    {
        return Results.Ok(new
        {
            createdJobId = existingActiveJob.Id,
            reusedExistingJob = true,
            finalVideoExists = false,
            mediaUrl = TryBuildMediaUrl(existingActiveJob.OutputPath, "finals", id)
        });
    }

    var existingFinal = await db.FinalVideos.FirstOrDefaultAsync(v => v.ProjectId == id);
    if (!force && existingFinal is not null && !string.IsNullOrWhiteSpace(existingFinal.Path))
    {
        return Results.Ok(new
        {
            createdJobId = (Guid?)null,
            reusedExistingJob = false,
            finalVideoExists = true,
            localPath = existingFinal.Path,
            mediaUrl = TryBuildMediaUrl(existingFinal.Path, "finals", id)
        });
    }

    var outputPath = Path.GetFullPath(Path.Combine("../../storage/finals", id.ToString(), "final-preview.mp4"));
    var job = new RenderJob
    {
        ProjectId = id,
        JobType = RenderJobType.MuxAudio,
        Preset = RenderPreset.FastPreview,
        GenerationMode = VideoGenerationMode.SpeechToVideo,
        InputVideoPath = videoPath,
        InputAudioPath = audioPath,
        OutputPath = outputPath,
        Status = RenderJobStatus.Pending
    };
    db.RenderJobs.Add(job);
    await db.SaveChangesAsync();
    return Results.Accepted($"/api/projects/{id}/render-jobs", new { createdJobId = job.Id });
});

app.MapGet("/api/projects/{id:guid}/render-status", async (Guid id, VideoStudioDbContext db) =>
{
    var project = await db.Projects.Include(p => p.RenderJobs).FirstOrDefaultAsync(p => p.Id == id);
    if (project is null)
    {
        return Results.NotFound();
    }

    var jobs = project.RenderJobs;
    return Results.Ok(new RenderStatusDto(
        project.Id,
        project.Status,
        jobs.Count,
        jobs.Count(j => j.Status == RenderJobStatus.Pending),
        jobs.Count(j => j.Status == RenderJobStatus.Rendering),
        jobs.Count(j => j.Status == RenderJobStatus.Completed),
        jobs.Count(j => j.Status == RenderJobStatus.Failed)));
});

app.MapGet("/api/projects/{id:guid}/render-jobs", async (Guid id, VideoStudioDbContext db) =>
{
    var exists = await db.Projects.AnyAsync(p => p.Id == id);
    if (!exists)
    {
        return Results.NotFound();
    }

    var jobEntities = await db.RenderJobs
        .Where(j => j.ProjectId == id)
        .Include(j => j.Scene)
        .Include(j => j.Shot)
        .OrderByDescending(j => j.CreatedAt)
        .ToListAsync();
    var jobs = jobEntities
        .Select(j => new ProjectRenderJobDetailsDto(
            j.Id,
            j.Scene != null ? j.Scene.Index : null,
            j.Shot != null ? j.Shot.Index : null,
            j.JobType,
            j.JobType.ToString(),
            j.GenerationMode,
            j.GenerationMode.ToString(),
            j.Status,
            j.Progress,
            j.Preset,
            j.InputImagePath,
            TryBuildMediaUrl(j.InputImagePath, "assets", j.ProjectId),
            j.OutputPath,
            TryBuildMediaUrl(j.OutputPath, j.JobType == RenderJobType.GenerateAudio ? "audio" : (j.JobType == RenderJobType.MuxAudio || j.JobType == RenderJobType.AssembleVideo ? "finals" : (j.JobType == RenderJobType.GenerateCharacterReferenceImage || j.JobType == RenderJobType.GenerateShotStartImage ? "assets" : "renders")), j.ProjectId),
            j.ErrorMessage,
            j.CreatedAt,
            j.StartedAt,
            j.FinishedAt))
        .ToList();

    return Results.Ok(jobs);
});

app.MapPost("/api/projects/{projectId:guid}/characters/{characterId:guid}/reference-image", async (Guid projectId, Guid characterId, IFormFile file, VideoStudioDbContext db, IOptions<StorageOptions> storageOptions) =>
{
    if (file.Length == 0)
    {
        return Results.BadRequest("Uploaded file is empty.");
    }

    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".webp")
    {
        return Results.BadRequest("Only .png, .jpg, .jpeg, and .webp reference images are allowed.");
    }

    var character = await db.Characters.FirstOrDefaultAsync(c => c.Id == characterId && c.ProjectId == projectId);
    if (character is null)
    {
        return Results.NotFound("Character was not found for this project.");
    }

    var storageRoot = ResolveStorageRoot(storageOptions.Value.RootPath);
    var targetDir = Path.Combine(storageRoot, "assets", projectId.ToString(), "characters", characterId.ToString());
    Directory.CreateDirectory(targetDir);
    var fileName = $"reference{extension}";
    var storagePath = Path.GetFullPath(Path.Combine(targetDir, fileName));

    await using (var stream = File.Create(storagePath))
    {
        await file.CopyToAsync(stream);
    }

    var mediaUrl = $"/media/assets/{projectId}/characters/{characterId}/{fileName}";
    var asset = new Asset
    {
        ProjectId = projectId,
        CharacterId = characterId,
        Type = AssetType.CharacterReference,
        OriginalFileName = file.FileName,
        FileName = fileName,
        StoragePath = storagePath,
        Path = storagePath,
        MediaUrl = mediaUrl,
        ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? GetMediaContentType(extension) : file.ContentType,
        SizeBytes = file.Length
    };

    db.Assets.Add(asset);
    character.ReferenceImagePath = storagePath;
    character.ReferenceImageUrl = mediaUrl;
    character.ReferenceAsset = asset;
    character.ReferenceStatus = "Uploaded";
    await db.SaveChangesAsync();

    return Results.Ok(new CharacterReferenceImageDto(character.Id, storagePath, mediaUrl));
}).DisableAntiforgery();

app.MapPost("/api/projects/{projectId:guid}/scenes/{sceneId:guid}/shots/{shotId:guid}/start-image", async (Guid projectId, Guid sceneId, Guid shotId, IFormFile file, VideoStudioDbContext db, IOptions<StorageOptions> storageOptions) =>
{
    if (file.Length == 0)
    {
        return Results.BadRequest("Uploaded file is empty.");
    }

    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".webp")
    {
        return Results.BadRequest("Only .png, .jpg, .jpeg, and .webp start images are allowed.");
    }

    var shot = await db.Shots
        .Include(s => s.Scene)
        .FirstOrDefaultAsync(s => s.Id == shotId && s.SceneId == sceneId && s.ProjectId == projectId && s.Scene!.ProjectId == projectId);
    if (shot is null)
    {
        return Results.NotFound("Shot was not found for this project and scene.");
    }

    var storageRoot = ResolveStorageRoot(storageOptions.Value.RootPath);
    var targetDir = Path.Combine(storageRoot, "assets", projectId.ToString(), "shots", shotId.ToString());
    Directory.CreateDirectory(targetDir);
    var fileName = $"start{extension}";
    var storagePath = Path.GetFullPath(Path.Combine(targetDir, fileName));

    await using (var stream = File.Create(storagePath))
    {
        await file.CopyToAsync(stream);
    }

    var mediaUrl = $"/media/assets/{projectId}/shots/{shotId}/{fileName}";
    var asset = new Asset
    {
        ProjectId = projectId,
        ShotId = shotId,
        Type = AssetType.InputImage,
        OriginalFileName = file.FileName,
        FileName = fileName,
        StoragePath = storagePath,
        Path = storagePath,
        MediaUrl = mediaUrl,
        ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? GetMediaContentType(extension) : file.ContentType,
        SizeBytes = file.Length
    };

    db.Assets.Add(asset);
    shot.StartImagePath = storagePath;
    shot.StartImageUrl = mediaUrl;
    shot.StartImageAsset = asset;
    shot.StartImageStatus = "Uploaded";
    await db.SaveChangesAsync();

    return Results.Ok(new ShotStartImageDto(shot.Id, storagePath, mediaUrl));
}).DisableAntiforgery();

app.MapPost("/api/assets/upload", async (IFormFile file, Guid? projectId, VideoStudioDbContext db, IOptions<StorageOptions> storageOptions) =>
{
    if (file.Length == 0)
    {
        return Results.BadRequest("Uploaded file is empty.");
    }

    var assetsDir = Path.Combine(storageOptions.Value.RootPath, "assets");
    Directory.CreateDirectory(assetsDir);
    var storedFileName = $"{Guid.NewGuid()}_{Path.GetFileName(file.FileName)}";
    var path = Path.Combine(assetsDir, storedFileName);

    await using (var stream = File.Create(path))
    {
        await file.CopyToAsync(stream);
    }

    var asset = new Asset
    {
        ProjectId = projectId,
        FileName = file.FileName,
        ContentType = file.ContentType,
        Path = path,
        SizeBytes = file.Length
    };
    db.Assets.Add(asset);
    await db.SaveChangesAsync();

    return Results.Created($"/api/assets/{asset.Id}", new AssetDto(asset.Id, asset.ProjectId, asset.FileName, asset.ContentType, asset.Path, asset.SizeBytes));
}).DisableAntiforgery();

app.MapGet("/api/assets/{id:guid}", async (Guid id, VideoStudioDbContext db) =>
{
    var asset = await db.Assets.FindAsync(id);
    return asset is null ? Results.NotFound() : Results.Ok(new AssetDto(asset.Id, asset.ProjectId, asset.FileName, asset.ContentType, asset.Path, asset.SizeBytes));
});

app.MapGet("/api/projects/{id:guid}/final-video", async (Guid id, VideoStudioDbContext db) =>
{
    var finalVideo = await db.FinalVideos.FirstOrDefaultAsync(v => v.ProjectId == id);
    var assembledPath = Path.GetFullPath(Path.Combine("../../storage/finals", id.ToString(), "assembled.mp4"));
    var hasAssembled = File.Exists(assembledPath);
    if (finalVideo is null && !hasAssembled)
    {
        return Results.NotFound("No final video exists for this project.");
    }

    return Results.Ok(new FinalVideoDto(
        finalVideo?.Id,
        id,
        finalVideo?.Path,
        TryBuildMediaUrl(finalVideo?.Path, "finals", id),
        hasAssembled ? assembledPath : null,
        hasAssembled ? TryBuildMediaUrl(assembledPath, "finals", id) : null));
});

app.MapGet("/api/projects/{id:guid}/media", async (Guid id, VideoStudioDbContext db) =>
{
    var exists = await db.Projects.AnyAsync(p => p.Id == id);
    if (!exists)
    {
        return Results.NotFound();
    }

    var shotVideos = new List<ProjectMediaItemDto>();
    var shotStartImages = new List<ProjectMediaItemDto>();
    var characterReferences = new List<ProjectMediaItemDto>();
    var audioFiles = new List<ProjectMediaItemDto>();
    ProjectMediaItemDto? assembledVideo = null;
    ProjectMediaItemDto? finalVideoItem = null;

    var renderAndFinalJobs = await db.RenderJobs
        .Where(j => j.ProjectId == id && j.Status == RenderJobStatus.Completed && !string.IsNullOrWhiteSpace(j.OutputPath))
        .OrderByDescending(j => j.FinishedAt)
        .ToListAsync();

    foreach (var job in renderAndFinalJobs)
    {
        var bucket = job.JobType == RenderJobType.MuxAudio || job.JobType == RenderJobType.AssembleVideo ? "finals" :
            job.JobType == RenderJobType.GenerateAudio ? "audio" : "renders";
        var type = job.JobType == RenderJobType.MuxAudio ? "final" :
            job.JobType == RenderJobType.AssembleVideo ? "assembled" :
            job.JobType == RenderJobType.GenerateAudio ? "audio" : "render";
        var url = TryBuildMediaUrl(job.OutputPath, bucket, id);
        var fileName = GetSafeFileName(job.OutputPath);
        if (url is null || fileName is null || string.IsNullOrWhiteSpace(job.OutputPath))
        {
            continue;
        }

        var item = new ProjectMediaItemDto(type, job.Id, fileName, job.OutputPath, url);
        if (job.JobType == RenderJobType.GenerateAudio)
        {
            audioFiles.Add(item);
        }
        else if (job.JobType == RenderJobType.AssembleVideo)
        {
            assembledVideo = item;
        }
        else if (job.JobType == RenderJobType.MuxAudio)
        {
            finalVideoItem = item;
        }
        else
        {
            shotVideos.Add(item);
        }
    }

    var dialogueLines = await db.DialogueLines
        .Where(d => d.ProjectId == id && !string.IsNullOrWhiteSpace(d.AudioPath))
        .OrderBy(d => d.EstimatedStartSecond)
        .ToListAsync();

    foreach (var line in dialogueLines)
    {
        var url = TryBuildMediaUrl(line.AudioPath, "audio", id);
        var fileName = GetSafeFileName(line.AudioPath);
        if (url is null || fileName is null || string.IsNullOrWhiteSpace(line.AudioPath))
        {
            continue;
        }

        audioFiles.Add(new ProjectMediaItemDto("audio", null, fileName, line.AudioPath, url));
    }

    var assets = await db.Assets.Where(a => a.ProjectId == id && a.MediaUrl != null && a.StoragePath != "").ToListAsync();
    foreach (var asset in assets)
    {
        var fileName = GetSafeFileName(asset.StoragePath);
        if (fileName is null || asset.MediaUrl is null)
        {
            continue;
        }

        var item = new ProjectMediaItemDto(asset.Type.ToString(), null, fileName, asset.StoragePath, asset.MediaUrl);
        if (asset.Type == AssetType.CharacterReference)
        {
            characterReferences.Add(item);
        }
        else if (asset.ShotId is not null)
        {
            shotStartImages.Add(item);
        }
    }

    var finalVideo = await db.FinalVideos.FirstOrDefaultAsync(v => v.ProjectId == id);
    if (finalVideo is not null)
    {
        var url = TryBuildMediaUrl(finalVideo.Path, "finals", id);
        var fileName = GetSafeFileName(finalVideo.Path);
        if (url is not null && fileName is not null)
        {
            finalVideoItem = new ProjectMediaItemDto("final", null, fileName, finalVideo.Path, url);
        }
    }

    var assembledPath = Path.GetFullPath(Path.Combine("../../storage/finals", id.ToString(), "assembled.mp4"));
    if (File.Exists(assembledPath))
    {
        assembledVideo = new ProjectMediaItemDto("assembled", null, "assembled.mp4", assembledPath, TryBuildMediaUrl(assembledPath, "finals", id)!);
    }

    return Results.Ok(new ProjectMediaSummaryDto(shotVideos, shotStartImages, characterReferences, audioFiles, assembledVideo, finalVideoItem));
});

app.MapGet("/media/renders/{projectId:guid}/{fileName}", async (Guid projectId, string fileName, IOptions<StorageOptions> storageOptions) =>
{
    return await ServeMediaFileAsync(projectId, fileName, "renders", storageOptions.Value.RootPath, [".mp4", ".webm"]);
});

app.MapGet("/media/audio/{projectId:guid}/{fileName}", async (Guid projectId, string fileName, IOptions<StorageOptions> storageOptions) =>
{
    return await ServeMediaFileAsync(projectId, fileName, "audio", storageOptions.Value.RootPath, [".mp3", ".wav", ".m4a"]);
});

app.MapGet("/media/finals/{projectId:guid}/{fileName}", async (Guid projectId, string fileName, IOptions<StorageOptions> storageOptions) =>
{
    return await ServeMediaFileAsync(projectId, fileName, "finals", storageOptions.Value.RootPath, [".mp4", ".webm"]);
});

app.MapGet("/media/assets/{projectId:guid}/characters/{characterId:guid}/{fileName}", async (Guid projectId, Guid characterId, string fileName, IOptions<StorageOptions> storageOptions) =>
{
    return await ServeNestedAssetAsync(projectId, "characters", characterId, fileName, storageOptions.Value.RootPath);
});

app.MapGet("/media/assets/{projectId:guid}/shots/{shotId:guid}/{fileName}", async (Guid projectId, Guid shotId, string fileName, IOptions<StorageOptions> storageOptions) =>
{
    return await ServeNestedAssetAsync(projectId, "shots", shotId, fileName, storageOptions.Value.RootPath);
});

app.MapPost("/api/worker/jobs/next", async (VideoStudioDbContext db) =>
{
    await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
    var job = await db.RenderJobs
        .Where(j => j.Status == RenderJobStatus.Pending)
        .OrderBy(j => j.CreatedAt)
        .FirstOrDefaultAsync();

    if (job is null)
    {
        return Results.NoContent();
    }

    job.Status = RenderJobStatus.Rendering;
    job.StartedAt = DateTimeOffset.UtcNow;
    job.Progress = Math.Max(job.Progress, 1);
    await db.SaveChangesAsync();
    await transaction.CommitAsync();

    return Results.Ok(ToRenderJobDto(job));
});

app.MapGet("/api/worker/jobs/{id:guid}", async (Guid id, VideoStudioDbContext db) =>
{
    var job = await db.RenderJobs.FindAsync(id);
    return job is null ? Results.NotFound() : Results.Ok(ToRenderJobDto(job));
});

app.MapPost("/api/worker/jobs/{id:guid}/start", async (Guid id, VideoStudioDbContext db) =>
{
    var job = await db.RenderJobs.FindAsync(id);
    if (job is null)
    {
        return Results.NotFound();
    }

    job.Status = RenderJobStatus.Rendering;
    job.StartedAt ??= DateTimeOffset.UtcNow;
    job.Progress = Math.Max(job.Progress, 1);
    await UpdateShotStatusAsync(db, job, ShotStatus.Rendering);
    await db.SaveChangesAsync();
    return Results.Ok(ToRenderJobDto(job));
});

app.MapPost("/api/worker/jobs/{id:guid}/complete", async (Guid id, CompleteRenderJobRequest request, VideoStudioDbContext db) =>
{
    var job = await db.RenderJobs.FindAsync(id);
    if (job is null)
    {
        return Results.NotFound();
    }

    job.Status = RenderJobStatus.Completed;
    job.Progress = 100;
    job.OutputPath = request.OutputPath;
    job.ErrorMessage = null;
    job.FinishedAt = DateTimeOffset.UtcNow;
    if (job.JobType == RenderJobType.GenerateAudio && job.DialogueLineId is Guid dialogueLineId)
    {
        var dialogueLine = await db.DialogueLines.FindAsync(dialogueLineId);
        if (dialogueLine is not null)
        {
            dialogueLine.AudioPath = request.OutputPath;
        }
    }
    if (job.JobType == RenderJobType.GenerateCharacterReferenceImage && job.CharacterId is Guid characterId)
    {
        var character = await db.Characters.FindAsync(characterId);
        if (character is not null)
        {
            var mediaUrl = TryBuildMediaUrl(request.OutputPath, "assets", job.ProjectId);
            character.ReferenceImagePath = request.OutputPath;
            character.ReferenceImageUrl = mediaUrl;
            character.ReferenceStatus = "Generated";
            var asset = CreateGeneratedAsset(job.ProjectId, characterId, null, AssetType.CharacterReference, request.OutputPath, mediaUrl);
            db.Assets.Add(asset);
            character.ReferenceAsset = asset;
        }
    }
    if (job.JobType == RenderJobType.GenerateShotStartImage && job.ShotId is Guid generatedShotId)
    {
        var shot = await db.Shots.FindAsync(generatedShotId);
        if (shot is not null)
        {
            var mediaUrl = TryBuildMediaUrl(request.OutputPath, "assets", job.ProjectId);
            shot.StartImagePath = request.OutputPath;
            shot.StartImageUrl = mediaUrl;
            shot.StartImageStatus = "Generated";
            var asset = CreateGeneratedAsset(job.ProjectId, null, generatedShotId, AssetType.InputImage, request.OutputPath, mediaUrl);
            db.Assets.Add(asset);
            shot.StartImageAsset = asset;
        }
    }
    if (job.JobType == RenderJobType.MuxAudio && !string.IsNullOrWhiteSpace(request.OutputPath))
    {
        var existingFinal = await db.FinalVideos.FirstOrDefaultAsync(v => v.ProjectId == job.ProjectId);
        if (existingFinal is null)
        {
            db.FinalVideos.Add(new FinalVideo { ProjectId = job.ProjectId, Path = request.OutputPath });
        }
        else
        {
            existingFinal.Path = request.OutputPath;
            existingFinal.CreatedAt = DateTimeOffset.UtcNow;
        }
    }
    await UpdateShotStatusAsync(db, job, ShotStatus.Completed);
    await db.SaveChangesAsync();
    return Results.Ok(ToRenderJobDto(job));
});

app.MapPost("/api/worker/jobs/{id:guid}/fail", async (Guid id, FailRenderJobRequest request, VideoStudioDbContext db) =>
{
    var job = await db.RenderJobs.FindAsync(id);
    if (job is null)
    {
        return Results.NotFound();
    }

    job.Status = RenderJobStatus.Failed;
    job.ErrorMessage = request.ErrorMessage;
    job.FinishedAt = DateTimeOffset.UtcNow;
    if (job.JobType == RenderJobType.GenerateCharacterReferenceImage && job.CharacterId is Guid characterId)
    {
        await db.Characters
            .Where(c => c.Id == characterId)
            .ExecuteUpdateAsync(updates => updates.SetProperty(c => c.ReferenceStatus, "Failed"));
    }
    if (job.JobType == RenderJobType.GenerateShotStartImage && job.ShotId is Guid failedShotId)
    {
        await db.Shots
            .Where(s => s.Id == failedShotId)
            .ExecuteUpdateAsync(updates => updates.SetProperty(s => s.StartImageStatus, "Failed"));
    }
    await UpdateShotStatusAsync(db, job, ShotStatus.Failed);
    await db.SaveChangesAsync();
    return Results.Ok(ToRenderJobDto(job));
});

app.MapPost("/api/worker/jobs/{id:guid}/progress", async (Guid id, ProgressRenderJobRequest request, VideoStudioDbContext db) =>
{
    var job = await db.RenderJobs.FindAsync(id);
    if (job is null)
    {
        return Results.NotFound();
    }

    job.Progress = Math.Clamp(request.Progress, 0, 100);
    await db.SaveChangesAsync();
    return Results.Ok(ToRenderJobDto(job));
});

app.MapPost("/api/worker/jobs/reset-stale", async (ResetStaleJobsRequest request, VideoStudioDbContext db) =>
{
    var olderThanMinutes = request.OlderThanMinutes.GetValueOrDefault(60);
    var targetStatus = request.SetStatus ?? RenderJobStatus.Pending;
    if (targetStatus is not RenderJobStatus.Pending and not RenderJobStatus.Failed)
    {
        return Results.BadRequest("setStatus must be Pending or Failed.");
    }

    var threshold = DateTimeOffset.UtcNow.AddMinutes(-olderThanMinutes);
    var staleQuery = db.RenderJobs.Where(j => j.Status == RenderJobStatus.Rendering && j.StartedAt != null && j.StartedAt < threshold);
    var updated = targetStatus == RenderJobStatus.Failed
        ? await staleQuery.ExecuteUpdateAsync(updates => updates
            .SetProperty(j => j.Status, RenderJobStatus.Failed)
            .SetProperty(j => j.ErrorMessage, "Marked failed by reset-stale endpoint."))
        : await staleQuery.ExecuteUpdateAsync(updates => updates
            .SetProperty(j => j.Status, RenderJobStatus.Pending));

    return Results.Ok(new { resetCount = updated, setStatus = targetStatus.ToString(), olderThanMinutes });
});

app.MapPost("/api/projects/{id:guid}/jobs/cleanup", async (Guid id, VideoStudioDbContext db) =>
{
    var exists = await db.Projects.AnyAsync(p => p.Id == id);
    if (!exists)
    {
        return Results.NotFound();
    }

    var candidates = await db.RenderJobs
        .Where(j => j.ProjectId == id && (j.Status == RenderJobStatus.Pending || j.Status == RenderJobStatus.Failed))
        .OrderByDescending(j => j.CreatedAt)
        .ToListAsync();

    var removed = new List<RenderJob>();
    var keptCount = 0;

    var groups = candidates.GroupBy(j => new { j.JobType, j.ShotId, j.DialogueLineId, j.SceneId });
    foreach (var group in groups)
    {
        var groupList = group.ToList();
        if (groupList.Count <= 1)
        {
            keptCount += groupList.Count;
            continue;
        }

        keptCount += 1;
        removed.AddRange(groupList.Skip(1));
    }

    if (removed.Count > 0)
    {
        db.RenderJobs.RemoveRange(removed);
        await db.SaveChangesAsync();
    }

    return Results.Ok(new
    {
        removedJobs = removed.Count,
        keptJobs = keptCount
    });
});

app.Run();

static ProjectSummaryDto ToSummary(Project project) => new(project.Id, project.Name, project.StoryText, project.TargetDurationSeconds, project.Status, project.CreatedAt, project.UpdatedAt);

static ProjectDetailsDto ToDetails(Project project) => new(project.Id, project.Name, project.StoryText, project.TargetDurationSeconds, project.Status, project.Logline, project.Genre, project.CreatedAt, project.UpdatedAt);

static RenderJobDto ToRenderJobDto(RenderJob job) => new(job.Id, job.ProjectId, job.SceneId, job.ShotId, job.CharacterId, job.DialogueLineId, job.JobType, job.Preset, job.GenerationMode, job.GenerationMode.ToString(), job.Prompt, job.CompiledPrompt, job.NegativePrompt, job.Size, job.FrameNum, job.SampleSteps, job.Seed, job.InputImagePath, TryBuildMediaUrl(job.InputImagePath, "assets", job.ProjectId), job.InputVideoPath, job.InputAudioPath, job.TextContent, job.Speaker, job.Emotion, job.Language, job.Voice, job.OutputPath, job.Status, job.Progress, job.ErrorMessage);

static Task<int> MarkProjectStatusAsync(VideoStudioDbContext db, Guid projectId, ProjectStatus status, CancellationToken cancellationToken)
{
    return db.Projects
        .Where(p => p.Id == projectId)
        .ExecuteUpdateAsync(updates => updates
            .SetProperty(p => p.Status, status)
            .SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
}

static PreproductionDto BuildPreproductionDto(Project project)
{
    var missing = new List<string>();
    var warnings = new List<string>();
    var characterJobs = project.RenderJobs
        .Where(j => j.JobType == RenderJobType.GenerateCharacterReferenceImage && j.CharacterId is not null)
        .GroupBy(j => j.CharacterId!.Value)
        .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.CreatedAt).First());
    var shotJobs = project.RenderJobs
        .Where(j => j.JobType == RenderJobType.GenerateShotStartImage && j.ShotId is not null)
        .GroupBy(j => j.ShotId!.Value)
        .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.CreatedAt).First());
    var characters = project.Characters
        .OrderBy(c => c.Name)
        .Select(c =>
        {
            if (string.IsNullOrWhiteSpace(c.ReferenceImagePrompt))
            {
                missing.Add($"Character '{c.Name}' is missing a reference image prompt.");
            }
            if (string.IsNullOrWhiteSpace(c.ReferenceImagePath))
            {
                warnings.Add($"Character '{c.Name}' has no reference image yet.");
            }

            characterJobs.TryGetValue(c.Id, out var job);
            return new PreproductionCharacterDto(c.Id, c.Name, c.Role, c.VisualPrompt, c.ReferenceImagePrompt, c.ReferenceImageNegativePrompt, c.ReferenceStatus, c.ReferenceImagePath, c.ReferenceImageUrl, job?.Id, job?.Status);
        })
        .ToList();

    var shots = project.Scenes
        .OrderBy(s => s.Index)
        .SelectMany(scene => scene.Shots.OrderBy(shot => shot.Index).Select(shot =>
        {
            if (string.IsNullOrWhiteSpace(shot.StartImagePrompt))
            {
                missing.Add($"Scene {scene.Index} shot {shot.Index} is missing a start image prompt.");
            }
            if (string.IsNullOrWhiteSpace(shot.StartImagePath))
            {
                warnings.Add($"Scene {scene.Index} shot {shot.Index} has no start image yet and will render as text-to-video unless one is uploaded or generated.");
            }

            shotJobs.TryGetValue(shot.Id, out var job);
            return new PreproductionShotDto(shot.Id, shot.SceneId, scene.Index, shot.Index, shot.ShotType, shot.Action, shot.StartImagePrompt, shot.StartImageNegativePrompt, shot.StartImageStatus, shot.StartImagePath, shot.StartImageUrl, job?.Id, job?.Status);
        }))
        .ToList();

    return new PreproductionDto(project.Id, project.Name, characters, shots, missing.Distinct().ToList(), warnings.Distinct().ToList());
}

static string BuildCharacterReferencePrompt(Project project, Character character)
{
    var style = RequiredText(project.VisualStylePrompt, "cinematic realism, coherent production design");
    return $"English character reference image for a story-to-video pipeline. Neutral cinematic background, clear face, natural full-body or waist-up framing, stable identity and clothing. Character: {character.Name}. Role: {character.Role}. Visual lock: {character.VisualPrompt}. Personality cue: {character.Personality}. Style: {style}. Soft cinematic lighting, realistic anatomy, no readable text, no logos.";
}

static string BuildShotStartImagePrompt(Project project, Scene scene, Shot shot)
{
    var style = RequiredText(project.VisualStylePrompt, "cinematic realism, coherent production design");
    var lighting = RequiredText(project.LightingStyle, "motivated cinematic lighting");
    return $"English keyframe image prompt for Wan2.2 image-to-video. Scene {scene.Index}: {scene.Title}. Environment: {scene.Location}, {scene.TimeOfDay}. Mood: {scene.Mood}. Composition: {shot.ShotType}. Character placement and action: {shot.Action}. Camera angle and motion intent: {shot.CameraMotion}. Lighting: {lighting}. Visual style: {style}. Historically and culturally consistent production design. No spoken dialogue, no readable text, no logos, no subtitles, no UI words.";
}

static string RequiredText(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

static Asset CreateGeneratedAsset(Guid projectId, Guid? characterId, Guid? shotId, AssetType type, string outputPath, string? mediaUrl)
{
    var fileName = Path.GetFileName(outputPath);
    var extension = Path.GetExtension(outputPath);
    return new Asset
    {
        ProjectId = projectId,
        CharacterId = characterId,
        ShotId = shotId,
        Type = type,
        OriginalFileName = fileName,
        FileName = fileName,
        StoragePath = outputPath,
        Path = outputPath,
        MediaUrl = mediaUrl,
        ContentType = GetMediaContentType(extension),
        SizeBytes = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0
    };
}

static async Task UpdateShotStatusAsync(VideoStudioDbContext db, RenderJob job, ShotStatus status)
{
    if (job.JobType != RenderJobType.RenderVideo)
    {
        return;
    }

    if (job.ShotId is not Guid shotId)
    {
        return;
    }

    var shot = await db.Shots.FindAsync(shotId);
    if (shot is null)
    {
        return;
    }

    shot.Status = status;
    if (status == ShotStatus.Completed)
    {
        shot.OutputPath = job.OutputPath ?? shot.OutputPath;
    }
}

static async Task<IResult> ServeMediaFileAsync(Guid projectId, string fileName, string bucket, string storageRootConfig, string[] allowedExtensions)
{
    if (!IsSafeMediaFileName(fileName))
    {
        return Results.BadRequest("Invalid file name.");
    }

    var extension = Path.GetExtension(fileName);
    if (string.IsNullOrWhiteSpace(extension) || !allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
    {
        return Results.BadRequest("File type is not allowed.");
    }

    var storageRoot = ResolveStorageRoot(storageRootConfig);
    var filePath = Path.GetFullPath(Path.Combine(storageRoot, bucket, projectId.ToString(), fileName));
    var expectedRoot = Path.GetFullPath(Path.Combine(storageRoot, bucket, projectId.ToString()));
    if (!filePath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("Invalid file path.");
    }

    if (!File.Exists(filePath))
    {
        return Results.NotFound();
    }

    return Results.File(filePath, GetMediaContentType(extension), enableRangeProcessing: true);
}

static async Task<IResult> ServeNestedAssetAsync(Guid projectId, string assetGroup, Guid assetOwnerId, string fileName, string storageRootConfig)
{
    if (!IsSafeMediaFileName(fileName))
    {
        return Results.BadRequest("Invalid file name.");
    }

    var extension = Path.GetExtension(fileName);
    if (string.IsNullOrWhiteSpace(extension) || extension.ToLowerInvariant() is not ".png" and not ".jpg" and not ".jpeg" and not ".webp")
    {
        return Results.BadRequest("File type is not allowed.");
    }

    var storageRoot = ResolveStorageRoot(storageRootConfig);
    var expectedRoot = Path.GetFullPath(Path.Combine(storageRoot, "assets", projectId.ToString(), assetGroup, assetOwnerId.ToString()));
    var filePath = Path.GetFullPath(Path.Combine(expectedRoot, fileName));
    if (!filePath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("Invalid file path.");
    }

    if (!File.Exists(filePath))
    {
        return Results.NotFound();
    }

    return Results.File(filePath, GetMediaContentType(extension));
}

static bool IsSafeMediaFileName(string fileName)
{
    if (string.IsNullOrWhiteSpace(fileName))
    {
        return false;
    }

    return !fileName.Contains("..", StringComparison.Ordinal)
           && !fileName.Contains('/', StringComparison.Ordinal)
           && !fileName.Contains('\\', StringComparison.Ordinal)
           && Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal);
}

static string GetMediaContentType(string extension)
{
    return extension.ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".m4a" => "audio/mp4",
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };
}

static string ResolveStorageRoot(string storageRootConfig)
{
    if (Path.IsPathRooted(storageRootConfig))
    {
        return Path.GetFullPath(storageRootConfig);
    }

    return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), storageRootConfig));
}

static string? GetSafeFileName(string? localPath)
{
    if (string.IsNullOrWhiteSpace(localPath))
    {
        return null;
    }

    var fileName = Path.GetFileName(localPath);
    return IsSafeMediaFileName(fileName) ? fileName : null;
}

static string? TryBuildMediaUrl(string? localPath, string bucket, Guid projectId)
{
    if (string.IsNullOrWhiteSpace(localPath))
    {
        return null;
    }

    var fileName = GetSafeFileName(localPath);
    if (fileName is null)
    {
        return null;
    }

    var extension = Path.GetExtension(fileName).ToLowerInvariant();
    if (bucket == "assets")
    {
        var parts = localPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var charactersIndex = Array.FindIndex(parts, p => string.Equals(p, "characters", StringComparison.OrdinalIgnoreCase));
        if (charactersIndex >= 0 && charactersIndex + 1 < parts.Length && Guid.TryParse(parts[charactersIndex + 1], out var characterId)
            && extension is ".png" or ".jpg" or ".jpeg" or ".webp")
        {
            return $"/media/assets/{projectId}/characters/{characterId}/{fileName}";
        }
        var shotsIndex = Array.FindIndex(parts, p => string.Equals(p, "shots", StringComparison.OrdinalIgnoreCase));
        if (shotsIndex >= 0 && shotsIndex + 1 < parts.Length && Guid.TryParse(parts[shotsIndex + 1], out var shotId)
            && extension is ".png" or ".jpg" or ".jpeg" or ".webp")
        {
            return $"/media/assets/{projectId}/shots/{shotId}/{fileName}";
        }

        return null;
    }

    var allowed = bucket switch
    {
        "renders" => extension is ".mp4" or ".webm",
        "audio" => extension is ".mp3" or ".wav" or ".m4a",
        "finals" => extension is ".mp4" or ".webm",
        _ => false
    };
    if (!allowed)
    {
        return null;
    }

    return $"/media/{bucket}/{projectId}/{fileName}";
}

static (string size, int frameNum, int sampleSteps) GetPresetSettings(RenderPreset preset)
{
    return preset switch
    {
        RenderPreset.FastPreview => ("1280*704", 25, 5),
        RenderPreset.Preview => ("1280*704", 49, 10),
        RenderPreset.Final => ("1280*704", 121, 25),
        _ => ("1280*704", 25, 5)
    };
}
