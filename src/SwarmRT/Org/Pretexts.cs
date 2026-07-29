namespace SwarmRT.Org;

/// <summary>
/// An influence tactic. <see cref="PrimaryTrait"/> is the persona dial the
/// tactic leans on, which is how the responder scores a tactic combination.
/// </summary>
public sealed record Tactic(string Id, TraitKey PrimaryTrait, string Description)
{
    public override string ToString() => Id;
}

/// <summary>
/// A family of social-engineering approach, recorded at the level of type and
/// tactic only (design §8.3). Nothing here is lure copy; these are the
/// awareness-training categories the engagement measures.
/// </summary>
/// <param name="Id">Stable identifier written to the log.</param>
/// <param name="Channel">Simulated delivery channel: email, chat, sms, phone, push, in_person.</param>
/// <param name="Description">Neutral description of the approach, for reports.</param>
/// <param name="TraitWeights">
/// How much each susceptibility dial contributes, summing to 1.0.
/// Only the four susceptibility traits appear; the remaining traits act as resistance.
/// </param>
/// <param name="ExposureTriggers">Synthetic exposure attributes that make this pretext more plausible.</param>
/// <param name="TechnicalPretext">
/// True when a technically literate target is markedly more likely to spot the ruse.
/// </param>
/// <param name="TacticCombinations">Candidate tactic pairings the planner samples from.</param>
/// <param name="Countermeasure">The control that defeats this family, quoted in report recommendations.</param>
public sealed record PretextType(
    string Id,
    string Channel,
    string Description,
    IReadOnlyDictionary<TraitKey, double> TraitWeights,
    IReadOnlyList<string> ExposureTriggers,
    bool TechnicalPretext,
    IReadOnlyList<string[]> TacticCombinations,
    string Countermeasure)
{
    /// <summary>Initialisms that must stay upper-case when an id is turned into prose.</summary>
    private static readonly HashSet<string> Initialisms =
        new(StringComparer.OrdinalIgnoreCase) { "hr", "it", "mfa", "otp", "vpn", "sms", "ceo", "cfo" };

    /// <summary>Human-facing label, e.g. "it_helpdesk_impersonation" -> "IT helpdesk impersonation".</summary>
    public string Label => Humanize(sentenceCase: false);

    /// <summary>
    /// The label as it reads mid-sentence: "attempted IT helpdesk impersonation", "attempted
    /// executive authority request". Only the leading word changes, and only when it is not
    /// an initialism.
    /// </summary>
    public string SentenceLabel => Humanize(sentenceCase: true);

    private string Humanize(bool sentenceCase)
    {
        string[] words = Id.Split('_', StringSplitOptions.RemoveEmptyEntries);

        IEnumerable<string> rendered = words.Select((word, index) =>
        {
            if (Initialisms.Contains(word))
            {
                return word.ToUpperInvariant();
            }

            return index == 0 && !sentenceCase
                ? string.Concat(char.ToUpperInvariant(word[0]), word[1..])
                : word.ToLowerInvariant();
        });

        return string.Join(' ', rendered);
    }
}

/// <summary>The fixed catalog of pretexts and tactics available to the swarm.</summary>
public static class PretextCatalog
{
    public static readonly Tactic Authority =
        new("authority", TraitKey.AuthorityDeference, "Claims standing that makes refusal feel insubordinate.");

    public static readonly Tactic Urgency =
        new("urgency", TraitKey.UrgencySusceptibility, "Imposes a deadline that crowds out verification.");

    public static readonly Tactic Fear =
        new("fear", TraitKey.UrgencySusceptibility, "Threatens a personal or operational consequence.");

    public static readonly Tactic Scarcity =
        new("scarcity", TraitKey.UrgencySusceptibility, "Frames the opportunity as limited or closing.");

    public static readonly Tactic Curiosity =
        new("curiosity", TraitKey.Curiosity, "Withholds detail so the target opens the content to learn more.");

    public static readonly Tactic Reciprocity =
        new("reciprocity", TraitKey.Helpfulness, "Offers an unsolicited favour that invites repayment.");

    public static readonly Tactic Familiarity =
        new("familiarity", TraitKey.Helpfulness, "Borrows a known name, team, or shared context to lower guard.");

    public static readonly Tactic SocialProof =
        new("social_proof", TraitKey.AuthorityDeference, "Asserts that peers have already complied.");

    public static readonly IReadOnlyList<Tactic> AllTactics =
    [
        Authority, Urgency, Fear, Scarcity, Curiosity, Reciprocity, Familiarity, SocialProof,
    ];

    public static Tactic? FindTactic(string id) =>
        AllTactics.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<TraitKey, double> Weights(
        double authority, double urgency, double curiosity, double helpfulness) => new()
    {
        [TraitKey.AuthorityDeference] = authority,
        [TraitKey.UrgencySusceptibility] = urgency,
        [TraitKey.Curiosity] = curiosity,
        [TraitKey.Helpfulness] = helpfulness,
    };

    public static readonly IReadOnlyList<PretextType> All =
    [
        new(
            Id: "it_helpdesk_impersonation",
            Channel: "email",
            Description: "Poses as internal IT support requiring an account action to restore or retain access.",
            TraitWeights: Weights(authority: 0.35, urgency: 0.35, curiosity: 0.10, helpfulness: 0.20),
            ExposureTriggers: ["new_hire", "recent_device_change"],
            TechnicalPretext: true,
            TacticCombinations: [["urgency", "authority"], ["authority", "familiarity"], ["fear", "urgency"]],
            Countermeasure: "IT never asks for credentials or MFA codes; route all access requests through the published service desk number."),

        new(
            Id: "mfa_fatigue_prompt",
            Channel: "push",
            Description: "Repeats authentication prompts until the target approves one to stop the interruption.",
            TraitWeights: Weights(authority: 0.25, urgency: 0.50, curiosity: 0.10, helpfulness: 0.15),
            ExposureTriggers: ["admin_privileges", "frequent_traveler"],
            TechnicalPretext: true,
            TacticCombinations: [["urgency", "fear"], ["urgency", "authority"]],
            Countermeasure: "Enable number matching on push approvals and treat unexpected prompt storms as a reportable incident."),

        new(
            Id: "executive_authority_request",
            Channel: "email",
            Description: "Impersonates a senior leader directing an out-of-process action, discouraging escalation.",
            TraitWeights: Weights(authority: 0.55, urgency: 0.30, curiosity: 0.00, helpfulness: 0.15),
            ExposureTriggers: ["reports_to_executive", "finance_approval_authority"],
            TechnicalPretext: false,
            TacticCombinations: [["authority", "urgency"], ["authority", "social_proof"], ["authority", "fear"]],
            Countermeasure: "Require out-of-band confirmation for any leadership request that bypasses normal process, with an explicit no-blame policy for checking."),

        new(
            Id: "vendor_invoice_update",
            Channel: "email",
            Description: "Poses as an existing supplier asking that remittance details be amended.",
            TraitWeights: Weights(authority: 0.30, urgency: 0.25, curiosity: 0.10, helpfulness: 0.35),
            ExposureTriggers: ["handles_vendor_invoices", "finance_approval_authority"],
            TechnicalPretext: false,
            TacticCombinations: [["familiarity", "urgency"], ["authority", "familiarity"], ["urgency", "scarcity"]],
            Countermeasure: "Verify every banking-detail change by callback to the vendor contact already on file, never to details supplied in the request."),

        new(
            Id: "payroll_direct_deposit_change",
            Channel: "email",
            Description: "Poses as an employee or payroll service redirecting salary payment details.",
            TraitWeights: Weights(authority: 0.35, urgency: 0.30, curiosity: 0.10, helpfulness: 0.25),
            ExposureTriggers: ["handles_payroll"],
            TechnicalPretext: false,
            TacticCombinations: [["urgency", "familiarity"], ["authority", "urgency"]],
            Countermeasure: "Confirm deposit changes with the employee through a verified internal channel and notify the prior account on record."),

        new(
            Id: "hr_benefits_notice",
            Channel: "email",
            Description: "Poses as HR announcing a benefits or policy action that requires sign-in to review.",
            TraitWeights: Weights(authority: 0.30, urgency: 0.25, curiosity: 0.30, helpfulness: 0.15),
            ExposureTriggers: ["listed_on_public_contact_page", "new_hire"],
            TechnicalPretext: false,
            TacticCombinations: [["authority", "curiosity"], ["urgency", "scarcity"], ["authority", "social_proof"]],
            Countermeasure: "Publish HR notices only through the intranet portal and train staff to navigate there directly rather than via links."),

        new(
            Id: "shared_document_notification",
            Channel: "email",
            Description: "Mimics a collaboration-platform share notification to prompt a sign-in.",
            TraitWeights: Weights(authority: 0.10, urgency: 0.20, curiosity: 0.45, helpfulness: 0.25),
            ExposureTriggers: ["collaborates_externally"],
            TechnicalPretext: true,
            TacticCombinations: [["curiosity", "familiarity"], ["curiosity", "urgency"]],
            Countermeasure: "Reach shared files from within the collaboration app; surface external-sender banners on share notifications."),

        new(
            Id: "delivery_notification",
            Channel: "sms",
            Description: "Poses as a courier reporting a held parcel needing confirmation.",
            TraitWeights: Weights(authority: 0.10, urgency: 0.30, curiosity: 0.50, helpfulness: 0.10),
            ExposureTriggers: ["frequent_traveler"],
            TechnicalPretext: false,
            TacticCombinations: [["curiosity", "urgency"], ["scarcity", "curiosity"]],
            Countermeasure: "Track parcels only from the carrier's own app or site; treat unsolicited delivery texts as disposable."),

        new(
            Id: "recruiter_outreach",
            Channel: "chat",
            Description: "Poses as a recruiter whose role details arrive as an attachment or link.",
            TraitWeights: Weights(authority: 0.10, urgency: 0.15, curiosity: 0.45, helpfulness: 0.30),
            ExposureTriggers: ["speaks_at_conferences", "listed_on_public_contact_page"],
            TechnicalPretext: false,
            TacticCombinations: [["curiosity", "reciprocity"], ["familiarity", "curiosity"]],
            Countermeasure: "Keep career conversations off corporate devices and never open unsolicited recruiter attachments on the corporate network."),

        new(
            Id: "survey_incentive",
            Channel: "email",
            Description: "Offers a reward for completing a form that harvests details.",
            TraitWeights: Weights(authority: 0.05, urgency: 0.15, curiosity: 0.40, helpfulness: 0.40),
            ExposureTriggers: ["listed_on_public_contact_page"],
            TechnicalPretext: false,
            TacticCombinations: [["reciprocity", "scarcity"], ["curiosity", "reciprocity"]],
            Countermeasure: "Treat incentive offers as untrusted; internal surveys are announced on the intranet and never request credentials."),

        new(
            Id: "tailgating_pretext",
            Channel: "in_person",
            Description: "Seeks physical entry by presenting as staff or a contractor with hands full.",
            TraitWeights: Weights(authority: 0.25, urgency: 0.15, curiosity: 0.05, helpfulness: 0.55),
            ExposureTriggers: ["badge_access_main_office"],
            TechnicalPretext: false,
            TacticCombinations: [["reciprocity", "familiarity"], ["authority", "urgency"]],
            Countermeasure: "One badge, one person; make challenging an unbadged follower the expected, supported behaviour."),
    ];

    public static PretextType? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static PretextType Require(string id) =>
        Find(id) ?? throw new KeyNotFoundException($"Unknown pretext type '{id}'.");

    /// <summary>Renders a tactic combination the way it appears in the log, e.g. "urgency + authority".</summary>
    public static string FormatTactic(IEnumerable<string> tactics) => string.Join(" + ", tactics);

    /// <summary>Splits a logged tactic string back into its components.</summary>
    public static IReadOnlyList<string> ParseTactic(string tactic) =>
        tactic.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
