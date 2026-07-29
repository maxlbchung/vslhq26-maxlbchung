using SwarmRT.Contracts;
using SwarmRT.Org;

namespace SwarmRT.Agents;

/// <summary>
/// Deterministic composer used when no model backend is configured. Stubs are drawn
/// from a fixed table keyed by pretext, so a run without network access still
/// exercises the whole pipeline and produces identical output for a given seed.
/// <para>
/// Output from this composer is never presented as model output: the run manifest and
/// reports record the engine that actually produced each engagement.
/// </para>
/// </summary>
public sealed class TemplateLureComposer : ILureComposer
{
    private static readonly Dictionary<string, string[]> Stubs = new()
    {
        ["it_helpdesk_impersonation"] =
        [
            "[SIMULATED] Poses as the internal service desk reporting that account access will lapse unless the recipient re-confirms their sign-in today.",
            "[SIMULATED] Claims the recipient's newly issued device was not enrolled correctly and asks them to complete a short access re-confirmation before the end of shift.",
        ],
        ["mfa_fatigue_prompt"] =
        [
            "[SIMULATED] Describes a burst of repeated approval prompts as a sync glitch and asks the recipient to accept the next one so the alerts stop.",
            "[SIMULATED] Frames a run of authentication prompts as a known outage and asks the recipient to approve one to clear the queue.",
        ],
        ["executive_authority_request"] =
        [
            "[SIMULATED] Presents as a senior leader asking for a discreet, time-critical action handled directly and kept off the usual approval thread.",
            "[SIMULATED] Poses as an executive in back-to-back meetings who needs a quiet favour actioned before a board session, without looping in the wider team.",
        ],
        ["vendor_invoice_update"] =
        [
            "[SIMULATED] Presents as an existing supplier reporting an audit-driven change to remittance details and asks that the next payment be redirected.",
            "[SIMULATED] Claims a long-standing supplier's finance team has relocated and requests updated payment routing for an outstanding balance.",
        ],
        ["payroll_direct_deposit_change"] =
        [
            "[SIMULATED] Presents as an employee whose bank account has closed, asking payroll to redirect this month's deposit before the cut-off.",
            "[SIMULATED] Poses as a payroll service reporting a failed deposit and requesting revised account routing ahead of the next run.",
        ],
        ["hr_benefits_notice"] =
        [
            "[SIMULATED] Announces an unspecified change to benefits enrolment that supposedly requires a sign-in to acknowledge before a stated deadline.",
            "[SIMULATED] Presents as a people-team notice about a policy acknowledgement that most of the department is described as having already completed.",
        ],
        ["shared_document_notification"] =
        [
            "[SIMULATED] Mimics a share notification for a vaguely titled document and invites the recipient to sign in to view it.",
            "[SIMULATED] Presents as a colleague sharing a file the recipient is described as already being named in.",
        ],
        ["delivery_notification"] =
        [
            "[SIMULATED] Reports a parcel held pending a small confirmation step, framed as expiring within the day.",
            "[SIMULATED] Claims a courier attempted delivery and needs the recipient to confirm details before the item is returned to sender.",
        ],
        ["recruiter_outreach"] =
        [
            "[SIMULATED] Presents as a recruiter with an unnamed role whose details are described as attached for the recipient to open.",
            "[SIMULATED] Poses as a recruiter who saw the recipient speak publicly and offers compensation details on request.",
        ],
        ["survey_incentive"] =
        [
            "[SIMULATED] Offers a small reward for completing a short internal-sounding questionnaire that gathers account details.",
            "[SIMULATED] Frames a brief survey as closing shortly, with a gift promised to the first respondents.",
        ],
        ["tailgating_pretext"] =
        [
            "[SIMULATED] Presents in person as a contractor with full hands, asking the recipient to hold the secure door open.",
            "[SIMULATED] Poses as a new starter whose badge has not yet arrived, asking to follow the recipient through the barrier.",
        ],
    };

    public string Description => "deterministic template stubs (no model backend)";

    public Task<ComposedLure> ComposeAsync(
        AgentAssignment assignment,
        Employee target,
        PretextType pretext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(pretext);

        string[] variants = Stubs.TryGetValue(pretext.Id, out string[]? found)
            ? found
            : [$"[SIMULATED] Generic {pretext.SentenceLabel} approach over {pretext.Channel}, described at pretext level only."];

        string stub = variants[new Random(assignment.Seed).Next(variants.Length)];

        string summary =
            $"Attempted {pretext.SentenceLabel} against a {target.Role.ToLowerInvariant()} " +
            $"over {pretext.Channel}, leaning on {assignment.Tactic}.";

        return Task.FromResult(new ComposedLure(stub, summary, pretext.Channel));
    }
}
