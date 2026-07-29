using SwarmRT.Contracts;
using SwarmRT.Org;

namespace SwarmRT.Agents;

/// <summary>
/// The agent's verdict on the single reply it received.
/// </summary>
/// <param name="Favorable">True when the target took, or committed to, the intended action.</param>
/// <param name="Reason">
/// One sentence explaining why the attempt did or did not work. Design §6 makes this the
/// field that gives the final report its value, so it must describe the target's
/// behaviour rather than restate the lure.
/// </param>
public sealed record ReplyJudgment(bool Favorable, string Reason);

/// <summary>
/// Design §6 — classifies the reply as favorable or not, from the reply's wording
/// alone. Implementations receive only the reply text: never the responder's internal
/// behaviour label, and never a second round-trip to the target.
/// </summary>
public interface IReplyJudge
{
    string Description { get; }

    Task<ReplyJudgment> JudgeAsync(
        string replyText,
        AgentAssignment assignment,
        PretextType pretext,
        CancellationToken cancellationToken = default);
}
