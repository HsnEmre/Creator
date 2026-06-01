using VideoStudio.Api.Contracts;

namespace VideoStudio.Api.Services;

public sealed class ProductionPlanNormalizer
{
    public const string DefaultNegativePrompt = "low quality, watermark, text, logo, distorted face, inconsistent character, bad hands, extra fingers, extra limbs, flicker, blurry, deformed body";

    public ProductionPlanDto Normalize(ProductionPlanDto plan, string projectTitle, int targetDurationSeconds)
    {
        var normalizedTarget = targetDurationSeconds > 0 ? targetDurationSeconds : plan.TargetDurationSeconds > 0 ? plan.TargetDurationSeconds : 60;
        var visualStyle = NormalizeVisualStyle(plan.VisualStyle);
        var characters = NormalizeCharacters(plan.Characters);
        var scenes = NormalizeScenes(plan.Scenes, characters, visualStyle.NegativePrompt);

        return plan with
        {
            Title = Required(plan.Title, projectTitle),
            TargetDurationSeconds = normalizedTarget,
            VisualStyle = visualStyle,
            Characters = characters,
            Scenes = scenes,
            AudioCues = plan.AudioCues ?? []
        };
    }

    private static VisualStyleDto NormalizeVisualStyle(VisualStyleDto? visualStyle)
    {
        return new VisualStyleDto(
            Required(visualStyle?.StylePrompt, "cinematic, coherent visual style, detailed production design"),
            Required(visualStyle?.NegativePrompt, DefaultNegativePrompt),
            Required(visualStyle?.CameraStyle, "cinematic camera language"),
            Required(visualStyle?.LightingStyle, "motivated cinematic lighting"),
            Required(visualStyle?.ColorPalette, "natural cinematic color palette"));
    }

    private static List<CharacterPlanDto> NormalizeCharacters(List<CharacterPlanDto>? characters)
    {
        return (characters ?? []).Select(c =>
        {
            var visual = Required(c.VisualPrompt, $"consistent visual description for {c.Name}");
            var rules = c.ContinuityRules is { Count: > 0 }
                ? c.ContinuityRules.Where(r => !string.IsNullOrWhiteSpace(r)).ToList()
                : [$"Keep this character visually consistent: {visual}", "Do not change age, face, hair, clothing, scars, glasses, beard, or accessories unless the story explicitly requires it."];

            return c with
            {
                Name = Required(c.Name, "Unnamed Character"),
                Role = Required(c.Role, "supporting character"),
                Personality = Required(c.Personality, "grounded and consistent personality"),
                VisualPrompt = visual,
                VoiceStyle = Required(c.VoiceStyle, $"natural voice style for {c.Name}"),
                ContinuityRules = rules,
                ReferenceImagePrompt = Required(c.ReferenceImagePrompt, BuildCharacterReferencePrompt(c, visual)),
                ReferenceImageNegativePrompt = Required(c.ReferenceImageNegativePrompt, DefaultImageNegativePrompt()),
                ReferenceStatus = string.IsNullOrWhiteSpace(c.ReferenceImageUrl) && string.IsNullOrWhiteSpace(c.ReferenceImagePath) ? "PromptReady" : "Ready"
            };
        }).ToList();
    }

    private static List<ScenePlanDto> NormalizeScenes(List<ScenePlanDto>? scenes, List<CharacterPlanDto> characters, string defaultNegativePrompt)
    {
        var sourceScenes = scenes is { Count: > 0 } ? scenes : [FallbackScene(defaultNegativePrompt)];
        var result = new List<ScenePlanDto>();

        for (var sceneIndex = 0; sceneIndex < sourceScenes.Count; sceneIndex++)
        {
            var scene = sourceScenes[sceneIndex];
            var shots = scene.Shots is { Count: > 0 } ? scene.Shots : [FallbackShot(defaultNegativePrompt)];
            var normalizedShots = new List<ShotPlanDto>();

            for (var shotIndex = 0; shotIndex < shots.Count; shotIndex++)
            {
                var shot = shots[shotIndex];
                normalizedShots.Add(shot with
                {
                    Index = shotIndex + 1,
                    DurationSeconds = Math.Clamp(shot.DurationSeconds <= 0 ? 5 : shot.DurationSeconds, 3, 6),
                    ShotType = Required(shot.ShotType, "medium cinematic shot"),
                    CameraMotion = Required(shot.CameraMotion, "slow controlled camera movement"),
                    Action = Required(shot.Action, "the scene action unfolds clearly"),
                    WanPrompt = Required(shot.WanPrompt, BuildFallbackWanPrompt(scene, characters)),
                    NegativePrompt = Required(shot.NegativePrompt, defaultNegativePrompt),
                    AudioCue = Required(shot.AudioCue, "natural scene ambience"),
                    ContinuityNotes = Required(shot.ContinuityNotes, "maintain character and environment continuity"),
                    StartImagePrompt = Required(shot.StartImagePrompt, BuildShotStartImagePrompt(scene, shot)),
                    StartImageNegativePrompt = Required(shot.StartImageNegativePrompt, DefaultImageNegativePrompt()),
                    StartImageStatus = string.IsNullOrWhiteSpace(shot.StartImageUrl) && string.IsNullOrWhiteSpace(shot.StartImagePath) ? "PromptReady" : "Ready"
                });
            }

            var requiredCharacters = scene.RequiredCharacters is { Count: > 0 }
                ? scene.RequiredCharacters.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : InferCharacters(scene, characters);

            result.Add(scene with
            {
                Index = sceneIndex + 1,
                Title = Required(scene.Title, $"Scene {sceneIndex + 1}"),
                Summary = Required(scene.Summary, "Scene summary"),
                Location = Required(scene.Location, "unspecified location"),
                TimeOfDay = Required(scene.TimeOfDay, "unspecified time of day"),
                Mood = Required(scene.Mood, "cinematic mood"),
                EstimatedDurationSeconds = scene.EstimatedDurationSeconds > 0 ? scene.EstimatedDurationSeconds : normalizedShots.Sum(s => s.DurationSeconds),
                RequiredCharacters = requiredCharacters,
                Shots = normalizedShots,
                DialogueLines = scene.DialogueLines ?? []
            });
        }

        return result;
    }

    private static List<string> InferCharacters(ScenePlanDto scene, List<CharacterPlanDto> characters)
    {
        var text = $"{scene.Summary} {string.Join(' ', scene.Shots.Select(s => $"{s.Action} {s.WanPrompt}"))}";
        return characters.Where(c => text.Contains(c.Name, StringComparison.OrdinalIgnoreCase)).Select(c => c.Name).ToList();
    }

    private static string BuildFallbackWanPrompt(ScenePlanDto scene, List<CharacterPlanDto> characters)
    {
        var locks = string.Join("; ", characters.Select(c => $"{c.Name}: {c.VisualPrompt}"));
        return $"English cinematic video prompt. Environment: {scene.Location}. Mood: {scene.Mood}. Action: {scene.Summary}. Camera: controlled cinematic camera motion. Lighting: cinematic lighting. Character visual locks: {locks}";
    }

    private static ScenePlanDto FallbackScene(string defaultNegativePrompt) => new(1, "Scene 1", "A clear cinematic scene based on the story.", "story location", "day", "focused", 5, [], [FallbackShot(defaultNegativePrompt)], []);

    private static ShotPlanDto FallbackShot(string defaultNegativePrompt) => new(1, 5, "medium shot", "slow push in", "the main action unfolds", "cinematic video, clear action, consistent characters, detailed environment, slow push in, cinematic lighting", defaultNegativePrompt, "natural ambience", "maintain visual continuity");

    private static string BuildCharacterReferencePrompt(CharacterPlanDto character, string visual)
    {
        return $"English character reference image, neutral cinematic background, full body and clear face, stable identity. Character: {character.Name}, role: {character.Role}. Visual lock: {visual}. Natural proportions, realistic face, consistent clothing, soft cinematic lighting, no readable text or logos.";
    }

    private static string BuildShotStartImagePrompt(ScenePlanDto scene, ShotPlanDto shot)
    {
        return $"English cinematic keyframe image for image-to-video. Scene: {scene.Title}. Location: {scene.Location}, time of day: {scene.TimeOfDay}, mood: {scene.Mood}. Composition: {shot.ShotType}, camera angle and motion intention: {shot.CameraMotion}. Action moment: {shot.Action}. Lighting: cinematic motivated lighting. Visual style: coherent film still, realistic faces and anatomy, no spoken dialogue, no readable text, no logos.";
    }

    public static string DefaultImageNegativePrompt() => "low quality, watermark, text, logo, readable letters, subtitles, distorted face, inconsistent face, inconsistent character, bad hands, extra fingers, extra limbs, flicker, blurry, deformed body, mutated face, asymmetrical eyes, broken anatomy";

    private static string Required(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
