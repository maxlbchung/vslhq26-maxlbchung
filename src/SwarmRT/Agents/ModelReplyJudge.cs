using System.Text.Json.Serialization;
using SwarmRT.Contracts;
using SwarmRT.Model;
using SwarmRT.Org;

namespace SwarmRT.Agents;

/// <summary>
/// Judges the reply with one model call (design §6). The prompt withholds everything
/// except the reply text and the pretext label, so the verdict comes from what the
/// target actually wrote rather than from what the attempt was hoping for.
/// </summary>
public sealed class ModelReplyJudge(IModelClient model) : IReplyJudge
{
    private sealed record JudgeOutput
    {
        [JsonPropertyName("favorable")]
        public bool Favorable { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }

    private const string SystemPrompt = """
        You are scoring one attempt in a defensive security-awareness simulation. You are
        shown a single reply written by a scripted synthetic employee and must decide
        whether the attempt succeeded.

        Favorable (true) means the recipient took the requested action, committed to taking
        it, or handed over part of what was asked.

        Not favorable (false) means the recipient ignored the message, asked for
        verification, refused, or reported it.

        Then write one sentence of at most 30 words explaining WHY it went that way, in
        terms of the recipient's behaviour — what they did or failed to do. This sentence
        goes into a training report, so:
        - describe the behaviour, never restate or quote the message that was sent
        - name the missing or applied control where you can (for example "acted without
          verifying the sender through a known channel", or "escalated to the security team")
        - write it in the past tense and stay neutral; do not praise the attempt

        Reply with JSON only: {"favorable": <bool>, "reason": "<one sentence>"}
        """;

    public string Description => $"model judgment ({model.Description})";

    public async Task<ReplyJudgment> JudgeAsync(
        string replyText,
        AgentAssignment assignment,
        PretextType pretext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replyText);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(pretext);

        ModelRequest request = new()
        {
            Kind = ModelCallKind.JudgeReply,
            SystemPrompt = SystemPrompt,
            UserPrompt = $"""
                Approach used: {pretext.Label} over {pretext.Channel}, tactic "{assignment.Tactic}".
                What that approach asked for, in general terms: {pretext.Description}

                The recipient's single reply:
                ---
                {replyText}
                ---

                Judge this reply.
                """,
            Temperature = 0.1,
            Seed = assignment.Seed,
            MaxOutputTokens = 200,
        };

        string raw = await model.CallModelAsync(request, cancellationToken).ConfigureAwait(false);
        JudgeOutput output = JsonPayload.Parse<JudgeOutput>(raw, ModelCallKind.JudgeReply);

        string reason = string.IsNullOrWhiteSpace(output.Reason)
            ? output.Favorable
                ? "Target complied with the request; the judge returned no further detail."
                : "Target did not comply with the request; the judge returned no further detail."
            : output.Reason.Trim();

        return new ReplyJudgment(output.Favorable, reason);
    }
}
