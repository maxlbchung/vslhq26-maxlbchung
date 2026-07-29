namespace SwarmRT.Safety;

/// <summary>
/// Last checkpoint before model-authored text becomes a log line.
/// <para>
/// The JSONL log is both the engagement's deliverable and its audit trail (design §3.6,
/// §8.5), so every string a model wrote — attempt summaries, success and failure reasons,
/// gate rationales — is screened on the way in and replaced with a safe equivalent if it
/// carries a real-world identifier, a brand name, tradecraft, or a capture mechanic.
/// Replacements are counted so a run that needed them says so instead of hiding it.
/// </para>
/// </summary>
public sealed class LogTextSanitizer(HeuristicSafetyScreen screen)
{
    private readonly List<string> _redactions = [];

    /// <summary>Categories that triggered a replacement, one entry per replacement.</summary>
    public IReadOnlyList<string> Redactions => _redactions;

    public int RedactionCount => _redactions.Count;

    /// <summary>
    /// Returns <paramref name="text"/> when it screens clean, otherwise
    /// <paramref name="fallback"/>. Whitespace-only input also yields the fallback so a
    /// required field is never written blank.
    /// </summary>
    public string Sanitize(string? text, string fallback, int? maxLength = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        return TrySanitize(text, out string clean, maxLength) ? clean : fallback;
    }

    /// <summary>
    /// Screens <paramref name="text"/> and reports whether it may be used. Callers with no
    /// sensible fallback — an optional narrative paragraph, say — use this and drop the
    /// text entirely when it does not pass.
    /// </summary>
    public bool TrySanitize(string? text, out string clean, int? maxLength = null)
    {
        clean = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string candidate = Collapse(text);
        SafetyVerdict verdict = screen.ScreenFragment(candidate, maxLength);
        if (verdict.Flagged)
        {
            _redactions.Add(verdict.Category);
            return false;
        }

        clean = candidate;
        return true;
    }

    /// <summary>Normalises whitespace so a log line stays a single line.</summary>
    private static string Collapse(string text)
    {
        string oneLine = text.ReplaceLineEndings(" ").Trim();
        while (oneLine.Contains("  ", StringComparison.Ordinal))
        {
            oneLine = oneLine.Replace("  ", " ", StringComparison.Ordinal);
        }

        return oneLine;
    }
}
