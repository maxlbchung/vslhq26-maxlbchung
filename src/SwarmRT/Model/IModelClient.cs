namespace SwarmRT.Model;

/// <summary>
/// What a model call is for. Carried on the request so the throttle, the console
/// trace, and the usage tally can attribute calls without inspecting prompts.
/// </summary>
public enum ModelCallKind
{
    /// <summary>Agent composes its single lure for the assigned pretext.</summary>
    ComposeLure,

    /// <summary>Content-safety gate self-check on the composed lure.</summary>
    SafetyScreen,

    /// <summary>Synthetic employee persona produces one reply.</summary>
    PersonaReply,

    /// <summary>Agent judges whether the reply was favorable.</summary>
    JudgeReply,

    /// <summary>Report generator phrases a narrative finding.</summary>
    ReportNarrative,
}

/// <summary>One stateless completion request. No history is ever attached (design §2).</summary>
public sealed record ModelRequest
{
    public required ModelCallKind Kind { get; init; }

    public required string SystemPrompt { get; init; }

    public required string UserPrompt { get; init; }

    public double Temperature { get; init; } = 0.4;

    /// <summary>Passed through to backends that honour it, for run reproducibility.</summary>
    public int? Seed { get; init; }

    /// <summary>When true, request a JSON object response and expect parseable JSON back.</summary>
    public bool ExpectJson { get; init; } = true;

    public int MaxOutputTokens { get; init; } = 600;
}

/// <summary>Cumulative backend usage, reported in the engagement footer.</summary>
public sealed record ModelUsage(int Calls, int PromptTokens, int CompletionTokens)
{
    public static readonly ModelUsage Zero = new(0, 0, 0);

    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>
/// Design §7's single model-call seam: one method taking a prompt and returning
/// raw text. Swapping GitHub Models for any other OpenAI-compatible endpoint is a
/// constructor change, not a code change.
/// </summary>
public interface IModelClient
{
    /// <summary>Backend description recorded in reports, e.g. "GitHub Models / openai/gpt-4o-mini".</summary>
    string Description { get; }

    ModelUsage Usage { get; }

    Task<string> CallModelAsync(ModelRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A backend call that could not be completed after retries.</summary>
public sealed class ModelCallException : Exception
{
    public ModelCallException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    public int? StatusCode { get; init; }

    /// <summary>True for rate limiting and transient server faults.</summary>
    public bool IsRetryable => StatusCode is 408 or 429 or >= 500;
}
