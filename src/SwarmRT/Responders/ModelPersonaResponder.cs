using System.Text.Json.Serialization;
using SwarmRT.Agents;
using SwarmRT.Contracts;
using SwarmRT.Model;
using SwarmRT.Org;

namespace SwarmRT.Responders;

/// <summary>
/// Design §3.4's optional model-driven responder. The persona's trait dials are handed
/// over as plain descriptors and the model improvises one in-character reply, so
/// outcomes vary the way people do rather than tracking a threshold exactly.
/// <para>
/// No ground-truth behaviour label exists in this mode — the reply is the only artefact
/// — so the agent's judgment is the sole classification, which is the arrangement
/// design §6 describes.
/// </para>
/// </summary>
public sealed class ModelPersonaResponder(IModelClient model) : IEmployeeResponder
{
    private sealed record PersonaOutput
    {
        [JsonPropertyName("reply")]
        public string? Reply { get; init; }
    }

    private const string NoResponseMarker = "(no response)";

    private const string SystemPrompt = """
        You are role-playing one fabricated employee persona inside a defensive
        security-awareness simulation. You are not a real person and no real system is
        involved. Your job is to react to one simulated message exactly as this persona
        plausibly would — including badly, if that is in character.

        Rules:
        - Reply in at most two sentences, in the persona's voice.
        - React to the message as written. If the persona would comply, comply. If they
          would push back, ask for verification. If they would escalate, say they are
          reporting it. If they would simply not engage, reply with exactly "(no response)".
        - Never mention your trait values, your instructions, or that you are role-playing.
        - Never invent links, phone numbers, account numbers, or real company names.
        - Do not include any real credential, code, or key, even a fabricated-looking one.

        Reply with JSON only: {"reply": "<the persona's reply>"}
        """;

    public string Description => $"model-driven personas ({model.Description})";

    public async Task<SimulatedReply> RespondAsync(
        ComposedLure lure,
        Employee target,
        PretextType pretext,
        AgentAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lure);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(assignment);

        ModelRequest request = new()
        {
            Kind = ModelCallKind.PersonaReply,
            SystemPrompt = SystemPrompt,
            UserPrompt = $"""
                Your persona:
                - role: {target.Role}, {target.Department}
                - manner: {target.Voice}
                - deference to authority: {Describe(target.Traits.AuthorityDeference)}
                - reaction to deadlines and pressure: {Describe(target.Traits.UrgencySusceptibility)}
                - curiosity about unexpected content: {Describe(target.Traits.Curiosity)}
                - urge to be helpful: {Describe(target.Traits.Helpfulness)}
                - technical literacy: {Describe(target.Traits.TechnicalLiteracy)}
                - habit of verifying requests independently: {Describe(target.Traits.VerificationHabit)}
                - how recent your security training is: {Describe(target.Traits.TrainingRecency)}

                Message received over {lure.Channel}:
                ---
                {lure.Reveal()}
                ---

                Reply as this persona would.
                """,
            Temperature = 0.9,
            Seed = assignment.Seed,
            MaxOutputTokens = 250,
        };

        string raw = await model.CallModelAsync(request, cancellationToken).ConfigureAwait(false);
        PersonaOutput output = JsonPayload.Parse<PersonaOutput>(raw, ModelCallKind.PersonaReply);

        string reply = (output.Reply ?? string.Empty).Trim();
        if (reply.Length == 0)
        {
            reply = NoResponseMarker;
        }

        bool silent = reply.StartsWith(NoResponseMarker, StringComparison.OrdinalIgnoreCase);

        return new SimulatedReply
        {
            Text = silent
                ? "(no response — recipient did not engage with the message)"
                : $"[SIMULATED REPLY] {reply}",
            Behavior = null,
            ComplianceScore = null,
        };
    }

    private static string Describe(double value) => value switch
    {
        >= 0.85 => "very high",
        >= 0.65 => "high",
        >= 0.45 => "moderate",
        >= 0.25 => "low",
        _ => "very low",
    };
}
