using SwarmRT.Contracts;
using SwarmRT.Org;
using SwarmRT.Responders;
using SwarmRT.Safety;

namespace SwarmRT.Agents;

/// <summary>
/// Design §2's "one engineering-agent definition": the fixed, stateless behaviour every
/// clone shares. It holds collaborators only — no per-attempt data — so cloning is free
/// and no state can leak between attempts.
/// </summary>
public sealed record AgentDefinition
{
    public required ILureComposer Composer { get; init; }

    public required IContentSafetyGate Gate { get; init; }

    public required IEmployeeResponder Responder { get; init; }

    public required IReplyJudge Judge { get; init; }

    /// <summary>Screens every model-authored string before it can become a log field.</summary>
    public required LogTextSanitizer Sanitizer { get; init; }

    public required TimeProvider Time { get; init; }

    /// <summary>
    /// Instantiates a fresh agent for one assignment. The returned instance carries no
    /// history from any previous attempt because there is nowhere for history to live.
    /// </summary>
    public EngineeringAgent Spawn(AgentAssignment assignment) => new(this, assignment);
}

/// <summary>
/// Design §3.2 — one stateless attempt, then disposal.
/// <para>
/// The instance composes a single lure, submits it to the content-safety gate, delivers
/// it to the synthetic responder if cleared, judges the one reply it receives, and
/// returns exactly one result object. It does not retry, adapt, or follow up:
/// <see cref="RunAsync"/> refuses a second call, so "one attempt only" is enforced by
/// the type rather than left to the caller's discipline.
/// </para>
/// <para>
/// The memory wipe of design §2 is not a metaphor here. The only per-attempt state is the
/// composed lure, and <see cref="Dispose"/> zeroes its buffer. After disposal the instance
/// holds nothing, and the orchestrator drops the reference.
/// </para>
/// </summary>
public sealed class EngineeringAgent : IDisposable
{
    private readonly AgentDefinition _definition;
    private ComposedLure? _lure;
    private bool _hasRun;
    private bool _disposed;

    internal EngineeringAgent(AgentDefinition definition, AgentAssignment assignment)
    {
        _definition = definition;
        Assignment = assignment;
    }

    /// <summary>The single assignment this clone exists to carry out.</summary>
    public AgentAssignment Assignment { get; }

    /// <summary>True once the buffer holding the composed lure has been zeroed.</summary>
    public bool IsWiped => _disposed && (_lure is null || _lure.IsScrubbed);

    /// <summary>
    /// Carries out the attempt and returns the single result object. Throws
    /// <see cref="InvalidOperationException"/> if called more than once.
    /// </summary>
    public async Task<AttemptResult> RunAsync(
        Employee target,
        PretextType pretext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(pretext);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_hasRun)
        {
            throw new InvalidOperationException(
                $"Agent for {Assignment.AttemptId} has already made its one attempt. " +
                "Clone a fresh agent instead of reusing this one.");
        }

        _hasRun = true;

        // 1. Compose one lure.
        _lure = await _definition.Composer
            .ComposeAsync(Assignment, target, pretext, cancellationToken)
            .ConfigureAwait(false);

        // 2. Content-safety gate, before anything is "delivered".
        SafetyVerdict verdict = await _definition.Gate
            .ScreenAsync(_lure, target, cancellationToken)
            .ConfigureAwait(false);

        if (verdict.Flagged)
        {
            return BlockedResult(pretext, verdict);
        }

        // 3 & 4. Deliver, receive one reply, and judge it in the same turn (design §6).
        SimulatedReply reply = await _definition.Responder
            .RespondAsync(_lure, target, pretext, Assignment, cancellationToken)
            .ConfigureAwait(false);

        ReplyJudgment judgment = await _definition.Judge
            .JudgeAsync(reply.Text, Assignment, pretext, cancellationToken)
            .ConfigureAwait(false);

        // 5. Return exactly one result object.
        return JudgedResult(pretext, judgment);
    }

    private AttemptResult BlockedResult(PretextType pretext, SafetyVerdict verdict)
    {
        string fallback =
            $"Attempted {pretext.SentenceLabel} over {pretext.Channel} using " +
            $"{Assignment.Tactic}; the composed lure was blocked pre-delivery by the " +
            $"content-safety gate under '{verdict.Category}'.";

        string summary = _definition.Sanitizer.Sanitize(
            $"{fallback} Gate note ({verdict.Source}): {verdict.Rationale}",
            fallback);

        AttemptResult result = AttemptResult.ForBlocked(
            Assignment, _definition.Time.GetUtcNow(), summary);

        result.EnsureValid();
        return result;
    }

    private AttemptResult JudgedResult(PretextType pretext, ReplyJudgment judgment)
    {
        string summaryFallback =
            $"Attempted {pretext.SentenceLabel} over {pretext.Channel} using {Assignment.Tactic}.";

        string summary = _definition.Sanitizer.Sanitize(_lure!.AttemptSummary, summaryFallback);

        string reasonFallback = judgment.Favorable
            ? "Target acted on the request without verifying it through a known channel."
            : "Target did not take the requested action.";

        string reason = _definition.Sanitizer.Sanitize(judgment.Reason, reasonFallback);

        AttemptResult result = judgment.Favorable
            ? AttemptResult.ForSuccess(Assignment, _definition.Time.GetUtcNow(), reason, summary)
            : AttemptResult.ForFailure(Assignment, _definition.Time.GetUtcNow(), reason, summary);

        result.EnsureValid();
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lure?.Dispose();
        _disposed = true;
    }
}
