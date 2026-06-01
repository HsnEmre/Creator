using System.Text;
using System.Text.Json;
using VideoStudio.Api.Contracts;

namespace VideoStudio.Api.Services;

public sealed class ProductionPlanJsonService(ILogger<ProductionPlanJsonService> logger)
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public string ExtractFirstJsonObject(string response)
    {
        var content = StripCodeFences(response);
        var start = content.IndexOf('{');
        if (start < 0)
        {
            throw new JsonException("No JSON object found.");
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < content.Length; i++)
        {
            var ch = content[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return content[start..(i + 1)];
                }
            }
        }

        throw new JsonException("JSON object was not complete.");
    }

    public bool TryParseProductionPlan(string response, out ProductionPlanDto? plan, out string? error)
    {
        plan = null;
        error = null;
        try
        {
            plan = JsonSerializer.Deserialize<ProductionPlanDto>(ExtractFirstJsonObject(response), _jsonOptions);
            if (plan is null)
            {
                error = "Production plan JSON was empty.";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Invalid Ollama production plan JSON: {Message}. Raw response: {Response}", ex.Message, response);
            error = ex.Message;
            return false;
        }
    }

    public string Serialize<T>(T value) => JsonSerializer.Serialize(value, _jsonOptions);

    public List<T> DeserializeList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, _jsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string StripCodeFences(string response)
    {
        var trimmed = response.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var lines = trimmed.Split('\n');
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            builder.AppendLine(line);
        }

        return builder.ToString().Trim();
    }
}
