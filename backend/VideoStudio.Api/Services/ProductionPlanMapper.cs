using VideoStudio.Api.Contracts;
using VideoStudio.Api.Data;
using VideoStudio.Api.Domain;

namespace VideoStudio.Api.Services;

public sealed class ProductionPlanMapper(ProductionPlanJsonService json)
{
    public ProjectUpdateFields BuildProjectUpdate(ProductionPlanDto plan)
    {
        return new ProjectUpdateFields(
            plan.Title,
            plan.Logline,
            plan.Genre,
            plan.TargetDurationSeconds,
            plan.VisualStyle.StylePrompt,
            plan.VisualStyle.NegativePrompt,
            plan.VisualStyle.CameraStyle,
            plan.VisualStyle.LightingStyle,
            plan.VisualStyle.ColorPalette,
            json.Serialize(plan.AudioCues),
            plan.DirectorTreatment,
            json.Serialize(plan.BeatSheet),
            json.Serialize(plan.ActBreakdown),
            json.Serialize(plan.DirectorPlan?.CharacterBible ?? []),
            json.Serialize(plan.DirectorPlan?.LocationBible ?? []),
            json.Serialize(plan.DirectorPlan?.TimelineContinuity ?? []),
            json.Serialize(plan.DirectorPlan?.VisualContinuityRules ?? []),
            json.Serialize(plan.RenderStrategy ?? plan.DirectorPlan?.RenderStrategyRecommendation),
            plan.RenderStrategy?.QualityGoal ?? plan.DirectorPlan?.RenderStrategyRecommendation.QualityGoal ?? "Balanced");
    }

    public List<Character> BuildCharacters(Guid projectId, ProductionPlanDto plan)
    {
        return plan.Characters.Select(c => new Character
        {
            ProjectId = projectId,
            Name = c.Name,
            Description = c.Personality,
            Role = c.Role,
            Personality = c.Personality,
            VisualPrompt = c.VisualPrompt,
            VoiceStyle = c.VoiceStyle,
            ContinuityRulesJson = json.Serialize(c.ContinuityRules),
            CharacterBibleJson = json.Serialize(c.Bible),
            ReferenceImagePrompt = c.ReferenceImagePrompt,
            ReferenceImageNegativePrompt = c.ReferenceImageNegativePrompt,
            ReferenceStatus = string.IsNullOrWhiteSpace(c.ReferenceImagePrompt) ? "Missing" : "PromptReady"
        }).ToList();
    }

    public List<Scene> BuildScenes(Guid projectId, ProductionPlanDto plan)
    {
        var scenes = new List<Scene>();
        foreach (var scenePlan in plan.Scenes.OrderBy(s => s.Index))
        {
            var scene = new Scene
            {
                ProjectId = projectId,
                Order = scenePlan.Index,
                Index = scenePlan.Index,
                Title = scenePlan.Title,
                Description = scenePlan.Summary,
                Summary = scenePlan.Summary,
                Location = scenePlan.Location,
                TimeOfDay = scenePlan.TimeOfDay,
                Mood = scenePlan.Mood,
                EstimatedDurationSeconds = scenePlan.EstimatedDurationSeconds,
                Purpose = scenePlan.Purpose,
                StoryStateBefore = scenePlan.StoryStateBefore,
                StoryStateAfter = scenePlan.StoryStateAfter,
                LocationId = scenePlan.LocationId,
                SceneAnchorPrompt = scenePlan.SceneAnchorPrompt,
                LocationContinuityPrompt = scenePlan.LocationContinuityPrompt,
                ForbiddenLocationDrift = scenePlan.ForbiddenLocationDrift,
                RequiredCharactersJson = json.Serialize(scenePlan.RequiredCharacters),
                DialogueLinesJson = json.Serialize(scenePlan.DialogueLines)
            };

            scene.Shots.AddRange(scenePlan.Shots.OrderBy(s => s.Index).Select(shot => new Shot
            {
                ProjectId = projectId,
                SceneId = scene.Id,
                Order = shot.Index,
                Index = shot.Index,
                DurationSeconds = shot.DurationSeconds,
                ShotType = shot.ShotType,
                CameraMotion = shot.CameraMotion,
                Action = shot.Action,
                Prompt = shot.WanPrompt,
                NegativePrompt = shot.NegativePrompt,
                AudioCue = shot.AudioCue,
                ContinuityNotes = shot.ContinuityNotes,
                InvolvedCharacterIdsJson = json.Serialize(shot.InvolvedCharacterIds),
                CharacterLockPrompt = shot.CharacterLockPrompt,
                LocationId = shot.LocationId,
                LocationLockPrompt = shot.LocationLockPrompt,
                ForbiddenDriftTerms = shot.ForbiddenDriftTerms,
                PreviousShotVisualState = shot.PreviousShotVisualState,
                CurrentShotVisualState = shot.CurrentShotVisualState,
                NextShotSetup = shot.NextShotSetup,
                KeyframeContinuityPrompt = shot.KeyframeContinuityPrompt,
                SceneAnchorPrompt = shot.SceneAnchorPrompt,
                RecommendedRenderDurationMode = shot.RecommendedRenderDurationMode,
                AssemblyExtensionAllowed = shot.AssemblyExtensionAllowed,
                GenerationMode = VideoGenerationMode.TextToVideo,
                Status = ShotStatus.Pending,
                StartImagePrompt = shot.StartImagePrompt,
                StartImageNegativePrompt = shot.StartImageNegativePrompt,
                StartImageStatus = string.IsNullOrWhiteSpace(shot.StartImagePrompt) ? "Missing" : "PromptReady"
            }));

            scenes.Add(scene);
        }
        return scenes;
    }

    public List<DialogueLine> BuildDialogueLines(Guid projectId, ProductionPlanDto plan, IReadOnlyCollection<Scene> scenes)
    {
        var sceneByIndex = scenes.ToDictionary(s => s.Index);
        var lines = new List<DialogueLine>();
        foreach (var scenePlan in plan.Scenes)
        {
            if (!sceneByIndex.TryGetValue(scenePlan.Index, out var scene))
            {
                continue;
            }

            foreach (var line in scenePlan.DialogueLines ?? [])
            {
                lines.Add(new DialogueLine
                {
                    ProjectId = projectId,
                    SceneId = scene.Id,
                    Speaker = line.Speaker,
                    Text = line.Text,
                    Emotion = line.Emotion,
                    EstimatedStartSecond = line.EstimatedStartSecond,
                    EstimatedEndSecond = line.EstimatedEndSecond
                });
            }
        }

        return lines;
    }

    public ProductionPlanDto FromProject(Project project)
    {
        var visualStyle = new VisualStyleDto(
            project.VisualStylePrompt ?? string.Empty,
            project.NegativePrompt ?? ProductionPlanNormalizer.DefaultNegativePrompt,
            project.CameraStyle ?? string.Empty,
            project.LightingStyle ?? string.Empty,
            project.ColorPalette ?? string.Empty);

        var characters = project.Characters
            .OrderBy(c => c.Name)
            .Select(c => new CharacterPlanDto(c.Name, c.Role, c.Personality, c.VisualPrompt, c.VoiceStyle, json.DeserializeList<string>(c.ContinuityRulesJson))
            {
                Id = c.Id,
                ReferenceImagePrompt = c.ReferenceImagePrompt,
                ReferenceImageNegativePrompt = c.ReferenceImageNegativePrompt,
                ReferenceStatus = c.ReferenceStatus,
                ReferenceImagePath = c.ReferenceImagePath,
                ReferenceImageUrl = c.ReferenceImageUrl,
                Bible = json.Deserialize<CharacterBibleDto>(c.CharacterBibleJson)
            })
            .ToList();

        var scenes = project.Scenes
            .OrderBy(s => s.Index)
            .Select(s => new ScenePlanDto(
                s.Index,
                s.Title,
                s.Summary,
                s.Location,
                s.TimeOfDay,
                s.Mood,
                s.EstimatedDurationSeconds,
                json.DeserializeList<string>(s.RequiredCharactersJson),
                s.Shots.OrderBy(sh => sh.Index).Select(sh => new ShotPlanDto(sh.Index, sh.DurationSeconds, sh.ShotType, sh.CameraMotion, sh.Action, sh.Prompt, sh.NegativePrompt ?? project.NegativePrompt ?? ProductionPlanNormalizer.DefaultNegativePrompt, sh.AudioCue, sh.ContinuityNotes)
                {
                    Id = sh.Id,
                    SceneId = sh.SceneId,
                    StartImagePrompt = sh.StartImagePrompt,
                    StartImageNegativePrompt = sh.StartImageNegativePrompt,
                    StartImageStatus = sh.StartImageStatus,
                    StartImagePath = sh.StartImagePath,
                    StartImageUrl = sh.StartImageUrl,
                    InvolvedCharacterIds = json.DeserializeList<string>(sh.InvolvedCharacterIdsJson),
                    CharacterLockPrompt = sh.CharacterLockPrompt,
                    LocationId = sh.LocationId,
                    LocationLockPrompt = sh.LocationLockPrompt,
                    ForbiddenDriftTerms = sh.ForbiddenDriftTerms,
                    PreviousShotVisualState = sh.PreviousShotVisualState,
                    CurrentShotVisualState = sh.CurrentShotVisualState,
                    NextShotSetup = sh.NextShotSetup,
                    KeyframeContinuityPrompt = sh.KeyframeContinuityPrompt,
                    SceneAnchorPrompt = sh.SceneAnchorPrompt,
                    RecommendedRenderDurationMode = sh.RecommendedRenderDurationMode,
                    AssemblyExtensionAllowed = sh.AssemblyExtensionAllowed
                }).ToList(),
                json.DeserializeList<DialogueLineDto>(s.DialogueLinesJson))
            {
                Id = s.Id,
                Purpose = s.Purpose,
                StoryStateBefore = s.StoryStateBefore,
                StoryStateAfter = s.StoryStateAfter,
                LocationId = s.LocationId,
                SceneAnchorPrompt = s.SceneAnchorPrompt,
                LocationContinuityPrompt = s.LocationContinuityPrompt,
                ForbiddenLocationDrift = s.ForbiddenLocationDrift
            })
            .ToList();

        var renderStrategy = json.Deserialize<RenderStrategyRecommendationDto>(project.RenderStrategyRecommendationJson);
        var directorPlan = new DirectorPlanDto(
            project.Name,
            project.Logline ?? string.Empty,
            project.Genre ?? string.Empty,
            project.VisualStylePrompt ?? string.Empty,
            project.TargetDurationSeconds,
            project.DirectorTreatment ?? string.Empty,
            json.DeserializeList<DirectorActDto>(project.ActBreakdownJson),
            json.DeserializeList<DirectorBeatDto>(project.BeatSheetJson),
            json.DeserializeList<CharacterBibleDto>(project.CharacterBibleJson),
            json.DeserializeList<LocationBibleDto>(project.LocationBibleJson),
            json.DeserializeList<string>(project.TimelineContinuityJson),
            json.DeserializeList<string>(project.VisualContinuityRulesJson),
            renderStrategy ?? new RenderStrategyRecommendationDto("Balanced", project.QualityGoal, "Use existing render profiles based on shot intent.", true, "Preview extension only.", []));

        return ProductionPlanNormalizer.WithDurationMetadata(new ProductionPlanDto(project.Name, project.Logline, project.Genre, project.TargetDurationSeconds, visualStyle, characters, scenes, json.DeserializeList<AudioCueDto>(project.AudioCuesJson))
        {
            DirectorPlan = directorPlan,
            DirectorTreatment = project.DirectorTreatment,
            BeatSheet = directorPlan.StoryBeats,
            ActBreakdown = directorPlan.ActStructure,
            RenderStrategy = directorPlan.RenderStrategyRecommendation
        });
    }
}

public sealed record ProjectUpdateFields(
    string Title,
    string? Logline,
    string? Genre,
    int TargetDurationSeconds,
    string? VisualStylePrompt,
    string? NegativePrompt,
    string? CameraStyle,
    string? LightingStyle,
    string? ColorPalette,
    string AudioCuesJson,
    string? DirectorTreatment,
    string BeatSheetJson,
    string ActBreakdownJson,
    string CharacterBibleJson,
    string LocationBibleJson,
    string TimelineContinuityJson,
    string VisualContinuityRulesJson,
    string RenderStrategyRecommendationJson,
    string QualityGoal);
