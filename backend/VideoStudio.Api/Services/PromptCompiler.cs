using System.Text;
using System.Text.RegularExpressions;
using VideoStudio.Api.Domain;

namespace VideoStudio.Api.Services;

public sealed class PromptCompiler(ILogger<PromptCompiler> logger)
{
    private const string DefaultNegative = "low quality, blurry, low resolution, watermark, text, logo, extra fingers, deformed hands, distorted face, bad anatomy, duplicate body, flicker, jitter, inconsistent lighting, broken motion, frame tearing";
    private static readonly Regex CorporateTextRegex = new(@"\b[A-Z0-9]{2,}(?:\s+[A-Z0-9]{2,})+\b", RegexOptions.Compiled);

    public (string prompt, string negativePrompt) Compile(Project project, Scene scene, Shot shot, IReadOnlyCollection<Character> characters, RenderPreset preset, bool useCharacterReferenceInPrompt = false, bool isImageToVideo = false)
    {
        var prompt = new StringBuilder();
        var shotText = $"{shot.Action} {shot.Prompt} {scene.Summary}";
        var relevantCharacters = characters.Where(c =>
            shotText.Contains(c.Name, StringComparison.OrdinalIgnoreCase) ||
            scene.RequiredCharactersJson.Contains(c.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        prompt.Append("Cinematic fantasy film shot, ");
        prompt.Append($"one simple visible action: {ValueOr(shot.Action, scene.Summary)}. ");
        if (relevantCharacters.Count > 0)
        {
            prompt.Append("Main character visual locks: ");
            prompt.Append(string.Join("; ", relevantCharacters.Select(c => $"{c.Name}: {c.VisualPrompt}")));
            prompt.Append(". Preserve identical face, hair, age, clothing, body shape, silhouette, and signature props. ");
            logger.LogInformation(
                "character_reference_lock_applied projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} characterCount={CharacterCount}",
                project.Id,
                scene.Index,
                shot.Index,
                shot.Id,
                relevantCharacters.Count);
            if (useCharacterReferenceInPrompt)
            {
                var referenced = relevantCharacters.Where(c => !string.IsNullOrWhiteSpace(c.ReferenceImagePath)).ToList();
                if (referenced.Count > 0)
                {
                    prompt.Append("Use the uploaded character reference images only as identity guidance for the written prompt: preserve each referenced character's consistent face, clothing, silhouette, age, hair, outfit, and body proportions. ");
                    prompt.Append("Do not treat portrait references as the shot start frame or scene composition. ");
                    foreach (var character in referenced)
                    {
                        logger.LogInformation(
                            "shot_reference_image_selected projectId={ProjectId} sceneIndex={SceneIndex} shotIndex={ShotIndex} shotId={ShotId} characterId={CharacterId} imagePath={ImagePath}",
                            project.Id,
                            scene.Index,
                            shot.Index,
                            shot.Id,
                            character.Id,
                            character.ReferenceImagePath);
                    }
                }
            }
        }

        prompt.Append($"Location continuity: {ValueOr(scene.Location, "cinematic location")}, {ValueOr(scene.TimeOfDay, "motivated time of day")}, mood {ValueOr(scene.Mood, "focused")}. ");
        prompt.Append($"Camera: {ValueOr(shot.ShotType, "medium shot")}, {ValueOr(shot.CameraMotion, "slow controlled movement")}. ");
        prompt.Append(BuildMotionPrompt(scene, shot, isImageToVideo));
        prompt.Append($"Lighting and color: {ValueOr(project.LightingStyle, "motivated cinematic lighting")}, {ValueOr(project.ColorPalette, "natural cinematic colors")}. ");
        prompt.Append($"Style: {ValueOr(project.VisualStylePrompt, "photorealistic cinematic style")}. ");
        if (preset == RenderPreset.FastPreview && !ContainsExplicitCloseup(shot))
        {
            prompt.Append("Prefer medium or wide framing for consistency. ");
        }
        prompt.Append($"Maintain continuity with previous shot: {ValueOr(shot.ContinuityNotes, scene.Summary)}. ");
        prompt.Append("No subtitles, no text, no logos, no spoken dialogue in the visual prompt. ");

        var corePrompt = string.IsNullOrWhiteSpace(shot.Prompt) ? shot.Action : shot.Prompt;
        corePrompt = ReplaceReadableSignText(corePrompt);
        corePrompt = RemoveDialogueLikeText(corePrompt);
        prompt.Append($"Primary shot prompt: {corePrompt}.");

        var negative = BuildNegativePrompt(project.NegativePrompt, scene, shot, relevantCharacters);
        return (prompt.ToString().Trim(), negative);
    }

    private static string BuildNegativePrompt(string? projectNegative, Scene scene, Shot shot, IReadOnlyCollection<Character> relevantCharacters)
    {
        var parts = new List<string>
        {
            DefaultNegative
        };
        if (relevantCharacters.Count > 0)
        {
            parts.Add("different face, different hairstyle, different costume, changing age, changing ethnicity, changing body shape, inconsistent armor, inconsistent clothing colors, missing signature prop");
        }
        parts.Add("wrong era, modern objects, cars, neon signs, electric wires, inconsistent architecture, wrong weather, wrong time of day, location mismatch");
        parts.Add(BuildContextNegativePrompt(scene, shot));
        if (!string.IsNullOrWhiteSpace(projectNegative))
        {
            parts.Add(projectNegative);
        }
        if (!string.IsNullOrWhiteSpace(shot.NegativePrompt))
        {
            parts.Add(shot.NegativePrompt);
        }
        return string.Join(", ", string.Join(", ", parts)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildContextNegativePrompt(Scene scene, Shot shot)
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
            negatives.Add($"wrong location for {ValueOr(scene.Location, "the scene")}, mismatched mood, unrelated action, visual discontinuity from scene {scene.Index} shot {shot.Index}");
        }
        negatives.Add($"wrong scene index {scene.Index}, wrong shot action, unrelated characters, inconsistent continuity");
        return string.Join(", ", negatives);
    }

    private static string BuildMotionPrompt(Scene scene, Shot shot, bool isImageToVideo)
    {
        var context = $"{scene.Title} {scene.Summary} {scene.Location} {scene.Mood} {shot.Action} {shot.ShotType} {shot.CameraMotion}".ToLowerInvariant();
        var primaryAction = SelectPrimaryMotion(context, shot.Action);
        var environmentalMotion = SelectEnvironmentalMotion(context);
        var cameraMotion = ValueOr(shot.CameraMotion, "slow cinematic push-in");
        var prefix = isImageToVideo
            ? "Animate from the supplied keyframe with clear temporal motion, not a frozen still. "
            : "Create readable temporal motion. ";

        return $"{prefix}Motion plan: primary action: {primaryAction}; environmental motion: {environmentalMotion}; camera motion: {cameraMotion}. Avoid readable text, subtitles, logos, and dialogue captions. ";
    }

    private static string SelectPrimaryMotion(string context, string? fallbackAction)
    {
        if (ContainsAny(context, "walk", "step", "approach", "enter", "cross"))
        {
            return "the character takes one clear deliberate step";
        }
        if (ContainsAny(context, "sword", "blade", "duel", "battle", "guard"))
        {
            return "the character's hand moves toward the sword and posture tightens";
        }
        if (ContainsAny(context, "turn", "look", "glance", "watch"))
        {
            return "the character slowly turns their head and shifts their gaze";
        }
        if (ContainsAny(context, "torch", "fire", "flame", "candle"))
        {
            return "the character steadies their pose as firelight flickers across the face";
        }
        if (ContainsAny(context, "ride", "horse", "run", "charge"))
        {
            return "the body advances with controlled forward motion";
        }

        return ValueOr(fallbackAction, "the character performs one simple visible action");
    }

    private static string SelectEnvironmentalMotion(string context)
    {
        if (ContainsAny(context, "torch", "fire", "flame", "candle", "smoke"))
        {
            return "torch smoke drifts upward and flame light flickers";
        }
        if (ContainsAny(context, "cloak", "mountain", "wind", "cliff", "snow"))
        {
            return "cloak edges, hair, and loose fabric move in the wind";
        }
        if (ContainsAny(context, "battle", "dust", "road", "desert", "ruin"))
        {
            return "dust and small debris drift through the air";
        }
        if (ContainsAny(context, "forest", "tree", "river", "rain"))
        {
            return "leaves, mist, and background water move subtly";
        }

        return "subtle fabric, hair, dust, and atmospheric movement";
    }

    private static string ReplaceReadableSignText(string input)
    {
        return CorporateTextRegex.Replace(input, "a glowing corporate neon sign with unreadable abstract symbols");
    }

    private static string RemoveDialogueLikeText(string input)
    {
        return Regex.Replace(input, "\".*?\"", string.Empty).Trim();
    }

    private static bool ContainsExplicitCloseup(Shot shot)
    {
        var text = $"{shot.ShotType} {shot.Action} {shot.Prompt}";
        return text.Contains("close-up", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("close up", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("extreme close", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string ValueOr(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
