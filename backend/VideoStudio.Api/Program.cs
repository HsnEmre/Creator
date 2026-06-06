using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

await LogRenderJobDurationMetadataSchemaAsync(app.Services, app.Logger);

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
        TargetDurationSeconds = request.TargetDurationSeconds is > 0 ? request.TargetDurationSeconds.Value : 60,
        QualityGoal = string.IsNullOrWhiteSpace(request.QualityGoal) ? "Balanced" : request.QualityGoal.Trim()
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
        result = await planner.CreatePlanAsync(snapshot.Id, snapshot.Name, snapshot.StoryText, snapshot.TargetDurationSeconds, cancellationToken);
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
                .SetProperty(p => p.DirectorTreatment, projectUpdate.DirectorTreatment)
                .SetProperty(p => p.BeatSheetJson, projectUpdate.BeatSheetJson)
                .SetProperty(p => p.ActBreakdownJson, projectUpdate.ActBreakdownJson)
                .SetProperty(p => p.CharacterBibleJson, projectUpdate.CharacterBibleJson)
                .SetProperty(p => p.LocationBibleJson, projectUpdate.LocationBibleJson)
                .SetProperty(p => p.TimelineContinuityJson, projectUpdate.TimelineContinuityJson)
                .SetProperty(p => p.VisualContinuityRulesJson, projectUpdate.VisualContinuityRulesJson)
                .SetProperty(p => p.RenderStrategyRecommendationJson, projectUpdate.RenderStrategyRecommendationJson)
                .SetProperty(p => p.QualityGoal, projectUpdate.QualityGoal)
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

app.MapPost("/api/projects/{id:guid}/preproduction/prepare", async (Guid id, VideoStudioDbContext db, ILogger<Program> logger) =>
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
            var compiled = BuildShotStartImagePrompt(project, scene, shot, project.Characters, logger);
            shot.StartImagePrompt = IsInvalidKeyframePrompt(shot.StartImagePrompt)
                ? compiled.Prompt
                : shot.StartImagePrompt;
            shot.StartImageNegativePrompt = IsInvalidKeyframeNegativePrompt(shot.StartImageNegativePrompt)
                ? compiled.NegativePrompt
                : shot.StartImageNegativePrompt;
            shot.StartImageStatus = string.IsNullOrWhiteSpace(shot.StartImagePath) ? "PromptReady" : "Ready";
        }
    }

    project.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();

    return Results.Ok(BuildPreproductionDto(project));
});

app.MapPost("/api/projects/{id:guid}/storyboard/prompts/repair", async (Guid id, VideoStudioDbContext db, ILogger<Program> logger) =>
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
        return Results.BadRequest("Analyze the story before repairing storyboard prompts.");
    }

    var repaired = 0;
    foreach (var scene in project.Scenes.OrderBy(s => s.Index))
    {
        foreach (var shot in scene.Shots.OrderBy(s => s.Index))
        {
            var compiled = BuildShotStartImagePrompt(project, scene, shot, project.Characters, logger);
            shot.StartImagePrompt = compiled.Prompt;
            shot.StartImageNegativePrompt = compiled.NegativePrompt;
            shot.CharacterLockPrompt = compiled.CharacterLock;
            shot.LocationLockPrompt = compiled.LocationLock;
            shot.SceneAnchorPrompt = compiled.SceneAnchor;
            shot.StartImageStatus = string.IsNullOrWhiteSpace(shot.StartImagePath) ? "PromptReady" : "Ready";
            repaired++;
        }
    }

    project.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new
    {
        repairedShots = repaired,
        preproduction = BuildPreproductionDto(project)
    });
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
    if (!string.IsNullOrWhiteSpace(character.ReferenceImagePrompt))
    {
        character.VisualPrompt = character.ReferenceImagePrompt;
        character.ContinuityRulesJson = JsonSerializer.Serialize(new[]
        {
            $"Canonical visual lock: {character.ReferenceImagePrompt}",
            "Use the same face, age, hair, beard, clothing, silhouette, and signature props in every storyboard prompt.",
            "Forbidden drift: different face, different costume, missing signature props, modern clothing, unrelated accessories."
        });
    }
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

    var renderJobs = await db.RenderJobs
        .Where(j => j.ProjectId == id && j.JobType == RenderJobType.RenderVideo && j.ShotId != null)
        .ToListAsync();
    var latestSuccessfulRenders = LatestCompletedRendersByShot(renderJobs);

    var shotEntities = await db.Shots
        .Where(s => s.ProjectId == id)
        .Include(s => s.Scene)
        .OrderBy(s => s.Scene!.Index)
        .ThenBy(s => s.Index)
        .ToListAsync();

    var shots = shotEntities
        .Select(s =>
        {
            latestSuccessfulRenders.TryGetValue(s.Id, out var latestRender);
            return new
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
                outputPath = latestRender?.OutputPath ?? s.OutputPath,
                latestRenderJobId = latestRender?.Id,
                latestRenderStatus = latestRender?.Status,
                latestRenderStatusName = latestRender?.Status.ToString(),
                latestRenderGenerationMode = latestRender?.GenerationMode,
                latestRenderGenerationModeName = latestRender?.GenerationMode.ToString(),
                latestRenderInputImagePath = latestRender?.InputImagePath,
                latestRenderInputImageUrl = TryBuildMediaUrl(latestRender?.InputImagePath, "assets", id),
                startImageUsedForLatestRender = latestRender?.GenerationMode == VideoGenerationMode.ImageToVideo && !string.IsNullOrWhiteSpace(latestRender.InputImagePath),
                textOnlyRenderFallback = latestRender is null || latestRender.GenerationMode == VideoGenerationMode.TextToVideo,
                latestRenderDurationSeconds = RenderDurationSeconds(latestRender),
                latestRenderOutputPath = latestRender?.OutputPath,
                latestRenderOutputUrl = TryBuildMediaUrl(latestRender?.OutputPath, "renders", id),
                latestRenderDurationMode = latestRender?.RenderDurationMode,
                latestRenderDurationModeName = latestRender?.RenderDurationMode.ToString(),
                latestRenderRequestedShotDurationSeconds = latestRender?.RequestedShotDurationSeconds,
                latestRenderRequestedFrameNum = latestRender?.RequestedFrameNum,
                latestRenderActualFrameNum = latestRender?.ActualFrameNum ?? latestRender?.FrameNum,
                latestRenderExpectedRawClipDurationSeconds = latestRender?.ExpectedRawClipDurationSeconds ?? ExpectedRawClipDurationSeconds(latestRender?.FrameNum),
                latestRenderProbedRawClipDurationSeconds = latestRender?.ProbedRawClipDurationSeconds,
                latestRenderRawDurationCoveragePercent = latestRender?.RawDurationCoveragePercent,
                latestRenderStartedAt = latestRender?.StartedAt,
                latestRenderFinishedAt = latestRender?.FinishedAt
            };
        })
        .ToList();

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

app.MapPost("/api/projects/{id:guid}/render", async (Guid id, RenderRequestDto? request, VideoStudioDbContext db, PromptCompiler promptCompiler, ILogger<Program> logger) =>
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
    var renderDurationMode = request?.RenderDurationMode ?? RenderDurationMode.FastPreview;
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

    var durationIssue = GetProjectDurationPlanIssue(project);
    if (durationIssue is not null)
    {
        return Results.BadRequest(new
        {
            error = "Storyboard is too short for the target duration. Regenerate or repair plan before rendering.",
            durationIssue
        });
    }

    var settings = GetPresetSettings(preset);
    var hasExplicitTarget = request?.ShotIds?.Count > 0 || request?.SceneIndex is not null || request?.ShotIndex is not null;
    var limitedShots = hasExplicitTarget ? shots : shots.Take(maxShots).ToList();
    var renderModeByShotId = limitedShots.ToDictionary(shot => shot.Id, shot => ResolveRenderDurationMode(renderDurationMode, shot));
    var strictI2vRequested = renderModeByShotId.Values.Any(RequiresImageToVideoKeyframes);
    if (strictI2vRequested && !useShotStartImage)
    {
        logger.LogWarning(
            "{RenderProfile}_i2v_required projectId={ProjectId} shotCount={ShotCount}",
            renderDurationMode.ToString().ToLowerInvariant(),
            project.Id,
            limitedShots.Count);
        if (renderModeByShotId.Values.Any(mode => mode == RenderDurationMode.ComfyUIParity))
        {
            logger.LogWarning(
                "comfyui_parity_i2v_required projectId={ProjectId} shotCount={ShotCount}",
                project.Id,
                limitedShots.Count);
        }
        return Results.BadRequest(new
        {
            error = $"{renderDurationMode} requires Image-to-Video from shot keyframes for final-quality motion. Enable useShotStartImage and generate or upload keyframes before queueing {renderDurationMode} renders."
        });
    }

    if (strictI2vRequested && useShotStartImage)
    {
        logger.LogInformation(
            "{RenderProfile}_keyframe_required projectId={ProjectId} shotCount={ShotCount}",
            renderDurationMode.ToString().ToLowerInvariant(),
            project.Id,
            limitedShots.Count);
        var missingLongMotionKeyframes = limitedShots
            .Where(shot => RequiresImageToVideoKeyframes(renderModeByShotId[shot.Id]))
            .Where(shot => string.IsNullOrWhiteSpace(shot.StartImagePath))
            .Select(shot => new RenderQueuedShotDto(shot.Id, shot.Scene!.Index, shot.Index))
            .ToList();
        if (missingLongMotionKeyframes.Count > 0)
        {
            foreach (var missingShot in missingLongMotionKeyframes)
            {
                logger.LogWarning(
                    "longmotion_keyframe_missing projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId}",
                    project.Id,
                    missingShot.SceneIndex,
                    missingShot.ShotIndex,
                    missingShot.ShotId);
            }

            return Results.BadRequest(new
            {
                error = $"{renderDurationMode} with Image-to-Video requires a shot keyframe for every selected shot. Generate or upload missing keyframes before queueing this profile.",
                missingShots = missingLongMotionKeyframes
            });
        }
    }
    var latestSuccessfulRenders = LatestCompletedRendersByShot(project.RenderJobs);
    var queuedShotDtos = new List<RenderQueuedShotDto>();
    var skippedShotDtos = new List<RenderQueuedShotDto>();
    var jobs = new List<RenderJob>();
    foreach (var shot in limitedShots)
    {
        if (!force)
        {
            var hasExisting = project.RenderJobs.Any(j => j.ShotId == shot.Id && (j.Status == RenderJobStatus.Pending || j.Status == RenderJobStatus.Rendering));
            if (hasExisting)
            {
                skippedShotDtos.Add(new RenderQueuedShotDto(shot.Id, shot.Scene!.Index, shot.Index));
                continue;
            }

            if (latestSuccessfulRenders.TryGetValue(shot.Id, out var latestSuccessfulRender) &&
                CompletedRenderSatisfiesRequestedMode(latestSuccessfulRender, renderModeByShotId[shot.Id]))
            {
                skippedShotDtos.Add(new RenderQueuedShotDto(shot.Id, shot.Scene!.Index, shot.Index));
                continue;
            }
        }

        var inputImagePath = useShotStartImage ? shot.StartImagePath : null;
        if (useShotStartImage && string.IsNullOrWhiteSpace(inputImagePath))
        {
            logger.LogWarning(
                "shot_start_image_missing projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId}",
                project.Id,
                shot.Scene!.Index,
                shot.Index,
                shot.Id);
        }
        if (!string.IsNullOrWhiteSpace(inputImagePath))
        {
            logger.LogInformation(
                "shot_start_image_selected projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} imagePath={ImagePath}",
                project.Id,
                shot.Scene!.Index,
                shot.Index,
                shot.Id,
                inputImagePath);
        }
        var generationMode = string.IsNullOrWhiteSpace(inputImagePath)
            ? VideoGenerationMode.TextToVideo
            : VideoGenerationMode.ImageToVideo;
        var effectiveRenderDurationMode = renderModeByShotId[shot.Id];
        var compiled = promptCompiler.Compile(
            project,
            shot.Scene!,
            shot,
            project.Characters,
            preset,
            useCharacterReferenceInPrompt,
            generationMode == VideoGenerationMode.ImageToVideo,
            effectiveRenderDurationMode);
        if (generationMode == VideoGenerationMode.ImageToVideo)
        {
            if (RequiresImageToVideoKeyframes(effectiveRenderDurationMode))
            {
                logger.LogInformation(
                    "{RenderProfile}_i2v_confirmed projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} startImagePath={StartImagePath}",
                    effectiveRenderDurationMode.ToString().ToLowerInvariant(),
                    project.Id,
                    shot.Scene!.Index,
                    shot.Index,
                    shot.Id,
                    inputImagePath);
            }
            logger.LogInformation(
                "shot_start_image_used_for_video projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} imagePath={ImagePath}",
                project.Id,
                shot.Scene!.Index,
                shot.Index,
                shot.Id,
                inputImagePath);
        }
        else
        {
            if (RequiresImageToVideoKeyframes(effectiveRenderDurationMode))
            {
                logger.LogWarning(
                    "{RenderProfile}_text_only_blocked projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} useShotStartImage={UseShotStartImage} reason={Reason}",
                    effectiveRenderDurationMode.ToString().ToLowerInvariant(),
                    project.Id,
                    shot.Scene!.Index,
                    shot.Index,
                    shot.Id,
                    useShotStartImage,
                    useShotStartImage ? "missing keyframe was blocked before queueing" : "user explicitly disabled Image-to-Video keyframes");
                if (effectiveRenderDurationMode == RenderDurationMode.ComfyUIParity)
                {
                    logger.LogWarning(
                        "comfyui_parity_text_only_blocked projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} useShotStartImage={UseShotStartImage}",
                        project.Id,
                        shot.Scene!.Index,
                        shot.Index,
                        shot.Id,
                        useShotStartImage);
                }
            }
            logger.LogInformation(
                "shot_start_image_not_supported_for_video projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} character_reference_image_not_used_by_video_backend={CharacterReferenceImageNotUsedByVideoBackend} reason={Reason}",
                project.Id,
                shot.Scene!.Index,
                shot.Index,
                shot.Id,
                useCharacterReferenceInPrompt && project.Characters.Any(c => !string.IsNullOrWhiteSpace(c.ReferenceImagePath)),
                "current render path only passes shot start images as Wan2.2 --image; character references are prompt identity guidance");
        }
        if (renderDurationMode == RenderDurationMode.AutoQuality)
        {
            logger.LogInformation(
                "autoquality_render_strategy_selected projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} recommendedRenderDurationMode={RenderDurationMode} assemblyExtensionAllowed={AssemblyExtensionAllowed}",
                project.Id,
                shot.Scene!.Index,
                shot.Index,
                shot.Id,
                effectiveRenderDurationMode,
                effectiveRenderDurationMode is RenderDurationMode.FastPreview or RenderDurationMode.CinematicPreview);
        }
        var durationSelection = SelectRenderDuration(effectiveRenderDurationMode, settings.frameNum, shot.DurationSeconds);
        var renderSize = SelectRenderSize(effectiveRenderDurationMode, settings.size, logger, project.Id, shot.Scene!.Index, shot.Index, shot.Id);
        var sampleSteps = SelectSampleSteps(effectiveRenderDurationMode, settings.sampleSteps);
        var renderJob = new RenderJob
        {
            ProjectId = project.Id,
            SceneId = shot.SceneId,
            ShotId = shot.Id,
            JobType = RenderJobType.RenderVideo,
            Preset = preset,
            RenderDurationMode = durationSelection.Mode,
            GenerationMode = generationMode,
            Prompt = shot.Prompt,
            CompiledPrompt = compiled.prompt,
            NegativePrompt = compiled.negativePrompt,
            Size = renderSize,
            FrameNum = durationSelection.ActualFrameNum,
            SampleSteps = sampleSteps,
            Seed = null,
            RequestedShotDurationSeconds = durationSelection.RequestedShotDurationSeconds,
            RequestedFrameNum = durationSelection.RequestedFrameNum,
            ActualFrameNum = durationSelection.ActualFrameNum,
            ExpectedRawClipDurationSeconds = durationSelection.ExpectedRawClipDurationSeconds,
            InputImagePath = inputImagePath,
            InputVideoPath = shot.InputVideoPath,
            InputAudioPath = shot.InputAudioPath,
            Status = RenderJobStatus.Pending
        };
        logger.LogInformation(
            "wan_render_duration_mode_selected projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} renderJobId={RenderJobId} targetShotDurationSeconds={TargetShotDurationSeconds} renderDurationMode={RenderDurationMode}",
            project.Id,
            shot.Scene!.Index,
            shot.Index,
            shot.Id,
            renderJob.Id,
            durationSelection.RequestedShotDurationSeconds,
            durationSelection.Mode);
        logger.LogInformation(
            "wan_render_frame_count_selected projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} renderJobId={RenderJobId} renderDurationMode={RenderDurationMode} requestedFrameNum={RequestedFrameNum} actualFrameNum={ActualFrameNum}",
            project.Id,
            shot.Scene!.Index,
            shot.Index,
            shot.Id,
            renderJob.Id,
            durationSelection.Mode,
            durationSelection.RequestedFrameNum,
            durationSelection.ActualFrameNum);
        if (durationSelection.WasClamped)
        {
            logger.LogWarning(
                "wan_render_frame_count_clamped projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} renderJobId={RenderJobId} targetShotDurationSeconds={TargetShotDurationSeconds} requestedFrameNum={RequestedFrameNum} actualFrameNum={ActualFrameNum} maxFrameNum={MaxFrameNum}",
                project.Id,
                shot.Scene!.Index,
                shot.Index,
                shot.Id,
                renderJob.Id,
                durationSelection.RequestedShotDurationSeconds,
                durationSelection.RequestedFrameNum,
                durationSelection.ActualFrameNum,
                RenderDurationMaxFrameNum());
        }
        if (effectiveRenderDurationMode == RenderDurationMode.ComfyUIParity)
        {
            logger.LogInformation(
                "wan_comfyui_parity_profile_selected projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} renderJobId={RenderJobId} size={Size} requestedFrameNum={RequestedFrameNum} actualFrameNum={ActualFrameNum} sampleSteps={SampleSteps} cfg={Cfg} sampler={Sampler} scheduler={Scheduler} shift={Shift} expectedRawClipDurationSeconds={ExpectedRawClipDurationSeconds}",
                project.Id,
                shot.Scene!.Index,
                shot.Index,
                shot.Id,
                renderJob.Id,
                renderJob.Size,
                durationSelection.RequestedFrameNum,
                durationSelection.ActualFrameNum,
                renderJob.SampleSteps,
                ComfyUIParityGuideScale(),
                ComfyUIParitySampleSolver(),
                "unsupported_by_wan_cli",
                ComfyUIParitySampleShift(),
                durationSelection.ExpectedRawClipDurationSeconds);
        }
        logger.LogInformation(
            "wan_render_expected_duration projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} renderJobId={RenderJobId} targetShotDurationSeconds={TargetShotDurationSeconds} renderDurationMode={RenderDurationMode} requestedFrameNum={RequestedFrameNum} actualFrameNum={ActualFrameNum} expectedRawClipDurationSeconds={ExpectedRawClipDurationSeconds}",
            project.Id,
            shot.Scene!.Index,
            shot.Index,
            shot.Id,
            renderJob.Id,
            durationSelection.RequestedShotDurationSeconds,
            durationSelection.Mode,
            durationSelection.RequestedFrameNum,
            durationSelection.ActualFrameNum,
            durationSelection.ExpectedRawClipDurationSeconds);
        if (renderJob.GenerationMode == VideoGenerationMode.ImageToVideo)
        {
            logger.LogInformation(
                "shot_start_image_used_for_video projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} renderJobId={RenderJobId} imagePath={ImagePath}",
                project.Id,
                shot.Scene!.Index,
                shot.Index,
                shot.Id,
                renderJob.Id,
                inputImagePath);
        }
        jobs.Add(renderJob);
        queuedShotDtos.Add(new RenderQueuedShotDto(
            shot.Id,
            shot.Scene!.Index,
            shot.Index,
            durationSelection.Mode.ToString(),
            durationSelection.RequestedShotDurationSeconds,
            durationSelection.RequestedFrameNum,
            durationSelection.ActualFrameNum,
            durationSelection.ExpectedRawClipDurationSeconds));
    }

    if (jobs.Count == 0)
    {
        return Results.Ok(new RenderQueuedDto(project.Id, 0, preset, maxShots, queuedShotDtos, skippedShotDtos.Count, skippedShotDtos));
    }

    db.RenderJobs.AddRange(jobs);
    var queuedShotIds = jobs.Where(j => j.ShotId is not null).Select(j => j.ShotId!.Value).ToHashSet();
    foreach (var shot in limitedShots.Where(shot => queuedShotIds.Contains(shot.Id)))
    {
        shot.Status = ShotStatus.Queued;
    }
    project.Status = ProjectStatus.Rendering;
    project.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();

    return Results.Accepted($"/api/projects/{id}/render-status", new RenderQueuedDto(project.Id, jobs.Count, preset, maxShots, queuedShotDtos, skippedShotDtos.Count, skippedShotDtos));
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

app.MapPost("/api/projects/{id:guid}/visuals/generate-shot-start-images", async (Guid id, VisualGenerationRequest? request, VideoStudioDbContext db, IOptions<StorageOptions> storageOptions, ILogger<Program> logger) =>
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
            var compiled = BuildShotStartImagePrompt(project, scene, shot, project.Characters, logger);
            shot.StartImagePrompt = IsInvalidKeyframePrompt(shot.StartImagePrompt) || force
                ? compiled.Prompt
                : shot.StartImagePrompt;
            shot.StartImageNegativePrompt = IsInvalidKeyframeNegativePrompt(shot.StartImageNegativePrompt) || force
                ? compiled.NegativePrompt
                : shot.StartImageNegativePrompt;
            shot.CharacterLockPrompt = compiled.CharacterLock;
            shot.LocationLockPrompt = compiled.LocationLock;
            shot.SceneAnchorPrompt = compiled.SceneAnchor;
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
                Prompt = shot.StartImagePrompt ?? compiled.Prompt,
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

app.MapPost("/api/projects/{id:guid}/assemble", async (Guid id, AssembleRequest? request, VideoStudioDbContext db, ILogger<Program> logger) =>
{
    var project = await db.Projects.Include(p => p.RenderJobs).FirstOrDefaultAsync(p => p.Id == id);
    if (project is null)
    {
        return Results.NotFound();
    }

    var force = request?.Force ?? false;
    var outputPath = Path.GetFullPath(Path.Combine("../../storage/finals", id.ToString(), "assembled.mp4"));
    var assemblyRules = GetAssemblyRules(project.TargetDurationSeconds);
    if (!force && !assemblyRules.IsLongForm && File.Exists(outputPath))
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

    var orderedShots = await db.Shots
        .Where(s => s.ProjectId == id)
        .Include(s => s.Scene)
        .OrderBy(s => s.Scene != null ? s.Scene.Index : int.MaxValue)
        .ThenBy(s => s.Index)
        .ToListAsync();
    var renderJobs = await db.RenderJobs
        .Where(j => j.ProjectId == id && j.JobType == RenderJobType.RenderVideo && j.ShotId != null)
        .ToListAsync();
    var latestSuccessfulRenders = LatestCompletedRendersByShot(renderJobs);
    var selectedShotRenders = orderedShots
        .Select(shot => latestSuccessfulRenders.TryGetValue(shot.Id, out var render) ? new { Shot = shot, Render = render } : null)
        .Where(item => item is not null)
        .Select(item => item!)
        .ToList();
    var completedShotVideos = selectedShotRenders
        .Select(item => item.Render.OutputPath!)
        .ToList();
    var assemblySegments = selectedShotRenders
        .Select(item => new
        {
            projectId = id,
            projectTargetDurationSeconds = project.TargetDurationSeconds,
            videoPath = item.Render.OutputPath,
            shotId = item.Shot.Id,
            sceneId = item.Shot.SceneId,
            sceneIndex = item.Shot.Scene?.Index ?? 0,
            shotIndex = item.Shot.Index,
            renderJobId = item.Render.Id,
            renderDurationMode = item.Render.RenderDurationMode.ToString(),
            expectedRawClipDurationSeconds = item.Render.ExpectedRawClipDurationSeconds ?? ExpectedRawClipDurationSeconds(item.Render.FrameNum),
            probedRawClipDurationSeconds = item.Render.ProbedRawClipDurationSeconds,
            rawDurationCoveragePercent = item.Render.RawDurationCoveragePercent,
            targetDurationSeconds = item.Shot.DurationSeconds
        })
        .ToList();
    var missingShotCount = orderedShots.Count - selectedShotRenders.Count;
    var totalTargetDurationSeconds = assemblySegments.Sum(segment => Math.Max(0, segment.targetDurationSeconds));

    if (completedShotVideos.Count == 0)
    {
        return Results.BadRequest("No completed shot videos are available to assemble.");
    }

    logger.LogInformation(
        "ffmpeg_assembly_plan_validation_started projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} totalTargetDurationSeconds={TotalTargetDurationSeconds} videoCount={VideoCount} shotCount={ShotCount} minShots={MinimumShots} minimumTargetDurationSeconds={MinimumTargetDurationSeconds}",
        id,
        project.TargetDurationSeconds,
        totalTargetDurationSeconds,
        assemblySegments.Count,
        orderedShots.Count,
        assemblyRules.MinimumShots,
        assemblyRules.MinimumTargetDurationSeconds);

    var hasMissingDurations = assemblySegments.Any(segment => segment.targetDurationSeconds <= 0);
    if (assemblyRules.IsLongForm &&
        (orderedShots.Count < assemblyRules.MinimumShots ||
         totalTargetDurationSeconds < assemblyRules.MinimumTargetDurationSeconds ||
         hasMissingDurations ||
         totalTargetDurationSeconds < 30))
    {
        logger.LogWarning(
            "ffmpeg_assembly_plan_validation_failed projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} totalTargetDurationSeconds={TotalTargetDurationSeconds} videoCount={VideoCount} shotCount={ShotCount} minShots={MinimumShots} minimumTargetDurationSeconds={MinimumTargetDurationSeconds} hasMissingDurations={HasMissingDurations}",
            id,
            project.TargetDurationSeconds,
            totalTargetDurationSeconds,
            assemblySegments.Count,
            orderedShots.Count,
            assemblyRules.MinimumShots,
            assemblyRules.MinimumTargetDurationSeconds,
            hasMissingDurations);
        return Results.BadRequest($"Storyboard is too short for assembly. Regenerate the plan before assembling: {orderedShots.Count} shots, {totalTargetDurationSeconds}s planned for {project.TargetDurationSeconds}s target.");
    }

    logger.LogInformation(
        "ffmpeg_assembly_plan_validation_completed projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} totalTargetDurationSeconds={TotalTargetDurationSeconds} videoCount={VideoCount}",
        id,
        project.TargetDurationSeconds,
        totalTargetDurationSeconds,
        assemblySegments.Count);

    logger.LogInformation(
        "Assembling project {ProjectId} with latest render jobs {RenderJobIds} for shots {ShotIds}. Missing shots: {MissingShotCount}",
        id,
        selectedShotRenders.Select(item => item.Render.Id).ToList(),
        selectedShotRenders.Select(item => item.Shot.Id).ToList(),
        missingShotCount);

    foreach (var segment in assemblySegments)
    {
        logger.LogInformation(
            "ffmpeg_assembly_selected_shot projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} renderJobId={RenderJobId} renderDurationMode={RenderDurationMode} expectedRawClipDurationSeconds={ExpectedRawClipDurationSeconds} probedRawClipDurationSeconds={ProbedRawClipDurationSeconds} rawDurationCoveragePercent={RawDurationCoveragePercent} targetDurationSeconds={TargetDurationSeconds} videoPath={VideoPath}",
            id,
            segment.sceneIndex,
            segment.shotIndex,
            segment.shotId,
            segment.renderJobId,
            segment.renderDurationMode,
            segment.expectedRawClipDurationSeconds,
            segment.probedRawClipDurationSeconds,
            segment.rawDurationCoveragePercent,
            segment.targetDurationSeconds,
            segment.videoPath);
    }

    logger.LogInformation(
        "ffmpeg_assembly_duration_plan projectId={ProjectId} videoCount={VideoCount} totalTargetDurationSeconds={TotalTargetDurationSeconds} missingShotCount={MissingShotCount}",
        id,
        assemblySegments.Count,
        totalTargetDurationSeconds,
        missingShotCount);

    var job = new RenderJob
    {
        ProjectId = id,
        JobType = RenderJobType.AssembleVideo,
        Preset = RenderPreset.FastPreview,
        GenerationMode = VideoGenerationMode.VideoToVideo,
        Prompt = "Assemble completed shot renders into full movie.",
        InputVideoPath = JsonSerializer.Serialize(assemblySegments),
        OutputPath = outputPath,
        Status = RenderJobStatus.Pending
    };
    db.RenderJobs.Add(job);
    await db.SaveChangesAsync();
    return Results.Accepted($"/api/projects/{id}/render-jobs", new
    {
        createdJobId = job.Id,
        videoCount = completedShotVideos.Count,
        missingShotCount,
        totalTargetDurationSeconds,
        selectedRenderJobIds = selectedShotRenders.Select(item => item.Render.Id).ToList(),
        selectedShotIds = selectedShotRenders.Select(item => item.Shot.Id).ToList(),
        localPath = outputPath,
        mediaUrl = TryBuildMediaUrl(outputPath, "finals", id)
    });
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
        .Include(j => j.Scene)
        .Include(j => j.Shot)
        .Include(j => j.Character)
        .Include(j => j.DialogueLine)
        .Where(j => j.ProjectId == id
            || (j.Scene != null && j.Scene.ProjectId == id)
            || (j.Shot != null && j.Shot.ProjectId == id)
            || (j.Character != null && j.Character.ProjectId == id)
            || (j.DialogueLine != null && j.DialogueLine.ProjectId == id))
        .OrderByDescending(j => j.CreatedAt)
        .ToListAsync();
    var latestSuccessfulRenderIds = LatestCompletedRendersByShot(jobEntities)
        .Values
        .Select(j => j.Id)
        .ToHashSet();
    var jobs = jobEntities
        .Select(j => new ProjectRenderJobDetailsDto(
            j.Id,
            id,
            j.SceneId ?? j.Scene?.Id,
            j.ShotId ?? j.Shot?.Id,
            j.Scene != null ? j.Scene.Index : null,
            j.Shot != null ? j.Shot.Index : null,
            j.JobType,
            j.JobType.ToString(),
            j.GenerationMode,
            j.GenerationMode.ToString(),
            j.Status,
            j.Status.ToString(),
            j.Progress,
            j.Preset,
            j.InputImagePath,
            TryBuildMediaUrl(j.InputImagePath, "assets", id),
            j.OutputPath,
            TryBuildMediaUrl(j.OutputPath, j.JobType == RenderJobType.GenerateAudio ? "audio" : (j.JobType == RenderJobType.MuxAudio || j.JobType == RenderJobType.AssembleVideo ? "finals" : (j.JobType == RenderJobType.GenerateCharacterReferenceImage || j.JobType == RenderJobType.GenerateShotStartImage ? "assets" : "renders")), id),
            j.ErrorMessage,
            j.CreatedAt,
            j.StartedAt,
            j.FinishedAt,
            RenderDurationSeconds(j),
            latestSuccessfulRenderIds.Contains(j.Id),
            j.RenderDurationMode.ToString(),
            j.RequestedShotDurationSeconds ?? j.Shot?.DurationSeconds,
            j.RequestedFrameNum ?? RequestedFrameNumForJob(j, j.Shot?.DurationSeconds),
            j.ActualFrameNum ?? j.FrameNum,
            j.ExpectedRawClipDurationSeconds ?? ExpectedRawClipDurationSeconds(j.FrameNum),
            j.ProbedRawClipDurationSeconds,
            j.RawDurationCoveragePercent))
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
        .Include(j => j.Scene)
        .Include(j => j.Shot)
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
    var job = await db.RenderJobs
        .Include(j => j.Scene)
        .Include(j => j.Shot)
        .FirstOrDefaultAsync(j => j.Id == id);
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
    if (request.ProbedRawClipDurationSeconds is double probedRawClipDurationSeconds)
    {
        job.ProbedRawClipDurationSeconds = Math.Round(Math.Max(0, probedRawClipDurationSeconds), 3);
        var target = job.RenderDurationMode == RenderDurationMode.ComfyUIParity
            ? job.ExpectedRawClipDurationSeconds.GetValueOrDefault()
            : job.RequestedShotDurationSeconds.GetValueOrDefault();
        job.RawDurationCoveragePercent = target > 0
            ? (int)Math.Round((job.ProbedRawClipDurationSeconds.Value / target) * 100)
            : null;
    }
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

app.MapPost("/api/projects/{id:guid}/jobs/cancel-active", async (Guid id, VideoStudioDbContext db) =>
{
    var exists = await db.Projects.AnyAsync(p => p.Id == id);
    if (!exists)
    {
        return Results.NotFound();
    }

    var now = DateTimeOffset.UtcNow;
    var canceled = await db.RenderJobs
        .Where(j => j.ProjectId == id && (j.Status == RenderJobStatus.Pending || j.Status == RenderJobStatus.Rendering))
        .ExecuteUpdateAsync(updates => updates
            .SetProperty(j => j.Status, RenderJobStatus.Canceled)
            .SetProperty(j => j.ErrorMessage, "Cancelled by development cleanup.")
            .SetProperty(j => j.FinishedAt, now));

    return Results.Ok(new
    {
        canceledJobs = canceled,
        status = RenderJobStatus.Canceled.ToString()
    });
});

app.MapPost("/api/projects/{id:guid}/director-plan/repair", RepairDirectorPlanAsync);
app.MapPost("/api/projects/{id:guid}/director-plan/regenerate", RepairDirectorPlanAsync);

app.Run();

static async Task<IResult> RepairDirectorPlanAsync(Guid id, VideoStudioDbContext db, ProductionPlanMapper mapper, ProductionPlanNormalizer normalizer, ILogger<Program> logger, CancellationToken cancellationToken)
{
    var projectSnapshot = await db.Projects
        .AsNoTracking()
        .Include(p => p.Characters)
        .Include(p => p.Scenes).ThenInclude(s => s.Shots)
        .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    if (projectSnapshot is null)
    {
        return Results.NotFound();
    }

    if (projectSnapshot.Scenes.Count == 0)
    {
        return Results.BadRequest("Analyze the story before repairing the director plan.");
    }

    var currentPlan = mapper.FromProject(projectSnapshot);
    var currentRules = GetProjectDurationRules(currentPlan.TargetDurationSeconds);
    var repairedPlan = normalizer.RepairDirectorDurationPlan(currentPlan, id);
    var repairedIssue = GetProductionPlanDurationIssue(repairedPlan);
    logger.LogInformation(
        "director_plan_regenerate_requested projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} plannedDurationSeconds={PlannedDurationSeconds} coveragePercent={CoveragePercent} sceneCount={SceneCount} shotCount={ShotCount} minimumScenes={MinimumScenes} minimumShots={MinimumShots} targetSceneRange={TargetSceneMin}-{TargetSceneMax} targetShotRange={TargetShotMin}-{TargetShotMax}",
        id,
        currentPlan.TargetDurationSeconds,
        currentPlan.TotalPlannedDurationSeconds,
        currentPlan.PlannedDurationCoveragePercent,
        currentPlan.SceneCount,
        currentPlan.ShotCount,
        currentRules.minimumScenes,
        currentRules.minimumShots,
        currentRules.targetSceneMin,
        currentRules.targetSceneMax,
        currentRules.targetShotMin,
        currentRules.targetShotMax);

    if (repairedIssue is not null)
    {
        return Results.BadRequest(new
        {
            error = "Repair plan did not produce a valid duration plan. Analyze the story again before rendering.",
            durationIssue = repairedIssue
        });
    }

    var oldSceneCount = projectSnapshot.Scenes.Count;
    var oldShotCount = projectSnapshot.Scenes.Sum(s => s.Shots.Count);
    var oldPlannedDurationSeconds = projectSnapshot.Scenes.Sum(s => s.Shots.Sum(sh => Math.Max(0, sh.DurationSeconds)));
    var newSceneCount = repairedPlan.SceneCount;
    var newShotCount = repairedPlan.ShotCount;
    var newPlannedDurationSeconds = repairedPlan.TotalPlannedDurationSeconds;
    var coveragePercent = repairedPlan.PlannedDurationCoveragePercent;
    logger.LogInformation(
        "director_duration_repair_started projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} oldSceneCount={OldSceneCount} oldShotCount={OldShotCount} newSceneCount={NewSceneCount} newShotCount={NewShotCount} oldPlannedDurationSeconds={OldPlannedDurationSeconds} newPlannedDurationSeconds={NewPlannedDurationSeconds} coveragePercent={CoveragePercent}",
        id,
        repairedPlan.TargetDurationSeconds,
        oldSceneCount,
        oldShotCount,
        newSceneCount,
        newShotCount,
        oldPlannedDurationSeconds,
        newPlannedDurationSeconds,
        coveragePercent);

    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    try
    {
        var now = DateTimeOffset.UtcNow;
        var projectExists = await db.Projects.AsNoTracking().AnyAsync(p => p.Id == id, cancellationToken);
        if (!projectExists)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Results.NotFound();
        }

        logger.LogInformation(
            "director_duration_repair_delete_old_storyboard_started projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} oldSceneCount={OldSceneCount} oldShotCount={OldShotCount} oldPlannedDurationSeconds={OldPlannedDurationSeconds}",
            id,
            repairedPlan.TargetDurationSeconds,
            oldSceneCount,
            oldShotCount,
            oldPlannedDurationSeconds);

        await db.RenderJobs
            .Where(j => j.ProjectId == id
                && j.Status != RenderJobStatus.Completed
                && (j.SceneId != null || j.ShotId != null || j.DialogueLineId != null))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(j => j.Status, RenderJobStatus.Canceled)
                .SetProperty(j => j.ErrorMessage, "Cancelled because the director plan was repaired.")
                .SetProperty(j => j.FinishedAt, now), cancellationToken);

        await db.RenderJobs
            .Where(j => j.ProjectId == id && (j.SceneId != null || j.ShotId != null || j.DialogueLineId != null))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(j => j.SceneId, (Guid?)null)
                .SetProperty(j => j.ShotId, (Guid?)null)
                .SetProperty(j => j.DialogueLineId, (Guid?)null), cancellationToken);

        await db.Assets
            .Where(a => a.ProjectId == id && a.ShotId != null)
            .ExecuteUpdateAsync(updates => updates.SetProperty(a => a.ShotId, (Guid?)null), cancellationToken);

        await db.DialogueLines.Where(d => d.ProjectId == id).ExecuteDeleteAsync(cancellationToken);
        await db.Shots.Where(s => s.ProjectId == id).ExecuteDeleteAsync(cancellationToken);
        await db.Scenes.Where(s => s.ProjectId == id).ExecuteDeleteAsync(cancellationToken);

        logger.LogInformation(
            "director_duration_repair_delete_old_storyboard_completed projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} oldSceneCount={OldSceneCount} oldShotCount={OldShotCount} oldPlannedDurationSeconds={OldPlannedDurationSeconds}",
            id,
            repairedPlan.TargetDurationSeconds,
            oldSceneCount,
            oldShotCount,
            oldPlannedDurationSeconds);

        db.ChangeTracker.Clear();

        var projectUpdate = mapper.BuildProjectUpdate(repairedPlan);
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
                .SetProperty(p => p.DirectorTreatment, projectUpdate.DirectorTreatment)
                .SetProperty(p => p.BeatSheetJson, projectUpdate.BeatSheetJson)
                .SetProperty(p => p.ActBreakdownJson, projectUpdate.ActBreakdownJson)
                .SetProperty(p => p.CharacterBibleJson, projectUpdate.CharacterBibleJson)
                .SetProperty(p => p.LocationBibleJson, projectUpdate.LocationBibleJson)
                .SetProperty(p => p.TimelineContinuityJson, projectUpdate.TimelineContinuityJson)
                .SetProperty(p => p.VisualContinuityRulesJson, projectUpdate.VisualContinuityRulesJson)
                .SetProperty(p => p.RenderStrategyRecommendationJson, projectUpdate.RenderStrategyRecommendationJson)
                .SetProperty(p => p.QualityGoal, projectUpdate.QualityGoal)
                .SetProperty(p => p.Status, ProjectStatus.ReadyForRender)
                .SetProperty(p => p.UpdatedAt, now), cancellationToken);

        if (updatedRows == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Results.NotFound();
        }

        UpsertDirectorPlanCharacters(id, repairedPlan, mapper, db);

        var scenes = mapper.BuildScenes(id, repairedPlan);
        var dialogueLines = mapper.BuildDialogueLines(id, repairedPlan, scenes);

        logger.LogInformation(
            "director_duration_repair_insert_new_storyboard_started projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} newSceneCount={NewSceneCount} newShotCount={NewShotCount} newPlannedDurationSeconds={NewPlannedDurationSeconds} coveragePercent={CoveragePercent}",
            id,
            repairedPlan.TargetDurationSeconds,
            newSceneCount,
            newShotCount,
            newPlannedDurationSeconds,
            coveragePercent);

        db.Scenes.AddRange(scenes);
        db.DialogueLines.AddRange(dialogueLines);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "director_duration_repair_insert_new_storyboard_completed projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} newSceneCount={NewSceneCount} newShotCount={NewShotCount} newPlannedDurationSeconds={NewPlannedDurationSeconds} coveragePercent={CoveragePercent}",
            id,
            repairedPlan.TargetDurationSeconds,
            newSceneCount,
            newShotCount,
            newPlannedDurationSeconds,
            coveragePercent);

        await transaction.CommitAsync(cancellationToken);
    }
    catch (DbUpdateConcurrencyException ex)
    {
        await transaction.RollbackAsync(cancellationToken);
        logger.LogWarning(
            ex,
            "director_duration_repair_concurrency_conflict projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} oldSceneCount={OldSceneCount} oldShotCount={OldShotCount} oldPlannedDurationSeconds={OldPlannedDurationSeconds}",
            id,
            repairedPlan.TargetDurationSeconds,
            oldSceneCount,
            oldShotCount,
            oldPlannedDurationSeconds);
        return Results.Conflict(new
        {
            error = "Repair plan failed because storyboard records changed during repair. Reload the project and try again."
        });
    }

    db.ChangeTracker.Clear();
    var savedProject = await db.Projects
        .Include(p => p.Characters)
        .Include(p => p.Scenes).ThenInclude(s => s.Shots)
        .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    if (savedProject is null)
    {
        return Results.NotFound();
    }

    var savedDurationIssue = GetProjectDurationPlanIssue(savedProject);
    if (savedDurationIssue is not null)
    {
        logger.LogWarning(
            "director_duration_validation_completed projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} newSceneCount={NewSceneCount} newShotCount={NewShotCount} newPlannedDurationSeconds={NewPlannedDurationSeconds} coveragePercent={CoveragePercent} isValid=false",
            id,
            savedProject.TargetDurationSeconds,
            savedProject.Scenes.Count,
            savedProject.Scenes.Sum(s => s.Shots.Count),
            savedProject.Scenes.Sum(s => s.Shots.Sum(sh => Math.Max(0, sh.DurationSeconds))),
            savedProject.TargetDurationSeconds > 0
                ? (int)Math.Round((double)savedProject.Scenes.Sum(s => s.Shots.Sum(sh => Math.Max(0, sh.DurationSeconds))) / savedProject.TargetDurationSeconds * 100)
                : 0);
        return Results.BadRequest(new
        {
            error = "Repair plan completed but the storyboard is still too short for the target duration. Analyze the story again before rendering.",
            durationIssue = savedDurationIssue
        });
    }

    var response = mapper.FromProject(savedProject);
    var responseRules = GetProjectDurationRules(response.TargetDurationSeconds);
    logger.LogInformation(
        "director_plan_regenerate_completed projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} plannedDurationSeconds={PlannedDurationSeconds} coveragePercent={CoveragePercent} sceneCount={SceneCount} shotCount={ShotCount} minimumScenes={MinimumScenes} minimumShots={MinimumShots} targetSceneRange={TargetSceneMin}-{TargetSceneMax} targetShotRange={TargetShotMin}-{TargetShotMax}",
        id,
        response.TargetDurationSeconds,
        response.TotalPlannedDurationSeconds,
        response.PlannedDurationCoveragePercent,
        response.SceneCount,
        response.ShotCount,
        responseRules.minimumScenes,
        responseRules.minimumShots,
        responseRules.targetSceneMin,
        responseRules.targetSceneMax,
        responseRules.targetShotMin,
        responseRules.targetShotMax);
    logger.LogInformation(
        "director_duration_repair_completed projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} oldSceneCount={OldSceneCount} oldShotCount={OldShotCount} newSceneCount={NewSceneCount} newShotCount={NewShotCount} oldPlannedDurationSeconds={OldPlannedDurationSeconds} newPlannedDurationSeconds={NewPlannedDurationSeconds} coveragePercent={CoveragePercent}",
        id,
        response.TargetDurationSeconds,
        oldSceneCount,
        oldShotCount,
        response.SceneCount,
        response.ShotCount,
        oldPlannedDurationSeconds,
        response.TotalPlannedDurationSeconds,
        response.PlannedDurationCoveragePercent);
    logger.LogInformation(
        "director_duration_validation_completed projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} newSceneCount={NewSceneCount} newShotCount={NewShotCount} newPlannedDurationSeconds={NewPlannedDurationSeconds} coveragePercent={CoveragePercent} isValid=true",
        id,
        response.TargetDurationSeconds,
        response.SceneCount,
        response.ShotCount,
        response.TotalPlannedDurationSeconds,
        response.PlannedDurationCoveragePercent);
    return Results.Ok(response);
}

static void UpsertDirectorPlanCharacters(Guid projectId, ProductionPlanDto plan, ProductionPlanMapper mapper, VideoStudioDbContext db)
{
    var existingCharacters = db.Characters.Where(c => c.ProjectId == projectId).ToList();
    var plannedCharacters = mapper.BuildCharacters(projectId, plan);
    foreach (var planned in plannedCharacters)
    {
        var existing = existingCharacters.FirstOrDefault(c => c.Id == planned.Id)
            ?? existingCharacters.FirstOrDefault(c => c.Name.Equals(planned.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            db.Characters.Add(planned);
            continue;
        }

        var referenceImagePath = existing.ReferenceImagePath;
        var referenceImageUrl = existing.ReferenceImageUrl;
        var referenceAssetId = existing.ReferenceAssetId;
        var referenceStatus = existing.ReferenceStatus;
        existing.Name = planned.Name;
        existing.Description = planned.Description;
        existing.Role = planned.Role;
        existing.Personality = planned.Personality;
        existing.VisualPrompt = planned.VisualPrompt;
        existing.VoiceStyle = planned.VoiceStyle;
        existing.ContinuityRulesJson = planned.ContinuityRulesJson;
        existing.CharacterBibleJson = planned.CharacterBibleJson;
        existing.ReferenceImagePrompt = planned.ReferenceImagePrompt;
        existing.ReferenceImageNegativePrompt = planned.ReferenceImageNegativePrompt;
        existing.ReferenceImagePath = referenceImagePath;
        existing.ReferenceImageUrl = referenceImageUrl;
        existing.ReferenceAssetId = referenceAssetId;
        existing.ReferenceStatus = string.IsNullOrWhiteSpace(referenceImagePath) ? planned.ReferenceStatus : referenceStatus;
    }
}

static ProjectSummaryDto ToSummary(Project project) => new(project.Id, project.Name, project.StoryText, project.TargetDurationSeconds, project.Status, project.CreatedAt, project.UpdatedAt);

static ProjectDetailsDto ToDetails(Project project) => new(project.Id, project.Name, project.StoryText, project.TargetDurationSeconds, project.Status, project.Logline, project.Genre, project.CreatedAt, project.UpdatedAt);

static RenderJobDto ToRenderJobDto(RenderJob job) => new(job.Id, job.ProjectId, job.SceneId, job.ShotId, job.Scene?.Index, job.Shot?.Index, job.CharacterId, job.DialogueLineId, job.JobType, job.Preset, job.GenerationMode, job.GenerationMode.ToString(), job.Prompt, job.CompiledPrompt, job.NegativePrompt, job.Size, job.FrameNum, job.SampleSteps, job.Seed, job.InputImagePath, TryBuildMediaUrl(job.InputImagePath, "assets", job.ProjectId), job.InputVideoPath, job.InputAudioPath, job.TextContent, job.Speaker, job.Emotion, job.Language, job.Voice, job.OutputPath, job.Status, job.Progress, job.ErrorMessage, job.CreatedAt, job.StartedAt, job.FinishedAt, RenderDurationSeconds(job), job.RenderDurationMode.ToString(), job.RequestedShotDurationSeconds, job.RequestedFrameNum ?? RequestedFrameNumForJob(job, null), job.ActualFrameNum ?? job.FrameNum, job.ExpectedRawClipDurationSeconds ?? ExpectedRawClipDurationSeconds(job.FrameNum), job.ProbedRawClipDurationSeconds, job.RawDurationCoveragePercent);

static Dictionary<Guid, RenderJob> LatestCompletedRendersByShot(IEnumerable<RenderJob> jobs)
{
    return jobs
        .Where(j => j.JobType == RenderJobType.RenderVideo
            && j.ShotId is not null
            && j.Status == RenderJobStatus.Completed
            && HasValidOutputPath(j.OutputPath))
        .GroupBy(j => j.ShotId!.Value)
        .ToDictionary(
            g => g.Key,
            g => g
                .OrderByDescending(RenderDurationRank)
                .ThenByDescending(RenderSortTimestamp)
                .ThenByDescending(j => j.CreatedAt)
                .First());
}

static DateTimeOffset RenderSortTimestamp(RenderJob job) => job.FinishedAt ?? job.CreatedAt;

static int RenderDurationRank(RenderJob job) => job.RenderDurationMode switch
{
    RenderDurationMode.ComfyUIParity => 3,
    RenderDurationMode.LongMotion => 2,
    RenderDurationMode.CinematicPreview => 1,
    _ => 0
};

static bool CompletedRenderSatisfiesRequestedMode(RenderJob job, RenderDurationMode requestedMode)
{
    return requestedMode switch
    {
        RenderDurationMode.AutoQuality => job.RenderDurationMode is RenderDurationMode.LongMotion or RenderDurationMode.ComfyUIParity,
        RenderDurationMode.ComfyUIParity => job.RenderDurationMode == RenderDurationMode.ComfyUIParity,
        RenderDurationMode.LongMotion => job.RenderDurationMode is RenderDurationMode.LongMotion or RenderDurationMode.ComfyUIParity,
        _ => true
    };
}

static bool HasValidOutputPath(string? outputPath)
{
    if (string.IsNullOrWhiteSpace(outputPath))
    {
        return false;
    }

    return !Path.IsPathFullyQualified(outputPath) || File.Exists(outputPath);
}

static object? GetProjectDurationPlanIssue(Project project)
{
    var targetDurationSeconds = project.TargetDurationSeconds > 0 ? project.TargetDurationSeconds : 60;
    var sceneCount = project.Scenes.Count;
    var shotCount = project.Scenes.Sum(scene => scene.Shots.Count);
    var plannedDurationSeconds = project.Scenes.Sum(scene => scene.Shots.Sum(shot => Math.Max(0, shot.DurationSeconds)));
    var rules = GetProjectDurationRules(targetDurationSeconds);
    var minimumPlannedDurationSeconds = (int)Math.Ceiling(targetDurationSeconds * rules.minimumCoverageRatio);
    var coveragePercent = targetDurationSeconds > 0
        ? (int)Math.Round((double)plannedDurationSeconds / targetDurationSeconds * 100)
        : 0;
    var isValid = sceneCount >= rules.minimumScenes
        && shotCount >= rules.minimumShots
        && plannedDurationSeconds >= minimumPlannedDurationSeconds;
    if (isValid)
    {
        return null;
    }

    return new
    {
        targetDurationSeconds,
        plannedDurationSeconds,
        coveragePercent,
        sceneCount,
        shotCount,
        minimumScenes = rules.minimumScenes,
        minimumShots = rules.minimumShots,
        targetSceneRange = $"{rules.targetSceneMin}-{rules.targetSceneMax}",
        targetShotRange = $"{rules.targetShotMin}-{rules.targetShotMax}",
        minimumPlannedDurationSeconds
    };
}

static object? GetProductionPlanDurationIssue(ProductionPlanDto plan)
{
    var targetDurationSeconds = plan.TargetDurationSeconds > 0 ? plan.TargetDurationSeconds : 60;
    var sceneCount = plan.SceneCount;
    var shotCount = plan.ShotCount;
    var plannedDurationSeconds = plan.TotalPlannedDurationSeconds;
    var rules = GetProjectDurationRules(targetDurationSeconds);
    var minimumPlannedDurationSeconds = (int)Math.Ceiling(targetDurationSeconds * rules.minimumCoverageRatio);
    var coveragePercent = targetDurationSeconds > 0
        ? (int)Math.Round((double)plannedDurationSeconds / targetDurationSeconds * 100)
        : 0;
    var isValid = sceneCount >= rules.minimumScenes
        && shotCount >= rules.minimumShots
        && plannedDurationSeconds >= minimumPlannedDurationSeconds;
    if (isValid)
    {
        return null;
    }

    return new
    {
        targetDurationSeconds,
        plannedDurationSeconds,
        coveragePercent,
        sceneCount,
        shotCount,
        minimumScenes = rules.minimumScenes,
        minimumShots = rules.minimumShots,
        targetSceneRange = $"{rules.targetSceneMin}-{rules.targetSceneMax}",
        targetShotRange = $"{rules.targetShotMin}-{rules.targetShotMax}",
        minimumPlannedDurationSeconds
    };
}

static (int minimumScenes, int targetSceneMin, int targetSceneMax, int minimumShots, int targetShotMin, int targetShotMax, double minimumCoverageRatio) GetProjectDurationRules(int targetDurationSeconds)
{
    if (targetDurationSeconds >= 420)
    {
        return (14, 18, 24, 50, 60, 84, 0.85);
    }

    if (targetDurationSeconds >= 300)
    {
        return (12, 14, 18, 40, 45, 60, 0.90);
    }

    if (targetDurationSeconds >= 180)
    {
        return (8, 10, 14, 24, 30, 36, 0.90);
    }

    if (targetDurationSeconds >= 60)
    {
        return (5, 6, 8, 10, 12, 16, 0.90);
    }

    return (1, 1, 4, 1, 1, 8, 0.85);
}

static double? RenderDurationSeconds(RenderJob? job)
{
    if (job?.StartedAt is not DateTimeOffset startedAt || job.FinishedAt is not DateTimeOffset finishedAt)
    {
        return null;
    }

    return Math.Round(Math.Max(0, (finishedAt - startedAt).TotalSeconds), 1);
}

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

static KeyframePromptResult BuildShotStartImagePrompt(Project project, Scene scene, Shot shot, IEnumerable<Character> characters, ILogger logger)
{
    logger.LogInformation(
        "prompt_compiler_character_lock_validation_started projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId}",
        project.Id,
        scene.Index,
        shot.Index,
        shot.Id);

    var failedFields = new List<string>();
    var repairedFields = new List<string>();
    var style = CleanPromptPart(RequiredText(project.VisualStylePrompt, "photorealistic live-action historical cinematic style"));
    var canonicalCharacters = ResolveShotCharacters(scene, shot, characters.ToList());
    var characterLock = BuildCanonicalCharacterLock(project, scene, shot, canonicalCharacters, logger);
    if (canonicalCharacters.Count == 0)
    {
        characterLock = "No visible character; environment insert shot.";
    }

    var locationLock = BuildConcreteLocationLock(project, scene, shot, logger, repairedFields);
    if (ContainsPlaceholderContinuity(locationLock))
    {
        failedFields.Add("locationLock");
        logger.LogWarning(
            "prompt_compiler_location_lock_validation_failed projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} failedFields={FailedFields}",
            project.Id,
            scene.Index,
            shot.Index,
            shot.Id,
            "locationLock");
        logger.LogInformation(
            "prompt_compiler_placeholder_repair_started projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} failedFields={FailedFields}",
            project.Id,
            scene.Index,
            shot.Index,
            shot.Id,
            "locationLock");
        locationLock = RepairConcreteLocationFromContext(project, scene, shot);
        repairedFields.Add("locationLock");
        logger.LogInformation(
            "prompt_compiler_placeholder_repair_completed projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} repairedFields={RepairedFields}",
            project.Id,
            scene.Index,
            shot.Index,
            shot.Id,
            "locationLock");
    }

    var lighting = NormalizeLighting(project, scene, shot);
    if (IsContradictoryLighting(project.LightingStyle) || IsContradictoryLighting(scene.TimeOfDay))
    {
        repairedFields.Add("lighting");
        logger.LogInformation(
            "prompt_compiler_lighting_normalization_applied projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} repairedFields={RepairedFields}",
            project.Id,
            scene.Index,
            shot.Index,
            shot.Id,
            "lighting");
    }

    var action = BuildExplicitShotAction(scene, shot, canonicalCharacters);
    if (IsPronounOnlyAction(shot.Action) && canonicalCharacters.Count > 0)
    {
        failedFields.Add("action");
        repairedFields.Add("action");
    }
    if (ContainsTurkishVisualText(action) || LooksLikeStoryText(action))
    {
        var originalActionLength = action.Length;
        action = RepairEnglishShotAction(scene, shot, canonicalCharacters);
        repairedFields.Add("actionEnglishRepair");
        logger.LogInformation(
            "prompt_compiler_turkish_visual_prompt_repaired projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} originalLength={OriginalLength} finalLength={FinalLength} repairedFields={RepairedFields}",
            project.Id,
            scene.Index,
            shot.Index,
            shot.Id,
            originalActionLength,
            action.Length,
            "actionEnglishRepair");
    }

    var sceneAnchor = BuildConcreteSceneAnchor(project, scene, canonicalCharacters, locationLock, lighting);
    var camera = CleanPromptPart($"{RequiredText(shot.ShotType, "medium cinematic shot")}, {RequiredText(shot.CameraMotion, "slow controlled camera movement")}");
    var mood = CleanPromptPart(RequiredText(scene.Mood, "tense grounded mystery"));

    var rawPrompt = string.Join(" ", new[]
    {
        $"Style lock: {style}.",
        $"Location: {locationLock}.",
        $"Visible characters: {characterLock}.",
        $"Primary action: {action}.",
        $"Camera: {camera}.",
        $"Lighting: {lighting}.",
        $"Mood: {mood}.",
        "No text, no subtitles, no logos."
    }).Trim();
    var prompt = ApplySdxlPromptBudget(rawPrompt, project, scene, shot, logger, repairedFields);

    var negative = BuildKeyframeNegativePrompt(project, scene, shot, canonicalCharacters);
    var validationFailures = ValidateKeyframePrompt(prompt, negative, canonicalCharacters);
    if (validationFailures.Count > 0)
    {
        failedFields.AddRange(validationFailures);
        logger.LogWarning(
            "prompt_compiler_character_lock_validation_failed projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} failedFields={FailedFields}",
            project.Id,
            scene.Index,
            shot.Index,
            shot.Id,
            string.Join("|", validationFailures.Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    logger.LogInformation(
        "prompt_compiler_keyframe_prompt_validation_completed projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} failedFields={FailedFields} repairedFields={RepairedFields}",
        project.Id,
        scene.Index,
        shot.Index,
        shot.Id,
        string.Join("|", failedFields.Distinct(StringComparer.OrdinalIgnoreCase)),
        string.Join("|", repairedFields.Distinct(StringComparer.OrdinalIgnoreCase)));

    return new KeyframePromptResult(prompt, negative, characterLock, locationLock, lighting, sceneAnchor);
}

static List<Character> ResolveShotCharacters(Scene scene, Shot shot, IReadOnlyCollection<Character> characters)
{
    var text = $"{shot.Action} {shot.Prompt} {shot.CharacterLockPrompt} {shot.InvolvedCharacterIdsJson} {scene.RequiredCharactersJson} {scene.Summary}";
    return characters
        .Where(character =>
            text.Contains(character.Id.ToString(), StringComparison.OrdinalIgnoreCase) ||
            text.Contains(character.Name, StringComparison.OrdinalIgnoreCase))
        .OrderBy(character => character.Name)
        .ToList();
}

static string BuildCanonicalCharacterLock(Project project, Scene scene, Shot shot, IReadOnlyCollection<Character> characters, ILogger logger)
{
    return string.Join(" ", characters.Select(character =>
    {
        var originalText = RequiredText(character.VisualPrompt, character.ReferenceImagePrompt ?? string.Empty);
        var visualLock = CanonicalCharacterLock(character);
        var repairedFields = new List<string>();
        if (LooksLikePortraitPrompt(originalText))
        {
            repairedFields.Add("portraitPromptSanitized");
            logger.LogInformation(
                "prompt_compiler_reference_prompt_sanitized projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} characterId={CharacterId} originalLength={OriginalLength} finalLength={FinalLength} repairedFields={RepairedFields}",
                project.Id,
                scene.Index,
                shot.Index,
                shot.Id,
                character.Id,
                originalText.Length,
                visualLock.Length,
                string.Join("|", repairedFields));
        }

        logger.LogInformation(
            "prompt_compiler_character_video_lock_created projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} characterId={CharacterId} originalLength={OriginalLength} finalLength={FinalLength} repairedFields={RepairedFields}",
            project.Id,
            scene.Index,
            shot.Index,
            shot.Id,
            character.Id,
            originalText.Length,
            visualLock.Length,
            string.Join("|", repairedFields));

        var forbidden = BuildCharacterForbiddenDrift(character);
        return $"{character.Name}: {visualLock} Forbidden drift: {forbidden}.";
    }));
}

static string CanonicalCharacterLock(Character character)
{
    var raw = RequiredText(character.VisualPrompt, string.Empty);
    var usedReferenceFallback = false;
    if (string.IsNullOrWhiteSpace(raw))
    {
        raw = character.ReferenceImagePrompt ?? string.Empty;
        usedReferenceFallback = true;
    }

    var lockText = BuildKnownStoryCharacterLock(character, raw);
    if (usedReferenceFallback || LooksLikePortraitPrompt(raw))
    {
        lockText = SanitizePortraitPrompt(lockText);
    }

    return CleanPromptPart(RequiredText(lockText, $"{character.Name}, {character.Role}, stable face, age, hair, costume, silhouette, and signature props"));
}

static string BuildKnownStoryCharacterLock(Character character, string candidate)
{
    var text = SanitizePortraitPrompt(candidate);
    text = RemoveForbiddenStaleVisualItems(text);

    if (character.Name.Contains("Aras", StringComparison.OrdinalIgnoreCase))
    {
        return CleanPromptPart("Aras, young Anatolian village messenger, consistent youthful face, dark hair, earth-toned wool tunic, worn leather belt, small messenger pouch, brass lantern, anxious but brave posture");
    }

    if (character.Name.Contains("Selim", StringComparison.OrdinalIgnoreCase))
    {
        return CleanPromptPart("Selim Usta, elderly Anatolian stone mason, weathered face, grey beard, dark wool vest over simple shirt, walking staff, calm protective posture, traditional village clothing");
    }

    return CleanPromptPart(RequiredText(text, $"{character.Name}, {character.Role}, stable face, age, hair, costume, silhouette, and signature props"));
}

static string SanitizePortraitPrompt(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    var text = CleanPromptPart(value);
    var bannedPatterns = new[]
    {
        "English character reference image for a story-to-video pipeline",
        "Neutral cinematic background",
        "clear face",
        "natural full-body or waist-up framing",
        "standing slightly turned toward camera",
        "standing slightly turned",
        "soft sunlight",
        "soft cinematic lighting",
        "realistic skin texture",
        "realistic anatomy",
        "portrait",
        "background",
        "no readable text",
        "no logos",
        "Character:",
        "Role:",
        "Visual lock:",
        "Personality cue:",
        "Style:"
    };

    foreach (var banned in bannedPatterns)
    {
        text = Regex.Replace(text, Regex.Escape(banned), " ", RegexOptions.IgnoreCase);
    }

    text = Regex.Replace(text, "\\b(reference image|headshot|waist-up|full-body|full body)\\b", " ", RegexOptions.IgnoreCase);
    return CleanPromptPart(text);
}

static bool LooksLikePortraitPrompt(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    return ContainsAny(value.ToLowerInvariant(),
        "reference image",
        "neutral cinematic background",
        "clear face",
        "waist-up",
        "waist up",
        "portrait",
        "standing slightly",
        "soft sunlight",
        "realistic skin texture");
}

static string RemoveForbiddenStaleVisualItems(string value)
{
    var text = value;
    var stalePatterns = new[]
    {
        "blue scarf",
        "bright blue scarf",
        "red scarf",
        "modern jacket",
        "sneakers",
        "jeans",
        "robot child",
        "robotic child"
    };

    foreach (var stale in stalePatterns)
    {
        text = Regex.Replace(text, Regex.Escape(stale), " ", RegexOptions.IgnoreCase);
    }

    return CleanPromptPart(text);
}

static string BuildCharacterForbiddenDrift(Character character)
{
    var lockText = CanonicalCharacterLock(character).ToLowerInvariant();
    var terms = new List<string> { "different face", "different costume", "different age", "different hair", "unrelated accessories" };
    if (lockText.Contains("lantern", StringComparison.OrdinalIgnoreCase))
    {
        terms.Add("missing brass lantern");
    }
    if (lockText.Contains("pouch", StringComparison.OrdinalIgnoreCase) || lockText.Contains("satchel", StringComparison.OrdinalIgnoreCase))
    {
        terms.Add("missing messenger pouch");
    }
    if (lockText.Contains("staff", StringComparison.OrdinalIgnoreCase))
    {
        terms.Add("missing walking staff");
    }
    if (lockText.Contains("headwrap", StringComparison.OrdinalIgnoreCase) || lockText.Contains("head wrap", StringComparison.OrdinalIgnoreCase))
    {
        terms.Add("missing headwrap");
    }

    return string.Join(", ", terms.Distinct(StringComparer.OrdinalIgnoreCase));
}

static string BuildConcreteLocationLock(Project project, Scene scene, Shot shot, ILogger logger, List<string> repairedFields)
{
    var candidates = new[]
    {
        scene.LocationContinuityPrompt,
        scene.SceneAnchorPrompt,
        scene.Location,
        ExtractLocationBibleText(project.LocationBibleJson),
        shot.LocationLockPrompt
    };
    var accepted = new List<string>();
    foreach (var rawCandidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)))
    {
        var candidate = CleanPromptPart(rawCandidate!);
        var rejectedAsStory = LooksLikeStoryText(candidate);
        var rejectedAsTurkish = ContainsTurkishVisualText(candidate);
        if (ContainsPlaceholderContinuity(candidate) || rejectedAsStory || rejectedAsTurkish)
        {
            if (rejectedAsStory)
            {
                repairedFields.Add("storyTextRemoved");
                logger.LogInformation(
                    "prompt_compiler_story_text_removed_from_location_lock projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} originalLength={OriginalLength} finalLength={FinalLength} repairedFields={RepairedFields}",
                    project.Id,
                    scene.Index,
                    shot.Index,
                    shot.Id,
                    candidate.Length,
                    0,
                    "storyTextRemoved");
            }

            if (rejectedAsTurkish)
            {
                repairedFields.Add("locationTurkishRepaired");
                logger.LogInformation(
                    "prompt_compiler_turkish_visual_prompt_repaired projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} originalLength={OriginalLength} finalLength={FinalLength} repairedFields={RepairedFields}",
                    project.Id,
                    scene.Index,
                    shot.Index,
                    shot.Id,
                    candidate.Length,
                    0,
                    "locationTurkishRepaired");
            }

            continue;
        }

        accepted.Add(candidate);
        if (accepted.Count >= 4)
        {
            break;
        }
    }

    var concrete = string.Join(", ", accepted);
    if (!string.IsNullOrWhiteSpace(concrete))
    {
        logger.LogInformation(
            "prompt_compiler_location_video_lock_created projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} originalLength={OriginalLength} finalLength={FinalLength} repairedFields={RepairedFields}",
            project.Id,
            scene.Index,
            shot.Index,
            shot.Id,
            candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Sum(value => value!.Length),
            concrete.Length,
            string.Join("|", repairedFields.Distinct(StringComparer.OrdinalIgnoreCase)));
        return concrete;
    }

    var repairedLocation = RepairConcreteLocationFromContext(project, scene, shot);
    repairedFields.Add("locationLock");
    logger.LogInformation(
        "prompt_compiler_location_video_lock_created projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} originalLength={OriginalLength} finalLength={FinalLength} repairedFields={RepairedFields}",
        project.Id,
        scene.Index,
        shot.Index,
        shot.Id,
        candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Sum(value => value!.Length),
        repairedLocation.Length,
        string.Join("|", repairedFields.Distinct(StringComparer.OrdinalIgnoreCase)));
    return repairedLocation;
}

static string ApplySdxlPromptBudget(string prompt, Project project, Scene scene, Shot shot, ILogger logger, List<string> repairedFields)
{
    var original = CleanPromptPart(RemoveStoryTextFromVisualPrompt(prompt));
    var promptParts = Regex.Split(original, "(?=Style lock:|Location:|Visible characters:|Primary action:|Camera:|Lighting:|Mood:|No text)", RegexOptions.IgnoreCase)
        .Select(CleanPromptPart)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToList();

    var finalParts = new List<string>();
    var wordBudget = 96;
    foreach (var part in promptParts)
    {
        var candidate = string.Join(" ", finalParts.Append(part));
        if (WordCount(candidate) <= wordBudget || finalParts.Count == 0)
        {
            finalParts.Add(part);
        }
    }

    var finalPrompt = string.Join(" ", finalParts);
    if (!finalPrompt.Contains("No text", StringComparison.OrdinalIgnoreCase))
    {
        finalPrompt = $"{finalPrompt} No text, no subtitles, no logos.";
    }

    if (!string.Equals(original, finalPrompt, StringComparison.Ordinal) || WordCount(prompt) > 100)
    {
        repairedFields.Add("sdxlPromptBudget");
        logger.LogInformation(
            "prompt_compiler_sdxl_prompt_budget_applied projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} originalLength={OriginalLength} finalLength={FinalLength} originalWords={OriginalWords} finalWords={FinalWords} repairedFields={RepairedFields}",
            project.Id,
            scene.Index,
            shot.Index,
            shot.Id,
            prompt.Length,
            finalPrompt.Length,
            WordCount(prompt),
            WordCount(finalPrompt),
            "sdxlPromptBudget");
    }

    return finalPrompt;
}

static string RemoveStoryTextFromVisualPrompt(string value)
{
    var text = value;
    text = Regex.Replace(text, "A clear narrative beat[^.]*\\.", " ", RegexOptions.IgnoreCase);
    text = Regex.Replace(text, "\\b(cinematic story location|story location|cinematic environment)\\b", "concrete visual location", RegexOptions.IgnoreCase);
    return CleanPromptPart(text);
}

static string RepairConcreteLocationFromContext(Project project, Scene scene, Shot shot)
{
    var context = $"{project.StoryText} {project.Name} {scene.Title} {scene.Summary} {shot.Action}".ToLowerInvariant();
    if (ContainsAny(context, "well", "kuyu", "village", "mountain", "aras", "selim"))
    {
        return scene.Index switch
        {
            1 => "abandoned Seljuq mountain village square at late dusk, old stone well at the center, narrow dirt paths, dry grass, old stone houses, worn wooden doors, cold dusk air, muted earth tones, same well geometry",
            2 => "abandoned Seljuq mountain village near the old stone well at darker dusk, narrow dirt path, dry grass, old stone houses, brass lantern light beginning to dominate, same well geometry",
            3 => "old stone well in abandoned Seljuq mountain village under moonlight, brass lantern glow near the rim, worn wooden doors and stone houses around the square, same well geometry",
            4 => "abandoned village square beside the old stone well, dust and old stone lit by low brass lantern light, dry grass and worn doors in the background, same well geometry",
            5 => "old stone well in abandoned Seljuq mountain village, faint warm light rising from inside the well, brass lantern glow, cool moonlit stone houses and dry grass around it",
            _ => "abandoned Seljuq mountain village square, old stone well at the center, narrow dirt paths, dry grass, old stone houses, worn wooden doors, cold dusk air, brass lantern light, muted earth tones, same well geometry"
        };
    }

    if (ContainsAny(context, "seljuq", "well", "kuyu", "village", "mountain"))
    {
        return scene.Index switch
        {
            1 => "abandoned Seljuq mountain village square at late dusk, old stone well at the center, narrow dirt paths, dry grass, old stone houses, worn wooden doors, cold dusk air, muted earth tones, same well geometry",
            2 => "abandoned Seljuq mountain village near the old stone well at darker dusk, narrow dirt path, dry grass, old stone houses, brass lantern light beginning to dominate, same well geometry",
            3 => "old stone well in abandoned Seljuq mountain village under moonlight, brass lantern glow near the rim, worn wooden doors and stone houses around the square, same well geometry",
            4 => "abandoned village square beside the old stone well, dust and old stone lit by low brass lantern light, dry grass and worn doors in the background, same well geometry",
            5 => "old stone well in abandoned Seljuq mountain village, faint warm light rising from inside the well, brass lantern glow, cool moonlit stone houses and dry grass around it",
            _ => "abandoned Seljuq mountain village square, old stone well at the center, narrow dirt paths, dry grass, old stone houses, worn wooden doors, cold dusk air, brass lantern light, muted earth tones, same well geometry"
        };
    }

    if (ContainsAny(context, "seljuq", "selçuk", "well", "kuyu", "village", "köy", "mountain", "dağ"))
    {
        return "abandoned Seljuq mountain village, old stone well in the center, narrow dirt path, old stone houses, dry grass, worn wooden doors, cold dusk air, brass lantern light, same well geometry and same village geography across shots";
    }

    return $"{CleanPromptPart(RequiredText(scene.Location, scene.Title))}, stable geography, recurring props, consistent architecture and materials";
}

static string BuildConcreteSceneAnchor(Project project, Scene scene, IReadOnlyCollection<Character> characters, string locationLock, string lighting)
{
    var characterBlocking = characters.Count == 0
        ? "no visible character blocking"
        : string.Join(", ", characters.Select(c => $"{c.Name} present with canonical costume and props"));
    return $"Scene {scene.Index} anchor: {locationLock}, {lighting}, {characterBlocking}, story state: {CleanPromptPart(RequiredText(scene.StoryStateAfter ?? scene.Summary, "the scene continues with stable geography"))}";
}

static string NormalizeLighting(Project project, Scene scene, Shot shot)
{
    var context = $"{project.StoryText} {scene.Title} {scene.Summary} {scene.TimeOfDay} {scene.Mood} {shot.Action}".ToLowerInvariant();
    if (ContainsAny(context, "well", "kuyu", "seljuq", "selçuk", "village", "köy"))
    {
        return scene.Index switch
        {
            1 => "late dusk, cold natural evening light, long soft shadows",
            2 => "darker dusk, brass lantern light beginning to dominate",
            3 => "cool moonlight mixed with brass lantern light",
            4 => "low brass lantern light grazing dust and old stone",
            5 => "faint warm light rising from inside the well plus brass lantern glow",
            _ => "brass lantern glow near the well, surrounding village in cool moonlight"
        };
    }

    if (ContainsAny(context, "night", "moon", "dark"))
    {
        return "cool moonlight with one motivated practical light source";
    }
    if (ContainsAny(context, "dusk", "twilight", "evening", "sunset"))
    {
        return "late dusk with soft fading natural light and motivated practical highlights";
    }
    if (ContainsAny(context, "interior", "inside", "cave", "room"))
    {
        return "low motivated practical light shaped by the scene environment";
    }

    return "one coherent natural cinematic light setup matched to the scene time of day";
}

static string BuildExplicitShotAction(Scene scene, Shot shot, IReadOnlyCollection<Character> characters)
{
    var action = CleanPromptPart(RequiredText(shot.Action, shot.Prompt ?? scene.Summary));
    if (characters.Count == 0)
    {
        return $"environment insert shot showing {action} in {CleanPromptPart(RequiredText(scene.Location, scene.Title))}";
    }

    var primary = characters.First();
    if (IsPronounOnlyAction(action))
    {
        return $"{primary.Name} performs the shot action in the concrete scene location, using their signature props, with clear body posture and readable intent";
    }

    if (!action.Contains(primary.Name, StringComparison.OrdinalIgnoreCase))
    {
        return $"{primary.Name} {LowercaseFirst(action)}";
    }

    return action;
}

static string RepairEnglishShotAction(Scene scene, Shot shot, IReadOnlyCollection<Character> characters)
{
    var subject = characters.FirstOrDefault()?.Name ?? "the visible subject";
    var context = $"{scene.Title} {scene.Summary} {shot.Action} {shot.Prompt}".ToLowerInvariant();
    if (ContainsAny(context, "well", "kuyu"))
    {
        return $"{subject} approaches the old stone well with cautious body language, holding position in the village square";
    }
    if (ContainsAny(context, "lantern", "fener"))
    {
        return $"{subject} raises a brass lantern and studies the surrounding stone village";
    }
    if (ContainsAny(context, "door", "kapı", "kapi"))
    {
        return $"{subject} pauses near worn wooden doors and looks toward the old stone houses";
    }
    if (ContainsAny(context, "listen", "duyar", "sound", "ses"))
    {
        return $"{subject} stops and listens carefully, tense posture facing the source of the sound";
    }

    return $"{subject} performs the story action with clear posture in the selected scene environment";
}

static string BuildKeyframeNegativePrompt(Project project, Scene scene, Shot shot, IReadOnlyCollection<Character> characters)
{
    var parts = new List<string>
    {
        "wrong location",
        "different face",
        "different costume",
        "modern objects",
        "modern clothing",
        "cars",
        "asphalt",
        "electric wires",
        "neon signs",
        "unrelated characters",
        "inconsistent well",
        "inconsistent village",
        "cartoon",
        "anime",
        "CGI",
        "illustration",
        "digital painting",
        "plastic skin",
        "bad hands",
        "extra fingers",
        "extra limbs",
        "distorted face",
        "text",
        "logo",
        "watermark"
    };
    foreach (var character in characters)
    {
        parts.AddRange(BuildCharacterForbiddenDrift(character).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }
    if (!string.IsNullOrWhiteSpace(project.NegativePrompt))
    {
        parts.AddRange(CleanAbstractNegativeTerms(project.NegativePrompt).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }
    if (!string.IsNullOrWhiteSpace(shot.NegativePrompt))
    {
        parts.AddRange(CleanAbstractNegativeTerms(shot.NegativePrompt).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    return string.Join(", ", parts
        .Select(CleanPromptPart)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Where(value => !IsAbstractMetadataNegative(value))
        .Distinct(StringComparer.OrdinalIgnoreCase));
}

static List<string> ValidateKeyframePrompt(string prompt, string negativePrompt, IReadOnlyCollection<Character> characters)
{
    var failed = new List<string>();
    if (ContainsPlaceholderContinuity(prompt))
    {
        failed.Add("placeholderEnvironment");
    }
    if (prompt.Contains("Characters: .", StringComparison.OrdinalIgnoreCase) || prompt.Contains("Visible characters: .", StringComparison.OrdinalIgnoreCase))
    {
        failed.Add("emptyCharacters");
    }
    if (IsContradictoryLighting(prompt))
    {
        failed.Add("contradictoryLighting");
    }
    if (ContainsTurkishVisualText(prompt))
    {
        failed.Add("nonEnglishVisualPrompt");
    }
    if (LooksLikeStoryText(prompt))
    {
        failed.Add("storyTextInVisualPrompt");
    }
    if (WordCount(prompt) > 110)
    {
        failed.Add("sdxlPromptOverBudget");
    }
    foreach (var character in characters)
    {
        if (!prompt.Contains(character.Name, StringComparison.OrdinalIgnoreCase) || !prompt.Contains(CanonicalCharacterLock(character).Split(',', '.', ';')[0], StringComparison.OrdinalIgnoreCase))
        {
            failed.Add($"missingCanonicalLock:{character.Name}");
        }
    }
    if (negativePrompt.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(IsAbstractMetadataNegative))
    {
        failed.Add("abstractNegativePrompt");
    }

    return failed;
}

static bool IsInvalidKeyframePrompt(string? prompt)
{
    return string.IsNullOrWhiteSpace(prompt)
        || ContainsPlaceholderContinuity(prompt)
        || prompt.Contains("Characters: .", StringComparison.OrdinalIgnoreCase)
        || ContainsTurkishVisualText(prompt)
        || LooksLikeStoryText(prompt)
        || WordCount(prompt) > 110
        || IsContradictoryLighting(prompt);
}

static bool IsInvalidKeyframeNegativePrompt(string? negativePrompt)
{
    return string.IsNullOrWhiteSpace(negativePrompt)
        || negativePrompt.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(IsAbstractMetadataNegative);
}

static bool ContainsPlaceholderContinuity(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return true;
    }

    return ContainsAny(value.ToLowerInvariant(),
        "cinematic story location",
        "motivated time of day",
        "cinematic mood",
        "a clear narrative beat",
        "recurring props remain in the same visual world",
        "cinematic environment",
        "story location");
}

static bool LooksLikeStoryText(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var text = value.ToLowerInvariant();
    if (ContainsAny(text,
        "bir ",
        " ve ",
        " ile ",
        " için ",
        "gizemli",
        "hikaye",
        "köy",
        "kuyu",
        "çocuk",
        "gece",
        "anlat",
        "başlar",
        "gider",
        "görür",
        "duyar",
        "a clear narrative beat"))
    {
        return true;
    }

    var sentenceCount = Regex.Matches(value, "[.!?]").Count;
    return value.Length > 260 && sentenceCount >= 2;
}

static bool ContainsTurkishVisualText(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var text = value.ToLowerInvariant();
    return ContainsAny(text,
        "ı",
        "ğ",
        "ü",
        "ş",
        "ö",
        "ç",
        "Ã§",
        "Ã¶",
        "ÄŸ",
        "Ä±",
        "ÅŸ",
        "kÃ¶y",
        "selÃ§uk",
        "daÄŸ",
        "gÃ¶r",
        "duyar",
        "gizemli");
}

static int WordCount(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return 0;
    }

    return Regex.Matches(value, "\\b[\\p{L}\\p{N}'-]+\\b").Count;
}

static bool IsPronounOnlyAction(string? action)
{
    if (string.IsNullOrWhiteSpace(action))
    {
        return false;
    }

    var trimmed = action.Trim();
    return Regex.IsMatch(trimmed, "^(he|she|they|it)\\b", RegexOptions.IgnoreCase);
}

static bool IsContradictoryLighting(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var text = value.ToLowerInvariant();
    var states = 0;
    if (ContainsAny(text, "daylight", "midday", "clear sky")) states++;
    if (ContainsAny(text, "golden hour", "sunset", "dusk", "twilight")) states++;
    if (ContainsAny(text, "night", "moonlight", "dark")) states++;
    return states > 1;
}

static string CleanAbstractNegativeTerms(string value)
{
    return string.Join(", ", value
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(term => !IsAbstractMetadataNegative(term)));
}

static bool IsAbstractMetadataNegative(string value)
{
    return ContainsAny(value.ToLowerInvariant(),
        "wrong scene index",
        "wrong shot action",
        "mismatched mood",
        "visual discontinuity from scene");
}

static string ExtractLocationBibleText(string? json)
{
    if (string.IsNullOrWhiteSpace(json))
    {
        return string.Empty;
    }

    return CleanPromptPart(json.Replace("{", " ").Replace("}", " ").Replace("[", " ").Replace("]", " ").Replace("\"", " "));
}

static string CleanPromptPart(string value)
{
    return Regex.Replace(value, "\\s+", " ").Trim(' ', '.', ',', ';', ':');
}

static string LowercaseFirst(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return value;
    }

    return char.ToLowerInvariant(value[0]) + value[1..];
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

static async Task LogRenderJobDurationMetadataSchemaAsync(IServiceProvider services, ILogger logger)
{
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<VideoStudioDbContext>();
    var expectedColumns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["RenderDurationMode"] = ["nvarchar"],
        ["RequestedShotDurationSeconds"] = ["int"],
        ["RequestedFrameNum"] = ["int"],
        ["ActualFrameNum"] = ["int"],
        ["ExpectedRawClipDurationSeconds"] = ["float", "real"],
        ["ProbedRawClipDurationSeconds"] = ["float", "real"],
        ["RawDurationCoveragePercent"] = ["int"]
    };

    try
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COLUMN_NAME, DATA_TYPE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = 'RenderJobs'
              AND COLUMN_NAME IN (
                'RenderDurationMode',
                'RequestedShotDurationSeconds',
                'RequestedFrameNum',
                'ActualFrameNum',
                'ExpectedRawClipDurationSeconds',
                'ProbedRawClipDurationSeconds',
                'RawDurationCoveragePercent'
              )
            """;

        var actualColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actualColumns[reader.GetString(0)] = reader.GetString(1);
        }

        var problems = expectedColumns
            .Select(expected =>
            {
                if (!actualColumns.TryGetValue(expected.Key, out var actualType))
                {
                    return $"{expected.Key}=missing";
                }

                return expected.Value.Contains(actualType, StringComparer.OrdinalIgnoreCase)
                    ? null
                    : $"{expected.Key}=actual:{actualType},expected:{string.Join("|", expected.Value)}";
            })
            .Where(problem => problem is not null)
            .ToList();

        if (problems.Count > 0)
        {
            logger.LogError(
                "renderjob_duration_metadata_schema_invalid table=RenderJobs problems={Problems} action={Action}",
                string.Join(",", problems),
                "Run EF migrations manually; startup will not delete data or run destructive migrations.");
            return;
        }

        logger.LogInformation(
            "renderjob_duration_metadata_schema_valid table=RenderJobs columns={Columns}",
            string.Join(",", actualColumns.Select(column => $"{column.Key}:{column.Value}")));
    }
    catch (Exception ex)
    {
        logger.LogWarning(
            ex,
            "renderjob_duration_metadata_schema_check_failed action={Action}",
            "Could not verify RenderJobs duration metadata column types; ensure SQL Server is reachable and migrations are applied.");
    }
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

static AssemblyRules GetAssemblyRules(int targetDurationSeconds)
{
    if (targetDurationSeconds >= 420)
    {
        return new AssemblyRules(true, 50, (int)Math.Ceiling(targetDurationSeconds * 0.85));
    }

    if (targetDurationSeconds >= 300)
    {
        return new AssemblyRules(true, 40, (int)Math.Ceiling(targetDurationSeconds * 0.90));
    }

    if (targetDurationSeconds >= 180)
    {
        return new AssemblyRules(true, 24, (int)Math.Ceiling(targetDurationSeconds * 0.90));
    }

    return new AssemblyRules(false, 1, 1);
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

static RenderDurationSelection SelectRenderDuration(RenderDurationMode mode, int presetFrameNum, int shotDurationSeconds)
{
    var targetSeconds = shotDurationSeconds > 0 ? shotDurationSeconds : 1;
    return mode switch
    {
        RenderDurationMode.CinematicPreview => BuildDurationSelection(mode, targetSeconds, 73, 73),
        RenderDurationMode.ComfyUIParity => BuildDurationSelection(mode, targetSeconds, ComfyUIParityFrameNum(), ComfyUIParityFrameNum()),
        RenderDurationMode.LongMotion => BuildDurationSelection(mode, targetSeconds, FramesForSeconds(targetSeconds), ClampWanFrameCount(FramesForSeconds(targetSeconds), 25, RenderDurationMaxFrameNum())),
        _ => BuildDurationSelection(RenderDurationMode.FastPreview, targetSeconds, presetFrameNum, presetFrameNum)
    };
}

static string SelectRenderSize(RenderDurationMode mode, string presetSize, ILogger logger, Guid projectId, int sceneIndex, int shotIndex, Guid shotId)
{
    if (mode != RenderDurationMode.ComfyUIParity)
    {
        return presetSize;
    }

    logger.LogWarning(
        "wan_comfyui_parity_setting_unsupported projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} setting={Setting} requested={Requested} applied={Applied} reason={Reason}",
        projectId,
        sceneIndex,
        shotIndex,
        shotId,
        "size",
        "1280*736",
        ComfyUIParitySupportedSize(),
        "Wan2.2 ti2v-5B local config supports 1280*704 and 704*1280, not 1280*736.");
    return ComfyUIParitySupportedSize();
}

static int SelectSampleSteps(RenderDurationMode mode, int presetSampleSteps)
{
    return mode == RenderDurationMode.ComfyUIParity ? ComfyUIParitySampleSteps() : presetSampleSteps;
}

static RenderDurationMode ResolveRenderDurationMode(RenderDurationMode requestedMode, Shot shot)
{
    if (requestedMode != RenderDurationMode.AutoQuality)
    {
        return requestedMode;
    }

    if (Enum.TryParse<RenderDurationMode>(shot.RecommendedRenderDurationMode, true, out var recommended)
        && recommended != RenderDurationMode.AutoQuality)
    {
        return recommended;
    }

    var text = $"{shot.ShotType} {shot.CameraMotion} {shot.Action}".ToLowerInvariant();
    if (ContainsAny(text, "establishing", "wide", "arrival", "landscape", "approach"))
    {
        return RenderDurationMode.ComfyUIParity;
    }

    if (ContainsAny(text, "close", "emotion", "face", "dialogue", "look"))
    {
        return RenderDurationMode.LongMotion;
    }

    if (ContainsAny(text, "fight", "battle", "run", "chase", "explosion", "fast"))
    {
        return RenderDurationMode.CinematicPreview;
    }

    return shot.DurationSeconds >= 5 ? RenderDurationMode.LongMotion : RenderDurationMode.CinematicPreview;
}

static bool ContainsAny(string text, params string[] terms)
{
    return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
}

static RenderDurationSelection BuildDurationSelection(RenderDurationMode mode, int requestedShotDurationSeconds, int requestedFrameNum, int actualFrameNum)
{
    return new RenderDurationSelection(
        mode,
        requestedShotDurationSeconds,
        requestedFrameNum,
        actualFrameNum,
        ExpectedRawClipDurationSeconds(actualFrameNum) ?? 0,
        requestedFrameNum != actualFrameNum);
}

static RenderDurationMode InferRenderDurationMode(RenderJob job)
{
    if (job.FrameNum is null)
    {
        return RenderDurationMode.FastPreview;
    }

    if (job.FrameNum >= ComfyUIParityFrameNum())
    {
        return RenderDurationMode.ComfyUIParity;
    }

    if (job.FrameNum > 73)
    {
        return RenderDurationMode.LongMotion;
    }

    if (job.FrameNum > 25)
    {
        return RenderDurationMode.CinematicPreview;
    }

    return RenderDurationMode.FastPreview;
}

static int? RequestedFrameNumForJob(RenderJob job, int? targetDurationSeconds)
{
    var mode = InferRenderDurationMode(job);
    if (mode == RenderDurationMode.ComfyUIParity)
    {
        return ComfyUIParityFrameNum();
    }

    return mode == RenderDurationMode.LongMotion && targetDurationSeconds is > 0
        ? FramesForSeconds(targetDurationSeconds.Value)
        : job.FrameNum;
}

static int FramesForSeconds(int seconds) => ClampWanFrameCount((int)Math.Round(Math.Max(1, seconds) * 24.0), 25, int.MaxValue);

static int ClampWanFrameCount(int requestedFrameNum, int minFrameNum, int maxFrameNum)
{
    var clamped = Math.Clamp(requestedFrameNum, minFrameNum, maxFrameNum);
    var normalized = clamped <= 1 ? 1 : (((clamped - 1) / 4) * 4) + 1;
    if (normalized < minFrameNum)
    {
        normalized = minFrameNum;
    }
    if (normalized > maxFrameNum)
    {
        normalized = ((maxFrameNum - 1) / 4) * 4 + 1;
    }

    return normalized;
}

static double? ExpectedRawClipDurationSeconds(int? frameNum)
{
    return frameNum is > 0 ? Math.Round(frameNum.Value / 24.0, 2) : null;
}

static int RenderDurationMaxFrameNum() => 121;
static bool RequiresImageToVideoKeyframes(RenderDurationMode mode) => mode is RenderDurationMode.LongMotion or RenderDurationMode.ComfyUIParity or RenderDurationMode.AutoQuality;
static int ComfyUIParityFrameNum() => 161;
static int ComfyUIParitySampleSteps() => 18;
static string ComfyUIParitySupportedSize() => "1280*704";
static double ComfyUIParityGuideScale() => 5.0;
static double ComfyUIParitySampleShift() => 8.0;
static string ComfyUIParitySampleSolver() => "unipc";

public sealed record AssemblyRules(bool IsLongForm, int MinimumShots, int MinimumTargetDurationSeconds);
public sealed record RenderDurationSelection(RenderDurationMode Mode, int RequestedShotDurationSeconds, int RequestedFrameNum, int ActualFrameNum, double ExpectedRawClipDurationSeconds, bool WasClamped);
public sealed record KeyframePromptResult(string Prompt, string NegativePrompt, string CharacterLock, string LocationLock, string Lighting, string SceneAnchor);
