using SwarmRT.Agents;
using SwarmRT.Contracts;
using SwarmRT.Org;

namespace SwarmRT.Responders;

/// <summary>How the synthetic employee actually behaved. Ordered most to least compliant.</summary>
public enum ReplyBehavior
{
    /// <summary>Took the requested action. Favorable.</summary>
    Complied,

    /// <summary>Started the action or gave up part of what was asked. Favorable.</summary>
    PartiallyDisclosed,

    /// <summary>Pushed back and asked for verification. Unfavorable.</summary>
    Questioned,

    /// <summary>Did not engage at all. Unfavorable.</summary>
    Ignored,

    /// <summary>Escalated it to security. Unfavorable, and the desired behaviour.</summary>
    Reported,
}

/// <summary>
/// The single reply a synthetic employee gives (design §3.4).
/// <para>
/// <see cref="Behavior"/> is the responder's private ground truth. The engineering
/// agent is only ever handed <see cref="Text"/> — judging from the reply's wording is
/// the whole point of design §6, so the label is kept out of the agent's reach and
/// used only to score the judge's accuracy afterwards. It is null when a model
/// persona produced the reply, since no label exists in that mode.
/// </para>
/// </summary>
public sealed record SimulatedReply
{
    public required string Text { get; init; }

    public ReplyBehavior? Behavior { get; init; }

    /// <summary>Compliance score behind the behaviour, for the report's method notes.</summary>
    public double? ComplianceScore { get; init; }

    public static bool IsFavorable(ReplyBehavior behavior) =>
        behavior is ReplyBehavior.Complied or ReplyBehavior.PartiallyDisclosed;
}

/// <summary>
/// Design §3.4 — given a delivered lure and a target, returns one reply representing
/// that employee's reaction. Implementations must not reveal their reasoning in
/// <see cref="SimulatedReply.Text"/> beyond what the persona would actually write.
/// </summary>
public interface IEmployeeResponder
{
    string Description { get; }

    Task<SimulatedReply> RespondAsync(
        ComposedLure lure,
        Employee target,
        PretextType pretext,
        AgentAssignment assignment,
        CancellationToken cancellationToken = default);
}
