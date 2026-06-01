using System.Text;
using System.Text.RegularExpressions;
using VideoStudio.Api.Domain;

namespace VideoStudio.Api.Services;

public sealed class PromptCompiler
{
    private const string DefaultNegative = "low quality, watermark, text, logo, readable letters, subtitles, distorted face, inconsistent face, inconsistent character, bad hands, extra fingers, extra limbs, flicker, blurry, deformed body, mutated face, asymmetrical eyes, broken anatomy";
    private static readonly Regex CorporateTextRegex = new(@"\b[A-Z0-9]{2,}(?:\s+[A-Z0-9]{2,})+\b", RegexOptions.Compiled);

    public (string prompt, string negativePrompt) Compile(Project project, Scene scene, Shot shot, IReadOnlyCollection<Character> characters, RenderPreset preset, bool useCharacterReferenceInPrompt = false)
    {
        var prompt = new StringBuilder();
        prompt.Append("English cinematic video prompt. ");
        prompt.Append($"Visual style: {ValueOr(project.VisualStylePrompt, "photorealistic cinematic style")}. ");
        prompt.Append($"Camera style: {ValueOr(project.CameraStyle, "stable cinematic framing")}. ");
        prompt.Append($"Lighting: {ValueOr(project.LightingStyle, "motivated cinematic lighting")}. ");
        prompt.Append($"Color palette: {ValueOr(project.ColorPalette, "natural cinematic colors")}. ");
        prompt.Append($"Scene environment: {ValueOr(scene.Location, "interior location")}, {ValueOr(scene.TimeOfDay, "day")}, mood {ValueOr(scene.Mood, "focused")}. ");
        prompt.Append($"Shot type: {ValueOr(shot.ShotType, "medium shot")}. ");
        prompt.Append($"Camera motion: {ValueOr(shot.CameraMotion, "slow controlled movement")}. ");
        prompt.Append($"Action: {ValueOr(shot.Action, scene.Summary)}. ");

        var shotText = $"{shot.Action} {shot.Prompt} {scene.Summary}";
        var relevantCharacters = characters.Where(c =>
            shotText.Contains(c.Name, StringComparison.OrdinalIgnoreCase) ||
            scene.RequiredCharactersJson.Contains(c.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (relevantCharacters.Count > 0)
        {
            prompt.Append("Character visual locks: ");
            prompt.Append(string.Join("; ", relevantCharacters.Select(c => $"{c.Name}: {c.VisualPrompt}")));
            prompt.Append(". ");
            if (useCharacterReferenceInPrompt)
            {
                var referenced = relevantCharacters.Where(c => !string.IsNullOrWhiteSpace(c.ReferenceImagePath)).ToList();
                if (referenced.Count > 0)
                {
                    prompt.Append("Use the uploaded character reference images only as identity guidance for the written prompt: preserve each referenced character's consistent face, clothing, silhouette, age, hair, outfit, and body proportions. ");
                    prompt.Append("Do not treat portrait references as the shot start frame or scene composition. ");
                }
            }
        }

        prompt.Append("High realism, stable facial identity, consistent face structure, consistent hairstyle, natural skin texture. ");
        prompt.Append("Avoid readable words, signs, UI text, subtitles, or logos in the scene. ");
        if (preset == RenderPreset.FastPreview && !ContainsExplicitCloseup(shot))
        {
            prompt.Append("Prefer medium or wide framing for consistency. ");
        }

        var corePrompt = string.IsNullOrWhiteSpace(shot.Prompt) ? shot.Action : shot.Prompt;
        corePrompt = ReplaceReadableSignText(corePrompt);
        corePrompt = RemoveDialogueLikeText(corePrompt);
        prompt.Append($"Primary shot prompt: {corePrompt}.");

        var negative = BuildNegativePrompt(project.NegativePrompt, shot.NegativePrompt);
        return (prompt.ToString().Trim(), negative);
    }

    private static string BuildNegativePrompt(string? projectNegative, string? shotNegative)
    {
        var parts = new List<string> { DefaultNegative };
        if (!string.IsNullOrWhiteSpace(projectNegative))
        {
            parts.Add(projectNegative);
        }
        if (!string.IsNullOrWhiteSpace(shotNegative))
        {
            parts.Add(shotNegative);
        }
        return string.Join(", ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
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

    private static string ValueOr(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
