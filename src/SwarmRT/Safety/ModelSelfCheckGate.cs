using System.Text.Json.Serialization;
using SwarmRT.Agents;
using SwarmRT.Model;
using SwarmRT.Org;

namespace SwarmRT.Safety;

/// <summary>
/// The semantic layer of the safety gate (design §7: "LLM self-check call … keeps it
/// single-backend"). It judges the harm categories a regex cannot see, and is
/// deliberately biased toward blocking: an unparseable or failed check is treated as
/// a flag, never as a pass.
/// </summary>
public sealed class ModelSelfCheckGate(IModelClient model)
{
    private sealed record SelfCheckVerdict
    {
        [JsonPropertyName("flagged")]
        public bool Flagged { get; init; }

        [JsonPropertyName("category")]
        public string? Category { get; init; }

        [JsonPropertyName("rationale")]
        public string? Rationale { get; init; }
    }

    private const string SystemPrompt = """
        You are the content-safety gate for a defensive security-awareness simulator.
        You review short simulated social-engineering stubs written against a fabricated
        company, before they are shown to a scripted synthetic persona. Nothing you review
        is ever sent to a real person.

        Flag the text when it contains any of:
        - hate, harassment, threats, sexual content, or self-harm content
        - instructions that would work as real tradecraft: malware, payload delivery,
          exploitation, or advice on evading security controls
        - any real-world identifier: a routable domain or URL, a real brand being
          impersonated, a real person, a real company, a phone number, or an address
        - content specific and polished enough to send as-is to a real target
        - a missing "[SIMULATED]" label

        Do not flag text merely for being persuasive, urgent, or deceptive: describing a
        pretext at a high level is the intended purpose of the exercise.

        Reply with JSON only:
        {"flagged": <bool>, "category": "<snake_case category or 'ok'>", "rationale": "<one short sentence>"}
        """;

    public string Description => "model self-check";

    public async Task<SafetyVerdict> ScreenAsync(
        string messageText, Employee target, CancellationToken cancellationToken = default)
    {
        ModelRequest request = new()
        {
            Kind = ModelCallKind.SafetyScreen,
            SystemPrompt = SystemPrompt,
            UserPrompt = $"""
                Simulated channel target: {target.Role}, {target.Department} (synthetic persona).

                Text under review:
                ---
                {messageText}
                ---
                """,
            Temperature = 0.0,
            MaxOutputTokens = 200,
        };

        string raw;
        try
        {
            raw = await model.CallModelAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ModelCallException ex)
        {
            // Fail closed: an unavailable check is not a passing check.
            return SafetyVerdict.Block(
                "self_check_unavailable",
                $"Safety self-check could not complete, so the lure was not delivered: {ex.Message}",
                Description);
        }

        SelfCheckVerdict verdict;
        try
        {
            verdict = JsonPayload.Parse<SelfCheckVerdict>(raw, ModelCallKind.SafetyScreen);
        }
        catch (ModelCallException ex)
        {
            return SafetyVerdict.Block(
                "self_check_unparseable",
                $"Safety self-check returned an unreadable verdict, so the lure was not delivered: {ex.Message}",
                Description);
        }

        if (!verdict.Flagged)
        {
            return SafetyVerdict.Cleared(Description);
        }

        string category = string.IsNullOrWhiteSpace(verdict.Category) ? "self_check_flag" : verdict.Category;
        string rationale = string.IsNullOrWhiteSpace(verdict.Rationale)
            ? "Safety self-check flagged the lure without giving a reason."
            : verdict.Rationale.Trim();

        return SafetyVerdict.Block(category, rationale, Description);
    }
}

/// <summary>
/// The gate the agent actually calls. Runs the deterministic screen first — it is
/// free, offline, and unpersuadable — and only spends a model call on text that has
/// already passed it.
/// </summary>
public sealed class LayeredContentSafetyGate(
    HeuristicSafetyScreen heuristics,
    ModelSelfCheckGate? selfCheck = null) : IContentSafetyGate
{
    public string Description => selfCheck is null
        ? heuristics.Description
        : $"{heuristics.Description} + {selfCheck.Description}";

    /// <summary>Number of lures screened, for the controls section of the report.</summary>
    public int Screened { get; private set; }

    public int Blocked { get; private set; }

    public async Task<SafetyVerdict> ScreenAsync(
        ComposedLure lure, Employee target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lure);
        ArgumentNullException.ThrowIfNull(target);

        Screened++;
        string text = lure.Reveal();

        SafetyVerdict verdict = heuristics.Screen(text);
        if (!verdict.Flagged && selfCheck is not null)
        {
            verdict = await selfCheck.ScreenAsync(text, target, cancellationToken).ConfigureAwait(false);
        }

        if (verdict.Flagged)
        {
            Blocked++;
        }

        return verdict;
    }
}
