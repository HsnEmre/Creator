using VideoStudio.Api.Contracts;

namespace VideoStudio.Api.Services;

public sealed class ProductionPlanNormalizer(ILogger<ProductionPlanNormalizer> logger)
{
    public const string DefaultNegativePrompt = "low quality, watermark, text, logo, distorted face, inconsistent character, bad hands, extra fingers, extra limbs, flicker, blurry, deformed body";

    private const string StrongNegativePrompt = "low quality, watermark, text, logo, readable letters, subtitles, distorted face, inconsistent face, inconsistent character, bad hands, extra fingers, extra limbs, flicker, blurry, deformed body, mutated face, asymmetrical eyes, broken anatomy";

    public ProductionPlanDto Normalize(ProductionPlanDto plan, string projectTitle, int targetDurationSeconds, Guid? projectId = null)
    {
        var normalizedTarget = targetDurationSeconds > 0 ? targetDurationSeconds : 60;
        var rules = DurationPlanningRules.For(normalizedTarget);

        logger.LogInformation(
            "storyboard_duration_validation_started projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} minScenes={MinimumScenes} targetSceneRange={TargetSceneMin}-{TargetSceneMax} minShots={MinimumShots} targetShotRange={TargetShotMin}-{TargetShotMax} minCoverageRatio={MinimumCoverageRatio}",
            projectId,
            normalizedTarget,
            rules.MinimumScenes,
            rules.TargetSceneMin,
            rules.TargetSceneMax,
            rules.MinimumShots,
            rules.TargetShotMin,
            rules.TargetShotMax,
            rules.MinimumCoverageRatio);

        var title = Required(plan.Title, projectTitle);
        var visualStyle = NormalizeVisualStyle(plan.VisualStyle);
        var characters = NormalizeCharacters(plan.Characters);
        var scenes = NormalizeScenes(plan.Scenes, normalizedTarget, visualStyle, characters, rules);
        var normalized = new ProductionPlanDto(
            title,
            plan.Logline,
            plan.Genre,
            normalizedTarget,
            visualStyle,
            characters,
            scenes,
            plan.AudioCues ?? []);

        var validation = ValidateDurationPlan(normalized, rules);
        if (!validation.IsValid)
        {
            logger.LogWarning(
                "storyboard_duration_validation_failed projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} sceneCount={SceneCount} shotCount={ShotCount} totalPlannedDurationSeconds={TotalPlannedDurationSeconds} minScenes={MinimumScenes} minShots={MinimumShots} minimumPlannedDurationSeconds={MinimumPlannedDurationSeconds} warning={Warning}",
                projectId,
                normalizedTarget,
                validation.SceneCount,
                validation.ShotCount,
                validation.TotalPlannedDurationSeconds,
                rules.MinimumScenes,
                rules.MinimumShots,
                validation.MinimumPlannedDurationSeconds,
                validation.Warning);
            logger.LogInformation(
                "storyboard_duration_repair_started projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} sceneCount={SceneCount} shotCount={ShotCount} totalPlannedDurationSeconds={TotalPlannedDurationSeconds}",
                projectId,
                normalizedTarget,
                validation.SceneCount,
                validation.ShotCount,
                validation.TotalPlannedDurationSeconds);

            normalized = RepairDurationPlan(normalized, visualStyle, characters, rules);
            validation = ValidateDurationPlan(normalized, rules);

            logger.LogInformation(
                "storyboard_duration_repair_completed projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} sceneCount={SceneCount} shotCount={ShotCount} totalPlannedDurationSeconds={TotalPlannedDurationSeconds} minimumPlannedDurationSeconds={MinimumPlannedDurationSeconds} isDurationPlanValid={IsDurationPlanValid}",
                projectId,
                normalizedTarget,
                validation.SceneCount,
                validation.ShotCount,
                validation.TotalPlannedDurationSeconds,
                validation.MinimumPlannedDurationSeconds,
                validation.IsValid);
        }

        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"Production plan is too short for the requested duration. {validation.Warning}");
        }

        logger.LogInformation(
            "storyboard_duration_validation_completed projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} sceneCount={SceneCount} shotCount={ShotCount} totalPlannedDurationSeconds={TotalPlannedDurationSeconds} plannedDurationCoveragePercent={CoveragePercent} isDurationPlanValid={IsDurationPlanValid}",
            projectId,
            normalizedTarget,
            validation.SceneCount,
            validation.ShotCount,
            validation.TotalPlannedDurationSeconds,
            validation.CoveragePercent,
            validation.IsValid);

        return WithDurationMetadata(normalized, validation.Warning);
    }

    public static ProductionPlanDto WithDurationMetadata(ProductionPlanDto plan, string? warning = null)
    {
        var sceneCount = plan.Scenes.Count;
        var shotCount = plan.Scenes.Sum(s => s.Shots.Count);
        var plannedDuration = plan.Scenes.Sum(s => s.Shots.Sum(sh => Math.Max(0, sh.DurationSeconds)));
        var target = plan.TargetDurationSeconds > 0 ? plan.TargetDurationSeconds : 60;
        var coverage = target > 0 ? (int)Math.Round((double)plannedDuration / target * 100) : 0;
        var rules = DurationPlanningRules.For(target);
        var validation = ValidateDurationPlan(plan, rules);
        return plan with
        {
            SceneCount = sceneCount,
            ShotCount = shotCount,
            TotalPlannedDurationSeconds = plannedDuration,
            PlannedDurationCoveragePercent = coverage,
            IsDurationPlanValid = validation.IsValid,
            DurationPlanWarning = warning ?? validation.Warning
        };
    }

    public static string DefaultImageNegativePrompt()
    {
        return StrongNegativePrompt;
    }

    private ProductionPlanDto RepairDurationPlan(
        ProductionPlanDto plan,
        VisualStyleDto visualStyle,
        List<CharacterPlanDto> characters,
        DurationPlanningRules rules)
    {
        if (!rules.IsLongForm && !(plan.TargetDurationSeconds >= 60 && plan.Scenes.Count <= 1 && plan.Scenes.Sum(s => s.Shots.Count) <= 1))
        {
            return plan;
        }

        var desiredSceneCount = rules.IsLongForm
            ? Math.Max(plan.Scenes.Count, rules.TargetSceneMin)
            : Math.Max(plan.Scenes.Count, 2);
        desiredSceneCount = Math.Min(desiredSceneCount, rules.TargetSceneMax);

        var coverageSeconds = (int)Math.Ceiling(plan.TargetDurationSeconds * rules.MinimumCoverageRatio);
        var shotsForCoverage = (int)Math.Ceiling((double)coverageSeconds / Math.Max(1, rules.MaxShotSeconds));
        var desiredShotCount = rules.IsLongForm
            ? Math.Max(rules.TargetShotMin, Math.Max(plan.Scenes.Sum(s => s.Shots.Count), shotsForCoverage))
            : Math.Max(3, plan.Scenes.Sum(s => s.Shots.Count));
        desiredShotCount = Math.Min(desiredShotCount, rules.TargetShotMax);
        desiredShotCount = Math.Max(desiredShotCount, rules.MinimumShots);

        var desiredShotDuration = Math.Clamp((int)Math.Ceiling((double)coverageSeconds / desiredShotCount), rules.MinShotSeconds, rules.MaxShotSeconds);
        var sourceScenes = plan.Scenes.Count > 0 ? plan.Scenes.OrderBy(s => s.Index).ToList() : [FallbackScene(plan.TargetDurationSeconds, rules)];
        var repairedScenes = new List<ScenePlanDto>();
        var baseShotsPerScene = desiredShotCount / desiredSceneCount;
        var extraShots = desiredShotCount % desiredSceneCount;

        for (var sceneIndex = 1; sceneIndex <= desiredSceneCount; sceneIndex++)
        {
            var sourceScene = sourceScenes[(sceneIndex - 1) % sourceScenes.Count];
            var sceneShotCount = baseShotsPerScene + (sceneIndex <= extraShots ? 1 : 0);
            sceneShotCount = Math.Max(1, sceneShotCount);
            var shots = BuildRepairedShots(sourceScene, sceneShotCount, desiredShotDuration, sceneIndex, visualStyle, characters);
            var sourceDialogue = sceneIndex <= sourceScenes.Count ? sourceScene.DialogueLines : [];
            var title = sceneIndex <= sourceScenes.Count
                ? Required(sourceScene.Title, $"Scene {sceneIndex}")
                : $"Scene {sceneIndex}: {Required(sourceScene.Title, "Continuation")}";
            var summary = sceneIndex <= sourceScenes.Count
                ? Required(sourceScene.Summary, "A clear narrative beat.")
                : $"Continuation of the long-form narrative beat: {Required(sourceScene.Summary, Required(sourceScene.Title, "the story advances"))}";
            repairedScenes.Add(new ScenePlanDto(
                sceneIndex,
                title,
                summary,
                Required(sourceScene.Location, "cinematic story location"),
                Required(sourceScene.TimeOfDay, "motivated time of day"),
                Required(sourceScene.Mood, "cinematic tension"),
                shots.Sum(s => s.DurationSeconds),
                NormalizeRequiredCharacters(sourceScene.RequiredCharacters, sourceScene, characters),
                shots,
                sourceDialogue));
        }

        return plan with { Scenes = repairedScenes };
    }

    private static List<ShotPlanDto> BuildRepairedShots(
        ScenePlanDto scene,
        int count,
        int durationSeconds,
        int sceneIndex,
        VisualStyleDto visualStyle,
        List<CharacterPlanDto> characters)
    {
        var sourceShots = scene.Shots.Count > 0 ? scene.Shots.OrderBy(s => s.Index).ToList() : [FallbackShot(scene, durationSeconds)];
        var requiredCharacters = NormalizeRequiredCharacters(scene.RequiredCharacters, scene, characters);
        var characterLocks = BuildCharacterLocks(requiredCharacters, characters);
        var shots = new List<ShotPlanDto>();

        for (var shotIndex = 1; shotIndex <= count; shotIndex++)
        {
            var source = sourceShots[(shotIndex - 1) % sourceShots.Count];
            var action = Required(source.Action, Required(scene.Summary, "A focused cinematic action beat."));
            if (shotIndex > sourceShots.Count)
            {
                action = $"A distinct continuation beat in the same scene: {action}";
            }

            var shotType = Required(source.ShotType, shotIndex % 3 == 0 ? "wide shot" : "medium shot");
            var cameraMotion = Required(source.CameraMotion, shotIndex % 2 == 0 ? "slow lateral tracking movement" : "gentle cinematic push-in");
            var wanPrompt = BuildWanPrompt(scene, visualStyle, action, shotType, cameraMotion, characterLocks);
            var startImagePrompt = BuildShotStartImagePrompt(scene, visualStyle, action, shotType, cameraMotion, characterLocks);
            shots.Add(new ShotPlanDto(
                shotIndex,
                durationSeconds,
                shotType,
                cameraMotion,
                action,
                wanPrompt,
                MergeNegativePrompt(source.NegativePrompt),
                Required(source.AudioCue, "subtle scene ambience"),
                $"{Required(source.ContinuityNotes, "Maintain visual continuity.")} Keep character visual locks and location continuity stable across the long-form sequence.")
            {
                StartImagePrompt = Required(source.StartImagePrompt, startImagePrompt),
                StartImageNegativePrompt = Required(source.StartImageNegativePrompt, StrongNegativePrompt)
            });
        }

        return shots;
    }

    private static List<ScenePlanDto> NormalizeScenes(
        List<ScenePlanDto>? scenes,
        int targetDurationSeconds,
        VisualStyleDto visualStyle,
        List<CharacterPlanDto> characters,
        DurationPlanningRules rules)
    {
        var sourceScenes = scenes is { Count: > 0 } ? scenes.OrderBy(s => s.Index).ToList() : [FallbackScene(targetDurationSeconds, rules)];
        var normalized = new List<ScenePlanDto>();
        var sceneIndex = 1;
        foreach (var scene in sourceScenes)
        {
            var requiredCharacters = NormalizeRequiredCharacters(scene.RequiredCharacters, scene, characters);
            var shots = NormalizeShots(scene, visualStyle, characters, requiredCharacters, rules);
            normalized.Add(new ScenePlanDto(
                sceneIndex,
                Required(scene.Title, $"Scene {sceneIndex}"),
                Required(scene.Summary, "A clear narrative beat."),
                Required(scene.Location, "cinematic story location"),
                Required(scene.TimeOfDay, "motivated time of day"),
                Required(scene.Mood, "cinematic mood"),
                Math.Max(shots.Sum(s => s.DurationSeconds), Math.Clamp(scene.EstimatedDurationSeconds, rules.MinShotSeconds, Math.Max(rules.MaxShotSeconds, targetDurationSeconds))),
                requiredCharacters,
                shots,
                scene.DialogueLines ?? [])
            {
                Id = scene.Id
            });
            sceneIndex++;
        }

        return normalized;
    }

    private static List<ShotPlanDto> NormalizeShots(
        ScenePlanDto scene,
        VisualStyleDto visualStyle,
        List<CharacterPlanDto> characters,
        List<string> requiredCharacters,
        DurationPlanningRules rules)
    {
        var sourceShots = scene.Shots is { Count: > 0 } ? scene.Shots.OrderBy(s => s.Index).ToList() : [FallbackShot(scene, rules.MinShotSeconds)];
        var characterLocks = BuildCharacterLocks(requiredCharacters, characters);
        var shots = new List<ShotPlanDto>();
        var index = 1;
        foreach (var shot in sourceShots)
        {
            var duration = Math.Clamp(shot.DurationSeconds > 0 ? shot.DurationSeconds : rules.MinShotSeconds, rules.MinShotSeconds, rules.MaxShotSeconds);
            var shotType = Required(shot.ShotType, "medium shot");
            var cameraMotion = Required(shot.CameraMotion, "slow cinematic camera movement");
            var action = Required(shot.Action, Required(scene.Summary, "A focused cinematic action beat."));
            shots.Add(new ShotPlanDto(
                index,
                duration,
                shotType,
                cameraMotion,
                action,
                Required(shot.WanPrompt, BuildWanPrompt(scene, visualStyle, action, shotType, cameraMotion, characterLocks)),
                MergeNegativePrompt(shot.NegativePrompt),
                Required(shot.AudioCue, "subtle scene ambience"),
                Required(shot.ContinuityNotes, "Maintain character and scene continuity."))
            {
                Id = shot.Id,
                SceneId = shot.SceneId,
                StartImagePrompt = Required(shot.StartImagePrompt, BuildShotStartImagePrompt(scene, visualStyle, action, shotType, cameraMotion, characterLocks)),
                StartImageNegativePrompt = Required(shot.StartImageNegativePrompt, StrongNegativePrompt),
                StartImageStatus = shot.StartImageStatus,
                StartImagePath = shot.StartImagePath,
                StartImageUrl = shot.StartImageUrl
            });
            index++;
        }

        return shots;
    }

    private static VisualStyleDto NormalizeVisualStyle(VisualStyleDto? visualStyle)
    {
        return new VisualStyleDto(
            Required(visualStyle?.StylePrompt, "cinematic realism, coherent production design, stable character continuity"),
            MergeNegativePrompt(visualStyle?.NegativePrompt),
            Required(visualStyle?.CameraStyle, "measured cinematic camera language with readable blocking"),
            Required(visualStyle?.LightingStyle, "motivated cinematic lighting with consistent exposure"),
            Required(visualStyle?.ColorPalette, "cohesive filmic color palette"));
    }

    private static List<CharacterPlanDto> NormalizeCharacters(List<CharacterPlanDto>? characters)
    {
        if (characters is not { Count: > 0 })
        {
            return [];
        }

        return characters.Select(character =>
        {
            var name = Required(character.Name, "Character");
            var visualPrompt = Required(character.VisualPrompt, $"{name}, stable cinematic character design, consistent face, hair, clothing, and body type");
            var continuityRules = character.ContinuityRules is { Count: > 0 }
                ? character.ContinuityRules
                : [$"Keep {name}'s face, hair, age, clothing, silhouette, and accessories consistent.", $"Use this visual lock for every shot: {visualPrompt}"];
            return new CharacterPlanDto(
                name,
                Required(character.Role, "story character"),
                Required(character.Personality, "clear and consistent personality"),
                visualPrompt,
                Required(character.VoiceStyle, "natural cinematic voice"),
                continuityRules)
            {
                Id = character.Id,
                ReferenceImagePrompt = Required(character.ReferenceImagePrompt, BuildCharacterReferencePrompt(name, visualPrompt)),
                ReferenceImageNegativePrompt = Required(character.ReferenceImageNegativePrompt, StrongNegativePrompt),
                ReferenceStatus = character.ReferenceStatus,
                ReferenceImagePath = character.ReferenceImagePath,
                ReferenceImageUrl = character.ReferenceImageUrl
            };
        }).ToList();
    }

    private static DurationValidationResult ValidateDurationPlan(ProductionPlanDto plan, DurationPlanningRules rules)
    {
        var sceneCount = plan.Scenes.Count;
        var shotCount = plan.Scenes.Sum(scene => scene.Shots.Count);
        var plannedDuration = plan.Scenes.Sum(scene => scene.Shots.Sum(shot => Math.Max(0, shot.DurationSeconds)));
        var minimumDuration = (int)Math.Ceiling(plan.TargetDurationSeconds * rules.MinimumCoverageRatio);
        var isValid = rules.IsLongForm
            ? sceneCount >= rules.MinimumScenes && shotCount >= rules.MinimumShots && plannedDuration >= minimumDuration
            : sceneCount >= rules.MinimumScenes && shotCount >= rules.MinimumShots && !(plan.TargetDurationSeconds >= 60 && sceneCount <= 1 && shotCount <= 1);
        string? warning = null;
        if (!isValid)
        {
            warning = rules.IsLongForm
                ? $"Storyboard duration plan is short: {sceneCount} scenes, {shotCount} shots, {plannedDuration}s planned for a {plan.TargetDurationSeconds}s target. Required minimum is {rules.MinimumScenes} scenes, {rules.MinimumShots} shots, and {minimumDuration}s planned."
                : $"Storyboard plan is too thin for this target: {sceneCount} scenes and {shotCount} shots. Add more beats or analyze again.";
        }

        return new DurationValidationResult(sceneCount, shotCount, plannedDuration, minimumDuration, plan.TargetDurationSeconds > 0 ? (int)Math.Round((double)plannedDuration / plan.TargetDurationSeconds * 100) : 0, isValid, warning);
    }

    private static ScenePlanDto FallbackScene(int targetDurationSeconds, DurationPlanningRules rules)
    {
        var duration = Math.Clamp(targetDurationSeconds, rules.MinShotSeconds, rules.MaxShotSeconds);
        return new ScenePlanDto(1, "Opening Scene", "A clear cinematic opening beat.", "cinematic story location", "motivated time of day", "focused cinematic mood", duration, [], [FallbackShot(null, duration)], []);
    }

    private static ShotPlanDto FallbackShot(ScenePlanDto? scene, int durationSeconds)
    {
        var action = scene is null ? "A focused cinematic action beat." : Required(scene.Summary, "A focused cinematic action beat.");
        return new ShotPlanDto(1, durationSeconds, "medium shot", "slow cinematic camera movement", action, action, StrongNegativePrompt, "subtle ambience", "Maintain visual continuity.");
    }

    private static string BuildCharacterReferencePrompt(string name, string visualPrompt)
    {
        return $"English character reference image, neutral cinematic background, clear face, stable clothing and identity. Character: {name}. Visual lock: {visualPrompt}. No readable text, no logo.";
    }

    private static string BuildWanPrompt(ScenePlanDto scene, VisualStyleDto visualStyle, string action, string shotType, string cameraMotion, string characterLocks)
    {
        var characterText = string.IsNullOrWhiteSpace(characterLocks) ? "No dialogue text or readable signage." : $"Character visual locks: {characterLocks}.";
        return $"English Wan2.2 video prompt. {characterText} Scene environment: {Required(scene.Location, "cinematic environment")}, {Required(scene.TimeOfDay, "motivated time of day")}. Action: {action}. Shot type: {shotType}. Camera motion: {cameraMotion}. Mood: {Required(scene.Mood, "cinematic mood")}. Style: {visualStyle.StylePrompt}. Lighting: {visualStyle.LightingStyle}. Realistic faces, stable identity, consistent clothing, coherent anatomy, no readable text or logos.";
    }

    private static string BuildShotStartImagePrompt(ScenePlanDto scene, VisualStyleDto visualStyle, string action, string shotType, string cameraMotion, string characterLocks)
    {
        var characterText = string.IsNullOrWhiteSpace(characterLocks) ? "" : $"Character visual locks: {characterLocks}. ";
        return $"English keyframe image prompt. {characterText}Environment: {Required(scene.Location, "cinematic environment")}, {Required(scene.TimeOfDay, "motivated time of day")}. Composition: {shotType}. Character placement and action: {action}. Camera angle and motion intent: {cameraMotion}. Mood: {Required(scene.Mood, "cinematic mood")}. Visual style: {visualStyle.StylePrompt}. Lighting: {visualStyle.LightingStyle}. No spoken dialogue, no readable text, no logos, no subtitles.";
    }

    private static List<string> NormalizeRequiredCharacters(List<string>? requiredCharacters, ScenePlanDto scene, List<CharacterPlanDto> characters)
    {
        if (requiredCharacters is { Count: > 0 })
        {
            return requiredCharacters.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var text = $"{scene.Summary} {string.Join(" ", scene.Shots.Select(s => s.Action))}";
        return characters
            .Where(character => text.Contains(character.Name, StringComparison.OrdinalIgnoreCase))
            .Select(character => character.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildCharacterLocks(List<string> requiredCharacters, List<CharacterPlanDto> characters)
    {
        var locks = characters
            .Where(character => requiredCharacters.Contains(character.Name, StringComparer.OrdinalIgnoreCase))
            .Select(character => $"{character.Name}: {character.VisualPrompt}");
        return string.Join(" ", locks);
    }

    private static string MergeNegativePrompt(string? negativePrompt)
    {
        if (string.IsNullOrWhiteSpace(negativePrompt))
        {
            return StrongNegativePrompt;
        }

        var merged = negativePrompt;
        foreach (var term in StrongNegativePrompt.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!merged.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                merged += $", {term}";
            }
        }

        return merged;
    }

    private static string Required(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private sealed record DurationValidationResult(
        int SceneCount,
        int ShotCount,
        int TotalPlannedDurationSeconds,
        int MinimumPlannedDurationSeconds,
        int CoveragePercent,
        bool IsValid,
        string? Warning);

    private sealed record DurationPlanningRules(
        int MinimumScenes,
        int TargetSceneMin,
        int TargetSceneMax,
        int MinimumShots,
        int TargetShotMin,
        int TargetShotMax,
        int MinShotSeconds,
        int MaxShotSeconds,
        double MinimumCoverageRatio,
        bool IsLongForm)
    {
        public static DurationPlanningRules For(int targetDurationSeconds)
        {
            if (targetDurationSeconds >= 420)
            {
                return new DurationPlanningRules(14, 18, 24, 50, 60, 84, 5, 8, 0.85, true);
            }

            if (targetDurationSeconds >= 300)
            {
                return new DurationPlanningRules(12, 14, 18, 40, 45, 60, 5, 8, 0.90, true);
            }

            if (targetDurationSeconds >= 180)
            {
                return new DurationPlanningRules(8, 10, 14, 24, 30, 36, 5, 8, 0.90, true);
            }

            return new DurationPlanningRules(1, 1, 6, 1, 1, 18, 3, 6, 0.50, false);
        }
    }
}
