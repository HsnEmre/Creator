using VideoStudio.Api.Contracts;
using VideoStudio.Api.Domain;

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

        normalized = ValidateAndRepairNegativePrompts(normalized, characters, projectId);
        normalized = ApplyDirectorPlanning(normalized, projectId);

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
        var distinctNegativePrompts = plan.Scenes
            .SelectMany(s => s.Shots)
            .Select(s => NormalizePromptKey(s.NegativePrompt))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var duplicateNegativePromptGroups = plan.Scenes
            .SelectMany(s => s.Shots)
            .GroupBy(s => NormalizePromptKey(s.NegativePrompt), StringComparer.OrdinalIgnoreCase)
            .Count(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1);
        var characterLocksApplied = plan.Characters.Count > 0
            && plan.Scenes.SelectMany(s => s.Shots).All(shot => ContainsAnyCharacterLock(shot.WanPrompt, plan.Characters) || !ShotLikelyHasCharacter(shot, plan.Characters));
        var continuityWarning = !characterLocksApplied && plan.Characters.Count > 0
            ? "Some shots may be missing character visual locks. Regenerate plan to apply the current continuity composer."
            : null;
        return plan with
        {
            SceneCount = sceneCount,
            ShotCount = shotCount,
            TotalPlannedDurationSeconds = plannedDuration,
            PlannedDurationCoveragePercent = coverage,
            IsDurationPlanValid = validation.IsValid,
            DurationPlanWarning = warning ?? validation.Warning,
            HasContinuityBible = plan.Characters.Count > 0 && plan.Characters.All(c => !string.IsNullOrWhiteSpace(c.VisualPrompt) && c.ContinuityRules.Count > 0),
            CharacterVisualLocksApplied = characterLocksApplied,
            DistinctNegativePromptCount = distinctNegativePrompts,
            DuplicateNegativePromptGroups = duplicateNegativePromptGroups,
            ContinuityWarning = continuityWarning,
            HasDirectorPlan = plan.DirectorPlan is not null && !string.IsNullOrWhiteSpace(plan.DirectorTreatment),
            StoryStructureValid = plan.BeatSheet.Count > 0 && plan.ActBreakdown.Count > 0 && sceneCount > 0 && shotCount > 0,
            LocationContinuityValid = plan.DirectorPlan?.LocationBible.Count > 0 && plan.Scenes.All(scene => !string.IsNullOrWhiteSpace(scene.LocationId)),
            KeyframeContinuityValid = plan.Scenes.SelectMany(scene => scene.Shots).All(shot => !string.IsNullOrWhiteSpace(shot.KeyframeContinuityPrompt)),
            RenderStrategyName = plan.RenderStrategy?.Name ?? plan.DirectorPlan?.RenderStrategyRecommendation.Name,
            AssemblyExtensionPolicy = plan.RenderStrategy?.ExtensionPolicy ?? plan.DirectorPlan?.RenderStrategyRecommendation.ExtensionPolicy
        };
    }

    private ProductionPlanDto ApplyDirectorPlanning(ProductionPlanDto plan, Guid? projectId)
    {
        var sceneCount = plan.Scenes.Count;
        var shotCount = plan.Scenes.Sum(scene => scene.Shots.Count);
        var uniqueSceneBeatCount = plan.Scenes.Select(scene => NormalizePromptKey(scene.Purpose ?? scene.Summary ?? scene.Title)).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var duplicateShotActionCount = plan.Scenes
            .SelectMany(scene => scene.Shots)
            .GroupBy(shot => NormalizePromptKey(shot.Action), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .Sum(group => group.Count() - 1);
        var repeatedSceneCycleDetected = uniqueSceneBeatCount > 0 && sceneCount >= 4 && uniqueSceneBeatCount <= Math.Max(2, sceneCount / 3);
        var characterContinuityValid = plan.Characters.Count == 0 || plan.Characters.All(c => c.Bible is not null || (!string.IsNullOrWhiteSpace(c.VisualPrompt) && c.ContinuityRules.Count > 0));
        var locationContinuityValid = plan.Scenes.All(scene => !string.IsNullOrWhiteSpace(scene.LocationId ?? Slug(scene.Location)));
        var keyframeContinuityValid = plan.Scenes.SelectMany(scene => scene.Shots).All(shot => !string.IsNullOrWhiteSpace(shot.KeyframeContinuityPrompt ?? shot.StartImagePrompt));

        logger.LogInformation(
            "director_plan_validation_started projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} sceneCount={SceneCount} shotCount={ShotCount} uniqueSceneBeatCount={UniqueSceneBeatCount} duplicateShotActionCount={DuplicateShotActionCount} repeatedSceneCycleDetected={RepeatedSceneCycleDetected} characterContinuityValid={CharacterContinuityValid} locationContinuityValid={LocationContinuityValid} keyframeContinuityValid={KeyframeContinuityValid}",
            projectId,
            plan.TargetDurationSeconds,
            sceneCount,
            shotCount,
            uniqueSceneBeatCount,
            duplicateShotActionCount,
            repeatedSceneCycleDetected,
            characterContinuityValid,
            locationContinuityValid,
            keyframeContinuityValid);

        var needsRepair = plan.DirectorPlan is null
            || string.IsNullOrWhiteSpace(plan.DirectorTreatment)
            || plan.BeatSheet.Count == 0
            || plan.ActBreakdown.Count == 0
            || repeatedSceneCycleDetected
            || !characterContinuityValid
            || !locationContinuityValid
            || !keyframeContinuityValid;

        if (needsRepair)
        {
            logger.LogWarning(
                "director_plan_validation_failed projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} sceneCount={SceneCount} shotCount={ShotCount} uniqueSceneBeatCount={UniqueSceneBeatCount} duplicateShotActionCount={DuplicateShotActionCount} repeatedSceneCycleDetected={RepeatedSceneCycleDetected} characterContinuityValid={CharacterContinuityValid} locationContinuityValid={LocationContinuityValid} keyframeContinuityValid={KeyframeContinuityValid}",
                projectId,
                plan.TargetDurationSeconds,
                sceneCount,
                shotCount,
                uniqueSceneBeatCount,
                duplicateShotActionCount,
                repeatedSceneCycleDetected,
                characterContinuityValid,
                locationContinuityValid,
                keyframeContinuityValid);
            logger.LogInformation("director_plan_repair_started projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds}", projectId, plan.TargetDurationSeconds);
            plan = BuildDirectorPlan(plan, projectId);
            logger.LogInformation("director_plan_repair_completed projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} sceneCount={SceneCount} shotCount={ShotCount}", projectId, plan.TargetDurationSeconds, plan.Scenes.Count, plan.Scenes.Sum(s => s.Shots.Count));
        }
        else
        {
            plan = BuildDirectorPlan(plan, projectId);
        }

        logger.LogInformation(
            "director_plan_validation_completed projectId={ProjectId} targetDurationSeconds={TargetDurationSeconds} sceneCount={SceneCount} shotCount={ShotCount} uniqueSceneBeatCount={UniqueSceneBeatCount} duplicateShotActionCount={DuplicateShotActionCount} repeatedSceneCycleDetected={RepeatedSceneCycleDetected} characterContinuityValid={CharacterContinuityValid} locationContinuityValid={LocationContinuityValid} keyframeContinuityValid={KeyframeContinuityValid}",
            projectId,
            plan.TargetDurationSeconds,
            plan.Scenes.Count,
            plan.Scenes.Sum(scene => scene.Shots.Count),
            plan.Scenes.Select(scene => NormalizePromptKey(scene.Purpose ?? scene.Summary ?? scene.Title)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            plan.Scenes.SelectMany(scene => scene.Shots).GroupBy(shot => NormalizePromptKey(shot.Action), StringComparer.OrdinalIgnoreCase).Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1).Sum(group => group.Count() - 1),
            false,
            true,
            true,
            true);

        return WithDurationMetadata(plan);
    }

    private ProductionPlanDto BuildDirectorPlan(ProductionPlanDto plan, Guid? projectId)
    {
        logger.LogInformation("director_keyframe_continuity_started projectId={ProjectId} sceneCount={SceneCount} shotCount={ShotCount}", projectId, plan.Scenes.Count, plan.Scenes.Sum(s => s.Shots.Count));

        var characterBible = plan.Characters.Select(BuildCharacterBible).ToList();
        var characters = plan.Characters.Select(character => character with
        {
            Bible = characterBible.FirstOrDefault(bible => bible.Name.Equals(character.Name, StringComparison.OrdinalIgnoreCase))
        }).ToList();
        var locationBible = BuildLocationBible(plan);
        var acts = BuildActs(plan.Scenes);
        var beats = BuildBeats(plan.Scenes);
        var renderStrategy = BuildRenderStrategy(plan.TargetDurationSeconds);
        var scenes = new List<ScenePlanDto>();

        foreach (var scene in plan.Scenes.OrderBy(s => s.Index))
        {
            var location = locationBible.FirstOrDefault(l => l.Name.Equals(scene.Location, StringComparison.OrdinalIgnoreCase)) ?? locationBible.FirstOrDefault();
            var locationId = location?.LocationId ?? Slug(scene.Location);
            var sceneAnchor = BuildSceneAnchor(plan, scene, location, characters);
            logger.LogInformation("director_keyframe_scene_anchor_created projectId={ProjectId} sceneIndex={SceneIndex} locationId={LocationId}", projectId, scene.Index, locationId);

            var previousState = $"Scene {scene.Index} opens from story state: {Required(scene.StoryStateBefore, SceneStateBefore(scene))}.";
            var plannedShots = new List<ShotPlanDto>();
            foreach (var shot in scene.Shots.OrderBy(s => s.Index))
            {
                var involvedCharacters = FindInvolvedCharacters(scene, shot, characters);
                var characterLock = BuildDirectorCharacterLock(involvedCharacters, characters);
                var locationLock = $"{location?.Name ?? scene.Location}: {location?.VisualDescription ?? scene.Location}, {location?.TimeOfDay ?? scene.TimeOfDay}, {location?.Weather ?? "motivated weather"}, recurring props: {location?.RecurringProps ?? "stable set dressing"}";
                var currentState = $"Scene {scene.Index} shot {shot.Index}: {Required(shot.Action, scene.Summary)} in {scene.Location}.";
                var nextSetup = shot.Index < scene.Shots.Count
                    ? $"Continue the same scene geography, character blocking, lighting, and emotional direction into shot {shot.Index + 1}."
                    : $"Prepare the transition from scene {scene.Index} to the next story beat without changing character identity.";
                logger.LogInformation("director_keyframe_previous_state_applied projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex}", projectId, scene.Index, shot.Index);
                logger.LogInformation("director_keyframe_image_reference_unavailable projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} reason={Reason}", projectId, scene.Index, shot.Index, "current still-image adapter is text-conditioned; prior keyframes are represented as text continuity prompts");

                var keyframePrompt = $"Connected keyframe for scene {scene.Index}, shot {shot.Index}. Scene anchor: {sceneAnchor}. Previous visual state: {previousState}. Current visual state: {currentState}. Character locks: {characterLock}. Location lock: {locationLock}. Next shot setup: {nextSetup}. No readable text, logos, subtitles, or dialogue.";
                var recommendedMode = RecommendRenderDurationMode(shot, plan.TargetDurationSeconds);
                plannedShots.Add(shot with
                {
                    InvolvedCharacterIds = involvedCharacters.Select(c => c.Name).ToList(),
                    CharacterLockPrompt = characterLock,
                    LocationId = locationId,
                    LocationLockPrompt = locationLock,
                    ForbiddenDriftTerms = $"{location?.NegativeLocationDrift ?? "wrong location, wrong weather, wrong era"}, different face, different hair, different costume, unrelated character",
                    PreviousShotVisualState = previousState,
                    CurrentShotVisualState = currentState,
                    NextShotSetup = nextSetup,
                    KeyframeContinuityPrompt = keyframePrompt,
                    SceneAnchorPrompt = sceneAnchor,
                    RecommendedRenderDurationMode = recommendedMode.ToString(),
                    AssemblyExtensionAllowed = recommendedMode == RenderDurationMode.FastPreview || recommendedMode == RenderDurationMode.CinematicPreview,
                    ContinuityNotes = $"{Required(shot.ContinuityNotes, "Maintain continuity.")} Previous: {previousState} Current: {currentState} Next: {nextSetup}",
                    StartImagePrompt = $"{Required(shot.StartImagePrompt, keyframePrompt)} Connected continuity: {keyframePrompt}"
                });
                previousState = currentState;
            }

            scenes.Add(scene with
            {
                Purpose = Required(scene.Purpose, ScenePurpose(scene)),
                StoryStateBefore = Required(scene.StoryStateBefore, SceneStateBefore(scene)),
                StoryStateAfter = Required(scene.StoryStateAfter, SceneStateAfter(scene)),
                LocationId = locationId,
                SceneAnchorPrompt = sceneAnchor,
                LocationContinuityPrompt = location?.ContinuityNotes ?? $"Keep {scene.Location} visually stable across all shots in scene {scene.Index}.",
                ForbiddenLocationDrift = location?.NegativeLocationDrift ?? "wrong location, modern objects, unrelated architecture, wrong weather, wrong time of day",
                Shots = plannedShots
            });
        }

        logger.LogInformation("director_keyframe_continuity_completed projectId={ProjectId} sceneCount={SceneCount} shotCount={ShotCount}", projectId, scenes.Count, scenes.Sum(s => s.Shots.Count));

        var treatment = Required(plan.DirectorTreatment, BuildTreatment(plan));
        var directorPlan = new DirectorPlanDto(
            plan.Title,
            plan.Logline ?? string.Empty,
            plan.Genre ?? string.Empty,
            Required(plan.VisualStyle.StylePrompt, "cinematic realism"),
            plan.TargetDurationSeconds,
            treatment,
            acts,
            beats,
            characterBible,
            locationBible,
            scenes.Select(scene => $"Scene {scene.Index}: {scene.StoryStateBefore} -> {scene.StoryStateAfter}").ToList(),
            [
                "Repeat character face, hair, costume, silhouette, and signature props in every relevant shot.",
                "Keep repeated locations anchored by time of day, weather, architecture, and recurring props.",
                "Connected keyframes must inherit scene anchor, previous visual state, and next-shot setup."
            ],
            renderStrategy);

        return plan with
        {
            Characters = characters,
            Scenes = scenes,
            DirectorPlan = directorPlan,
            DirectorTreatment = treatment,
            BeatSheet = beats,
            ActBreakdown = acts,
            RenderStrategy = renderStrategy
        };
    }

    private static CharacterBibleDto BuildCharacterBible(CharacterPlanDto character)
    {
        var visual = Required(character.VisualPrompt, $"{character.Name}, stable cinematic identity");
        return character.Bible ?? new CharacterBibleDto(
            Slug(character.Name),
            character.Name,
            Required(character.Role, "story character"),
            visual,
            visual,
            visual,
            visual,
            "signature props remain stable unless the story explicitly changes them",
            visual,
            "different face, different hair, different age, different costume, missing signature prop, changed silhouette",
            Required(character.ReferenceImagePrompt, BuildCharacterReferencePrompt(character.Name, visual)),
            Required(character.ReferenceImageNegativePrompt, StrongNegativePrompt));
    }

    private static List<LocationBibleDto> BuildLocationBible(ProductionPlanDto plan)
    {
        return plan.Scenes
            .GroupBy(scene => Required(scene.Location, "story location"), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new LocationBibleDto(
                    Slug(first.Location),
                    Required(first.Location, "story location"),
                    $"{Required(first.Location, "story location")}, {Required(first.Mood, "cinematic mood")}, {plan.VisualStyle.ColorPalette}",
                    Required(first.TimeOfDay, "motivated time of day"),
                    "story-motivated weather, stable within repeated scenes",
                    "consistent architecture, materials, terrain, and set dressing",
                    "recurring props remain in the same visual world",
                    $"When returning to {first.Location}, preserve time of day, lighting logic, set dressing, and geography.",
                    "wrong location, unrelated architecture, wrong climate, wrong time of day, modern objects, mismatched props");
            })
            .ToList();
    }

    private static List<DirectorActDto> BuildActs(List<ScenePlanDto> scenes)
    {
        if (scenes.Count == 0)
        {
            return [new DirectorActDto("Act I", "Setup the story premise.", 1, 1)];
        }

        var last = scenes.Max(s => s.Index);
        var actOneEnd = Math.Max(1, (int)Math.Ceiling(last * 0.25));
        var actTwoEnd = Math.Max(actOneEnd, (int)Math.Ceiling(last * 0.75));
        return
        [
            new DirectorActDto("Act I", "Setup the premise, main character need, and first irreversible movement.", 1, actOneEnd),
            new DirectorActDto("Act II", "Escalate obstacles, deepen the conflict, and reach the midpoint reversal.", Math.Min(last, actOneEnd + 1), actTwoEnd),
            new DirectorActDto("Act III", "Resolve the confrontation and show consequence.", Math.Min(last, actTwoEnd + 1), last)
        ];
    }

    private static List<DirectorBeatDto> BuildBeats(List<ScenePlanDto> scenes)
    {
        return scenes.OrderBy(scene => scene.Index).Select(scene => new DirectorBeatDto(
            scene.Index,
            scene.Index == 1 ? "Opening setup" : scene.Index == scenes.Count ? "Resolution" : $"Beat {scene.Index}",
            Required(scene.Purpose, ScenePurpose(scene)),
            $"{Required(scene.StoryStateBefore, SceneStateBefore(scene))} -> {Required(scene.StoryStateAfter, SceneStateAfter(scene))}",
            scene.Index)).ToList();
    }

    private static RenderStrategyRecommendationDto BuildRenderStrategy(int targetDurationSeconds)
    {
        var qualityGoal = targetDurationSeconds >= 180 ? "Final quality / AutoQuality" : "Balanced";
        return new RenderStrategyRecommendationDto(
            "AutoQuality",
            qualityGoal,
            "Choose per-shot render duration from shot intent. Keep FastPreview for tests; use keyframe-driven longer profiles for final-quality motion.",
            false,
            "FastPreview may be duration-extended for previews. Final-quality modes must not hide one-second clips as long motion.",
            [
                "Establishing shots prefer ComfyUIParity when keyframes are available.",
                "Close emotional shots prefer LongMotion or ComfyUIParity with Image-to-Video keyframes.",
                "Fast action shots prefer CinematicPreview or LongMotion to avoid overloaded motion.",
                "Long films should add shots instead of making single shots extremely long."
            ]);
    }

    private static RenderDurationMode RecommendRenderDurationMode(ShotPlanDto shot, int targetDurationSeconds)
    {
        var text = $"{shot.ShotType} {shot.CameraMotion} {shot.Action}".ToLowerInvariant();
        if (ContainsAny(text, "establishing", "wide", "landscape", "arrival", "approach"))
        {
            return RenderDurationMode.ComfyUIParity;
        }

        if (ContainsAny(text, "close", "emotion", "face", "look", "tear", "dialogue"))
        {
            return targetDurationSeconds >= 180 ? RenderDurationMode.ComfyUIParity : RenderDurationMode.LongMotion;
        }

        if (ContainsAny(text, "fight", "battle", "run", "chase", "explosion", "fast"))
        {
            return RenderDurationMode.CinematicPreview;
        }

        return targetDurationSeconds >= 60 ? RenderDurationMode.LongMotion : RenderDurationMode.CinematicPreview;
    }

    private static string BuildTreatment(ProductionPlanDto plan)
    {
        var opening = plan.Scenes.FirstOrDefault()?.Summary ?? plan.Logline ?? plan.Title;
        var ending = plan.Scenes.LastOrDefault()?.Summary ?? "the story resolves with clear consequence";
        return $"{plan.Title} is shaped as a coherent short film: it opens with {opening}, escalates through distinct story beats, reaches a midpoint change, and resolves with {ending}. Each scene changes the story state instead of repeating the same visual situation.";
    }

    private static string BuildSceneAnchor(ProductionPlanDto plan, ScenePlanDto scene, LocationBibleDto? location, List<CharacterPlanDto> characters)
    {
        var required = NormalizeRequiredCharacters(scene.RequiredCharacters, scene, characters);
        var locks = BuildCharacterLocks(required, characters);
        return $"Scene {scene.Index} master keyframe anchor: {Required(scene.Location, "story location")}, {Required(scene.TimeOfDay, "motivated time")}, {Required(scene.Mood, "cinematic mood")}, {location?.ArchitectureMaterials ?? "consistent materials"}, {location?.RecurringProps ?? "stable props"}. Characters: {locks}. Style: {plan.VisualStyle.StylePrompt}.";
    }

    private static List<CharacterPlanDto> FindInvolvedCharacters(ScenePlanDto scene, ShotPlanDto shot, List<CharacterPlanDto> characters)
    {
        var names = NormalizeRequiredCharacters(scene.RequiredCharacters, scene, characters);
        var text = $"{shot.Action} {shot.WanPrompt} {shot.ContinuityNotes}";
        var matched = characters.Where(character => names.Contains(character.Name, StringComparer.OrdinalIgnoreCase) || text.Contains(character.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matched.Count > 0 ? matched : characters.Where(c => names.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private static string BuildDirectorCharacterLock(List<CharacterPlanDto> involvedCharacters, List<CharacterPlanDto> characters)
    {
        var source = involvedCharacters.Count > 0 ? involvedCharacters : characters.Take(1).ToList();
        return string.Join(" ", source.Select(character => $"{character.Name}: {character.Bible?.FaceLock ?? character.VisualPrompt}; costume lock: {character.Bible?.CostumeLock ?? character.VisualPrompt}; forbidden drift: {character.Bible?.ForbiddenDrift ?? "different face, different hair, different costume"}"));
    }

    private static string ScenePurpose(ScenePlanDto scene) => $"Advance the story through this beat: {Required(scene.Summary, scene.Title)}";
    private static string SceneStateBefore(ScenePlanDto scene) => scene.Index == 1 ? "The story premise is unresolved." : $"The previous beat leads into {Required(scene.Title, $"scene {scene.Index}")}.";
    private static string SceneStateAfter(ScenePlanDto scene) => $"The characters and situation are changed by {Required(scene.Summary, scene.Title)}.";
    private static string Slug(string? value) => string.IsNullOrWhiteSpace(value)
        ? "unknown"
        : string.Concat(value.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');

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
            var negativePrompt = BuildNegativePrompt(scene, source, requiredCharacters);
            shots.Add(new ShotPlanDto(
                shotIndex,
                durationSeconds,
                shotType,
                cameraMotion,
                action,
                wanPrompt,
                negativePrompt,
                Required(source.AudioCue, "subtle scene ambience"),
                $"{Required(source.ContinuityNotes, "Maintain visual continuity.")} Keep character visual locks and location continuity stable across the long-form sequence.")
            {
                StartImagePrompt = Required(source.StartImagePrompt, startImagePrompt),
                StartImageNegativePrompt = Required(source.StartImageNegativePrompt, BuildImageNegativePrompt(scene, source))
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
            var composedPrompt = BuildWanPrompt(scene, visualStyle, action, shotType, cameraMotion, characterLocks);
            var negativePrompt = BuildNegativePrompt(scene, shot, requiredCharacters);
            shots.Add(new ShotPlanDto(
                index,
                duration,
                shotType,
                cameraMotion,
                action,
                composedPrompt,
                negativePrompt,
                Required(shot.AudioCue, "subtle scene ambience"),
                Required(shot.ContinuityNotes, "Maintain character and scene continuity."))
            {
                Id = shot.Id,
                SceneId = shot.SceneId,
                StartImagePrompt = BuildShotStartImagePrompt(scene, visualStyle, action, shotType, cameraMotion, characterLocks),
                StartImageNegativePrompt = BuildImageNegativePrompt(scene, shot),
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
        return $"Cinematic fantasy film shot, one simple visible action: {action}. {characterText} Location continuity: {Required(scene.Location, "cinematic environment")}, {Required(scene.TimeOfDay, "motivated time of day")}, {Required(scene.Mood, "cinematic mood")}. Camera: {shotType}, {cameraMotion}. Lighting and color: {visualStyle.LightingStyle}, {visualStyle.ColorPalette}. Style: {visualStyle.StylePrompt}. Maintain continuity with previous shot: {Required(scene.Summary, "the same narrative beat continues")}. No subtitles, no text, no logos, no spoken dialogue in the visual prompt.";
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

    private ProductionPlanDto ValidateAndRepairNegativePrompts(ProductionPlanDto plan, List<CharacterPlanDto> characters, Guid? projectId)
    {
        var allShots = plan.Scenes.SelectMany(scene => scene.Shots.Select(shot => new { Scene = scene, Shot = shot })).ToList();
        var shotCount = allShots.Count;
        var distinctCount = allShots.Select(item => NormalizePromptKey(item.Shot.NegativePrompt)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var duplicateGroups = allShots
            .GroupBy(item => NormalizePromptKey(item.Shot.NegativePrompt), StringComparer.OrdinalIgnoreCase)
            .Count(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1);

        logger.LogInformation(
            "storyboard_negative_prompt_validation_started projectId={ProjectId} shotCount={ShotCount} distinctNegativePromptCount={DistinctNegativePromptCount} duplicateNegativePromptGroups={DuplicateNegativePromptGroups}",
            projectId,
            shotCount,
            distinctCount,
            duplicateGroups);

        var minimumDistinct = shotCount >= 12 ? Math.Min(shotCount, 6) : Math.Min(shotCount, 3);
        if (shotCount > 1 && distinctCount < minimumDistinct)
        {
            logger.LogWarning(
                "storyboard_negative_prompt_validation_failed projectId={ProjectId} shotCount={ShotCount} distinctNegativePromptCount={DistinctNegativePromptCount} duplicateNegativePromptGroups={DuplicateNegativePromptGroups}",
                projectId,
                shotCount,
                distinctCount,
                duplicateGroups);
            logger.LogInformation("storyboard_negative_prompt_repair_started projectId={ProjectId} shotCount={ShotCount}", projectId, shotCount);

            var repairedScenes = plan.Scenes
                .Select(scene => scene with
                {
                    Shots = scene.Shots.Select(shot =>
                    {
                        var involvedCharacters = NormalizeRequiredCharacters(scene.RequiredCharacters, scene, characters);
                        return shot with { NegativePrompt = BuildNegativePrompt(scene, shot, involvedCharacters) };
                    }).ToList()
                })
                .ToList();
            plan = plan with { Scenes = repairedScenes };
            allShots = plan.Scenes.SelectMany(scene => scene.Shots.Select(shot => new { Scene = scene, Shot = shot })).ToList();
            distinctCount = allShots.Select(item => NormalizePromptKey(item.Shot.NegativePrompt)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            duplicateGroups = allShots
                .GroupBy(item => NormalizePromptKey(item.Shot.NegativePrompt), StringComparer.OrdinalIgnoreCase)
                .Count(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1);

            logger.LogInformation(
                "storyboard_negative_prompt_repair_completed projectId={ProjectId} shotCount={ShotCount} distinctNegativePromptCount={DistinctNegativePromptCount} duplicateNegativePromptGroups={DuplicateNegativePromptGroups}",
                projectId,
                shotCount,
                distinctCount,
                duplicateGroups);
        }

        logger.LogInformation(
            "storyboard_negative_prompt_validation_completed projectId={ProjectId} shotCount={ShotCount} distinctNegativePromptCount={DistinctNegativePromptCount} duplicateNegativePromptGroups={DuplicateNegativePromptGroups}",
            projectId,
            shotCount,
            distinctCount,
            duplicateGroups);

        return plan;
    }

    private static string BuildNegativePrompt(ScenePlanDto scene, ShotPlanDto shot, List<string> involvedCharacters)
    {
        var parts = new List<string>
        {
            "low quality, blurry, low resolution, watermark, text, logo, extra fingers, deformed hands, distorted face, bad anatomy, duplicate body, flicker, jitter, inconsistent lighting, broken motion, frame tearing"
        };

        if (involvedCharacters.Count > 0)
        {
            parts.Add("different face, different hairstyle, different costume, changing age, changing ethnicity, changing body shape, inconsistent armor, inconsistent clothing colors, missing signature prop");
        }

        parts.Add("wrong era, modern objects, cars, neon signs, electric wires, inconsistent architecture, wrong weather, wrong time of day, location mismatch");
        parts.Add(BuildContextNegativePrompt(scene, shot));
        return string.Join(", ", parts)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Aggregate((current, next) => $"{current}, {next}");
    }

    private static string BuildImageNegativePrompt(ScenePlanDto scene, ShotPlanDto shot)
    {
        return $"{StrongNegativePrompt}, {BuildContextNegativePrompt(scene, shot)}";
    }

    private static string BuildContextNegativePrompt(ScenePlanDto scene, ShotPlanDto shot)
    {
        var text = $"{scene.Title} {scene.Summary} {scene.Location} {scene.Mood} {shot.Action} {shot.ShotType}".ToLowerInvariant();
        var negatives = new List<string>();
        if (ContainsAny(text, "battle", "war", "sword", "army", "combat", "siege", "duel"))
        {
            negatives.Add("peaceful empty battlefield, clean armor, static pose, no impact, toy weapons");
        }
        if (ContainsAny(text, "palace", "throne", "royal", "court", "castle"))
        {
            negatives.Add("outdoor landscape, modern furniture, office lighting, casual clothes");
        }
        if (ContainsAny(text, "night", "mountain", "snow", "cliff", "cave"))
        {
            negatives.Add("daylight, city skyline, beach, tropical plants");
        }
        if (ContainsAny(text, "village", "market", "street", "farm", "town"))
        {
            negatives.Add("futuristic buildings, asphalt road, cars, plastic objects");
        }
        if (ContainsAny(text, "forest", "woods", "tree", "river"))
        {
            negatives.Add("concrete buildings, fluorescent lights, urban traffic, plastic props");
        }

        if (negatives.Count == 0)
        {
            negatives.Add($"wrong location for {Required(scene.Location, "the scene")}, mismatched mood, unrelated action, visual discontinuity from scene {scene.Index} shot {shot.Index}");
        }

        negatives.Add($"wrong scene index {scene.Index}, wrong shot action, unrelated characters, inconsistent continuity");
        return string.Join(", ", negatives);
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

    private static bool ContainsAny(string text, params string[] terms)
    {
        return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAnyCharacterLock(string prompt, List<CharacterPlanDto> characters)
    {
        return characters.Any(character => prompt.Contains(character.Name, StringComparison.OrdinalIgnoreCase)
            && prompt.Contains(character.VisualPrompt, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShotLikelyHasCharacter(ShotPlanDto shot, List<CharacterPlanDto> characters)
    {
        var text = $"{shot.Action} {shot.WanPrompt} {shot.ContinuityNotes}";
        return characters.Any(character => text.Contains(character.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePromptKey(string? prompt)
    {
        return string.IsNullOrWhiteSpace(prompt) ? string.Empty : string.Join(" ", prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
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
