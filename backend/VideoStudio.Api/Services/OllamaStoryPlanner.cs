using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VideoStudio.Api.Contracts;
using VideoStudio.Api.Options;

namespace VideoStudio.Api.Services;

public sealed class OllamaStoryPlanner(
    HttpClient httpClient,
    IOptions<OllamaOptions> options,
    ProductionPlanJsonService jsonService,
    ProductionPlanNormalizer normalizer,
    ILogger<OllamaStoryPlanner> logger)
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public Task<StoryResultDto> CreatePlanAsync(string projectTitle, string storyText, int targetDurationSeconds, CancellationToken cancellationToken)
    {
        return CreatePlanAsync(null, projectTitle, storyText, targetDurationSeconds, cancellationToken);
    }

    public async Task<StoryResultDto> CreatePlanAsync(Guid? projectId, string projectTitle, string storyText, int targetDurationSeconds, CancellationToken cancellationToken)
    {
        await EnsureConfiguredModelExistsAsync(cancellationToken);

        var firstPrompt = BuildPlanningPrompt(projectTitle, storyText, targetDurationSeconds);
        var raw = await ChatAsync(firstPrompt.system, firstPrompt.user, cancellationToken);
        if (jsonService.TryParseProductionPlan(raw, out var plan, out var parseError) && plan is not null)
        {
            return new StoryResultDto(true, normalizer.Normalize(plan, projectTitle, targetDurationSeconds, projectId), null);
        }

        logger.LogWarning("Ollama returned invalid production plan JSON: {Error}. Response: {Response}", parseError, raw);

        var correctionPrompt = BuildCorrectionPrompt(raw, parseError ?? "Invalid JSON");
        var corrected = await ChatAsync(firstPrompt.system, correctionPrompt, cancellationToken);
        if (jsonService.TryParseProductionPlan(corrected, out plan, out parseError) && plan is not null)
        {
            return new StoryResultDto(true, normalizer.Normalize(plan, projectTitle, targetDurationSeconds, projectId), null);
        }

        logger.LogWarning("Ollama correction response was invalid: {Error}. Response: {Response}", parseError, corrected);
        return new StoryResultDto(false, null, "Ollama did not return valid production-plan JSON.");
    }

    private async Task<string> ChatAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var requestUri = BuildOllamaUri("/api/chat");
        var body = new
        {
            model = options.Value.Model,
            stream = false,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        logger.LogInformation("Sending Ollama chat request to {RequestUri}", requestUri);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(body, options: _jsonOptions)
        };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new OllamaRequestException($"Ollama request failed at {requestUri}: {(int)response.StatusCode} {response.ReasonPhrase}. Response body: {responseBody}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    public async Task<object> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var requestUri = BuildOllamaUri("/api/tags");
        logger.LogInformation("Sending Ollama diagnostics request to {RequestUri}", requestUri);

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new
                {
                    reachable = false,
                    configuredModel = options.Value.Model,
                    requestUri = requestUri.ToString(),
                    statusCode = (int)response.StatusCode,
                    responseBody
                };
            }

            var models = ParseModelNames(responseBody);
            return new
            {
                reachable = true,
                configuredModel = options.Value.Model,
                modelAvailable = models.Contains(options.Value.Model, StringComparer.OrdinalIgnoreCase),
                requestUri = requestUri.ToString(),
                models
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new
            {
                reachable = false,
                configuredModel = options.Value.Model,
                requestUri = requestUri.ToString(),
                error = ex.Message
            };
        }
    }

    private async Task EnsureConfiguredModelExistsAsync(CancellationToken cancellationToken)
    {
        var requestUri = BuildOllamaUri("/api/tags");
        logger.LogInformation("Checking Ollama model availability at {RequestUri}", requestUri);

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new OllamaRequestException($"Ollama diagnostics request failed at {requestUri}: {(int)response.StatusCode} {response.ReasonPhrase}. Response body: {responseBody}");
            }

            var models = ParseModelNames(responseBody);
            if (!models.Contains(options.Value.Model, StringComparer.OrdinalIgnoreCase))
            {
                throw new OllamaRequestException($"Configured Ollama model '{options.Value.Model}' was not found. Run: ollama pull {options.Value.Model}");
            }
        }
        catch (OllamaRequestException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new OllamaRequestException($"Ollama is not reachable at {requestUri}. Ensure Ollama is running. Details: {ex.Message}");
        }
    }

    private Uri BuildOllamaUri(string path)
    {
        var baseUri = new Uri(options.Value.BaseUrl.TrimEnd('/') + "/");
        return new Uri(baseUri.GetLeftPart(UriPartial.Authority) + path);
    }

    private static List<string> ParseModelNames(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return models
            .EnumerateArray()
            .Select(model => model.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();
    }

    private static (string system, string user) BuildPlanningPrompt(string projectTitle, string storyText, int targetDurationSeconds)
    {
        var durationGuidance = BuildDurationGuidance(targetDurationSeconds);
        var system = """
You convert stories into production-ready AI video plans.
Return ONLY valid JSON.
No markdown.
No explanations.
No comments.
No trailing text.
Do not ask follow-up questions.
Make a best-effort structured plan from the available story.
For short projects below 180 seconds, shots should usually be between 3 and 6 seconds.
For long-form projects of 180 seconds or more, every shot must be between 5 and 8 seconds.
Character visual descriptions must stay consistent across all scenes and shots.
For every character, keep age, face, hair, clothing, and personality stable.
Treat every character as part of a continuity bible: include age range, face, hair, body type, clothing, and signature props inside visualPrompt and continuityRules.
Every character must include visualPrompt, voiceStyle, and continuityRules.
Every character must include referenceImagePrompt and referenceImageNegativePrompt in English.
Character visualPrompt and voiceStyle must be in English.
Character reference image prompts must be simple, stable, and suitable for a single reference portrait or full-body image on a neutral cinematic background.
Do not change clothing unless the story explicitly requires it.
Do not randomly add or remove scars, glasses, hair color, beard, or accessories between scenes.
Dialogue must NOT be placed inside visual Wan prompts.
Spoken dialogue belongs only in dialogueLines.
Wan prompts must be in English.
Story, scene titles, dialogue, and summaries may remain in the user's language.
Every scene must include at least one shot.
Every shot must include wanPrompt and negativePrompt.
Every shot must include startImagePrompt and startImageNegativePrompt in English.
Every shot must include cameraMotion, action, shotType, audioCue, and continuityNotes.
Every wanPrompt must include character visual lock if a character appears in that shot.
Every wanPrompt must include scene environment, action, camera motion, mood, cinematic style, and lighting.
Every wanPrompt must describe one simple visible action only.
Every wanPrompt must repeat stable character visual locks and recurring location continuity when relevant.
Every startImagePrompt must describe a complete keyframe composition: environment, character placement, camera angle, lighting, mood, visual style, and historically or culturally consistent details.
Every startImagePrompt must include involved character visual locks and location continuity when relevant.
No spoken dialogue belongs in wanPrompt or startImagePrompt.
Avoid readable text, logos, signs, subtitles, and UI words in image prompts.
Every negativePrompt must include: low quality, watermark, text, logo, distorted face, inconsistent character, bad hands, extra fingers, extra limbs, flicker, blurry, deformed body.
Every negativePrompt must also include scene-specific and shot-specific failure terms. Do not return the exact same negativePrompt for every shot unless the shots are genuinely identical.
Use stable recurring locations; do not make every shot a new world.
Do not introduce new major characters unless the story requires them.
The plan must be suitable for long-form video generation.
If the requested target duration is long, break it into many short shots and many scenes.
Do not return a tiny 1-scene or 3-shot plan for a multi-minute target.
Wan2.2 is a video model adapter, not an Ollama model.
""";

        var user = $$"""
Project title: {{projectTitle}}
Target duration seconds: {{targetDurationSeconds}}

Duration planning requirements:
{{durationGuidance}}

Story:
{{storyText}}

Return exactly one JSON object with this schema:
{
  "title": "string",
  "logline": "string",
  "genre": "string",
  "targetDurationSeconds": 60,
  "visualStyle": {
    "stylePrompt": "string",
    "negativePrompt": "string",
    "cameraStyle": "string",
    "lightingStyle": "string",
    "colorPalette": "string"
  },
  "characters": [
    {
      "name": "string",
      "role": "string",
      "personality": "string",
      "visualPrompt": "English stable visual character bible",
      "referenceImagePrompt": "English reference image prompt: neutral cinematic background, clear face, stable clothing and identity",
      "referenceImageNegativePrompt": "low quality, watermark, text, logo, readable letters, distorted face, inconsistent face, bad hands, extra fingers, extra limbs, blurry, deformed body",
      "voiceStyle": "English voice style",
      "continuityRules": ["string"]
    }
  ],
  "scenes": [
    {
      "index": 1,
      "title": "string",
      "summary": "string",
      "location": "string",
      "timeOfDay": "string",
      "mood": "string",
      "estimatedDurationSeconds": 15,
      "requiredCharacters": ["character name"],
      "shots": [
        {
          "index": 1,
          "durationSeconds": 5,
          "shotType": "string",
          "cameraMotion": "string",
          "action": "string",
          "wanPrompt": "English Wan2.2-friendly visual prompt with character visual locks and no dialogue",
          "startImagePrompt": "English keyframe image prompt with environment, character placement, camera angle, lighting, mood, visual style, no dialogue, no readable text",
          "startImageNegativePrompt": "low quality, watermark, text, logo, readable letters, subtitles, distorted face, inconsistent face, inconsistent character, bad hands, extra fingers, extra limbs, blurry, deformed body, broken anatomy",
          "negativePrompt": "low quality, watermark, text, logo, distorted face, inconsistent character, bad hands, extra fingers, extra limbs, flicker, blurry, deformed body",
          "audioCue": "string",
          "continuityNotes": "string"
        }
      ],
      "dialogueLines": [
        {
          "speaker": "string",
          "text": "string",
          "emotion": "string",
          "estimatedStartSecond": 0,
          "estimatedEndSecond": 4
        }
      ]
    }
  ],
  "audioCues": [
    {
      "type": "ambience",
      "description": "string",
      "startSecond": 0,
      "endSecond": 5
    }
  ]
}
""";

        return (system, user);
    }

    private static string BuildCorrectionPrompt(string invalidResponse, string error)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Fix the previous response and return only valid JSON matching the requested production-plan schema.");
        builder.AppendLine("No markdown, no comments, no explanations, no trailing text.");
        builder.AppendLine($"Parser error: {error}");
        builder.AppendLine("Previous response:");
        builder.AppendLine(invalidResponse);
        return builder.ToString();
    }

    private static string BuildDurationGuidance(int targetDurationSeconds)
    {
        if (targetDurationSeconds >= 420)
        {
            var minimum = (int)Math.Ceiling(targetDurationSeconds * 0.85);
            return $"""
For this long-form target, create at least 14 scenes and at least 50 shots.
Aim for 18-24 scenes and 60-84 shots.
Every shot duration must be 5-8 seconds.
The sum of all shot durations must be at least {minimum} seconds.
Use many simple one-action shots rather than a few long shots.
""";
        }

        if (targetDurationSeconds >= 300)
        {
            var minimum = (int)Math.Ceiling(targetDurationSeconds * 0.90);
            return $"""
For this long-form target, create at least 12 scenes and at least 40 shots.
Aim for 14-18 scenes and 45-60 shots.
Every shot duration must be 5-8 seconds.
The sum of all shot durations must be at least {minimum} seconds.
Use many simple one-action shots rather than a few long shots.
""";
        }

        if (targetDurationSeconds >= 180)
        {
            var minimum = (int)Math.Ceiling(targetDurationSeconds * 0.90);
            return $"""
For this long-form target, create at least 8 scenes and at least 24 shots.
Aim for 10-14 scenes and 30-36 shots.
Every shot duration must be 5-8 seconds.
The sum of all shot durations must be at least {minimum} seconds.
Use many simple one-action shots rather than a few long shots.
""";
        }

        return """
For short targets, keep the plan compact but still create enough scenes and shots to tell the story.
Avoid a 1-scene/1-shot plan unless the story explicitly asks for a single shot.
Most shots should be 3-6 seconds.
""";
    }
}
