using SwarmRT.Contracts;
using SwarmRT.Org;

namespace SwarmRT.Agents;

/// <summary>
/// Marker-based judge used when no model backend is configured. Like the model judge it
/// sees only the reply text, so the shape of design §6 is preserved: classification
/// comes from the reply's wording.
/// <para>
/// Resistance markers are tested before compliance markers, because an escalation
/// ("not actioning this, I've raised it with security") can otherwise look superficially
/// like an action being taken.
/// </para>
/// </summary>
public sealed class HeuristicReplyJudge : IReplyJudge
{
    private sealed record Marker(bool Favorable, string Reason, string[] Phrases);

    private static readonly Marker[] Markers =
    [
        new(false,
            "Target escalated the message to the security team rather than acting on it.",
            [
                "reported to", "reported it", "raised it with security", "forwarded this to the security",
                "suspected phishing", "looks like a phishing", "blocked the sender", "won't be replying",
            ]),

        new(false,
            "Target withheld action and asked for confirmation through a channel they already trusted.",
            [
                "which ticket", "confirm this through", "who authorised", "who authorized",
                "check with my manager", "not comfortable", "normal process", "usual channel",
                "doesn't line up", "don't recognise", "don't recognize", "before i do anything",
            ]),

        new(false,
            "Target never engaged with the message, so no action was taken.",
            ["no response", "did not engage", "left unopened"]),

        new(true,
            "Target began the requested action and gave up part of what was asked before hesitating.",
            [
                "started the change", "begun the first step", "part that seemed harmless",
                "passed on what i can", "can't complete all of it", "cannot complete all of it",
            ]),

        new(true,
            "Target carried out the request in a single reply without verifying the sender through a known channel.",
            [
                "i've gone ahead", "ive gone ahead", "gone ahead and actioned", "taken care of it",
                "that's handled", "thats handled", "all sorted", "done —", "done -", "it's done",
                "assumed it was fine",
            ]),
    ];

    public string Description => "marker-based judgment (no model backend)";

    public Task<ReplyJudgment> JudgeAsync(
        string replyText,
        AgentAssignment assignment,
        PretextType pretext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replyText);

        foreach (Marker marker in Markers)
        {
            if (marker.Phrases.Any(p => replyText.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(new ReplyJudgment(marker.Favorable, marker.Reason));
            }
        }

        // An unrecognised reply is treated as unfavorable: an attempt is only counted as a
        // success on positive evidence that the target acted.
        return Task.FromResult(new ReplyJudgment(
            false,
            "Reply showed no evidence that the target took the requested action."));
    }
}
