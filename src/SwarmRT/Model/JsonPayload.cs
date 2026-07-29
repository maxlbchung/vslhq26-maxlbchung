using System.Text.Json;
using SwarmRT.Contracts;

namespace SwarmRT.Model;

/// <summary>
/// Recovers a JSON object from a model response. Even with a JSON response format
/// requested, models occasionally wrap output in a fenced block or add a sentence
/// of preamble, so the object is located rather than assumed (design §7:
/// "validate on parse").
/// </summary>
public static class JsonPayload
{
    /// <summary>Returns the substring holding the outermost JSON object, or null if there isn't one.</summary>
    public static string? Locate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string text = StripFences(raw).Trim();

        int start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text[start..(i + 1)];
                    }

                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the located object into <typeparamref name="T"/>. Throws
    /// <see cref="ModelCallException"/> with a truncated excerpt so a malformed
    /// response is diagnosable without dumping the whole body into the log.
    /// </summary>
    public static T Parse<T>(string raw, ModelCallKind kind)
    {
        string? located = Locate(raw);
        if (located is null)
        {
            throw new ModelCallException(
                $"{kind} response contained no JSON object. Received: {Excerpt(raw)}");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(located, SwarmJson.Reading)
                   ?? throw new ModelCallException($"{kind} response deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new ModelCallException(
                $"{kind} response was not valid {typeof(T).Name} JSON: {ex.Message}. Received: {Excerpt(located)}",
                ex);
        }
    }

    private static string StripFences(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return trimmed;
        }

        string body = trimmed[(firstNewline + 1)..];
        int closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? body[..closing] : body;
    }

    public static string Excerpt(string text, int limit = 240)
    {
        string collapsed = text.ReplaceLineEndings(" ").Trim();
        return collapsed.Length <= limit ? collapsed : collapsed[..limit] + "…";
    }
}
