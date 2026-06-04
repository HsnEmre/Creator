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
            json.Serialize(plan.AudioCues));
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
                ReferenceImageUrl = c.ReferenceImageUrl
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
                    StartImageUrl = sh.StartImageUrl
                }).ToList(),
                json.DeserializeList<DialogueLineDto>(s.DialogueLinesJson))
            {
                Id = s.Id
            })
            .ToList();

        return ProductionPlanNormalizer.WithDurationMetadata(new ProductionPlanDto(project.Name, project.Logline, project.Genre, project.TargetDurationSeconds, visualStyle, characters, scenes, json.DeserializeList<AudioCueDto>(project.AudioCuesJson)));
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
    string AudioCuesJson);
