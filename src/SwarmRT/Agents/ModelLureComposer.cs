using System.Text.Json.Serialization;
using SwarmRT.Contracts;
using SwarmRT.Model;
using SwarmRT.Org;
using SwarmRT.Safety;

namespace SwarmRT.Agents;

/// <summary>
/// Composes the lure with one stateless model call. The prompt asks for a
/// deliberately thin simulation stub: enough to convey the pretext to a scripted
/// persona, not enough to send anywhere. The gate enforces those limits afterwards
/// regardless of what comes back.
/// </summary>
public sealed class ModelLureComposer(IModelClient model) : ILureComposer
{
    private sealed record ComposeOutput
    {
        [JsonPropertyName("simulated_message")]
        public string? SimulatedMessage { get; init; }

        [JsonPropertyName("attempt_summary")]
        public string? AttemptSummary { get; init; }
    }

    private const int MessageCharBudget = 320;

    private static readonly string SystemPrompt = $$"""
        You are one attempt in a defensive security-awareness simulation. A fabricated
        company is being tested so its staff can be trained; every target is a scripted
        synthetic persona on a reserved, non-routable domain, and nothing you write is
        delivered to a real person or system.

        Produce a SIMULATION STUB, not usable phishing copy. The stub exists only so a
        scripted persona has something to react to. Hard requirements:
        - Begin with the literal label {{HeuristicSafetyScreen.RequiredWatermark}}.
        - At most 2 short sentences and {{MessageCharBudget}} characters in total.
        - Describe the ask in general terms ("asks the recipient to confirm account access
          before a deadline"). Do not write polished, ready-to-send prose.
        - No links, domains, URLs, email addresses, phone numbers, addresses, IP addresses,
          attachment filenames, or reference numbers of any kind.
        - No real company, product, or brand names. Refer to "the internal service desk",
          "the collaboration platform", "the supplier" and so on.
        - Never instruct anyone to reply with, type, or send a password, PIN, one-time
          code, or key. Reference the action abstractly instead.
        - No malware, payload, exploitation, or security-control-evasion content.

        Also return a one-sentence attempt_summary in the past tense describing the
        approach at the level of pretext and tactic, for an audit log. The summary must not
        reproduce the stub's wording.

        Reply with JSON only:
        {"simulated_message": "<stub>", "attempt_summary": "<one sentence>"}
        """;

    public string Description => $"model-composed stubs ({model.Description})";

    public async Task<ComposedLure> ComposeAsync(
        AgentAssignment assignment,
        Employee target,
        PretextType pretext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(pretext);

        string tacticDetail = string.Join("; ", PretextCatalog
            .ParseTactic(assignment.Tactic)
            .Select(id => PretextCatalog.FindTactic(id) is { } t ? $"{t.Id} — {t.Description}" : id));

        ModelRequest request = new()
        {
            Kind = ModelCallKind.ComposeLure,
            SystemPrompt = SystemPrompt,
            UserPrompt = $"""
                Assignment:
                - pretext_type: {pretext.Id}
                - pretext meaning: {pretext.Description}
                - simulated channel: {pretext.Channel}
                - tactic: {assignment.Tactic}
                - tactic meaning: {tacticDetail}

                Synthetic target (fabricated persona, not a real person):
                - role: {target.Role}
                - department: {target.Department}
                - synthetic exposure attributes: {(target.Exposure.Count == 0 ? "none" : string.Join(", ", target.Exposure))}

                Compose the single stub for this one attempt.
                """,
            Temperature = 0.8,
            Seed = assignment.Seed,
            MaxOutputTokens = 400,
        };

        string raw = await model.CallModelAsync(request, cancellationToken).ConfigureAwait(false);
        ComposeOutput output = JsonPayload.Parse<ComposeOutput>(raw, ModelCallKind.ComposeLure);

        if (string.IsNullOrWhiteSpace(output.SimulatedMessage))
        {
            throw new ModelCallException($"{assignment.AttemptId}: composer returned no simulated_message.");
        }

        string summary = string.IsNullOrWhiteSpace(output.AttemptSummary)
            ? $"Attempted {pretext.Label.ToLowerInvariant()} over {pretext.Channel} using {assignment.Tactic}."
            : output.AttemptSummary;

        return new ComposedLure(output.SimulatedMessage.Trim(), summary, pretext.Channel);
    }
}
