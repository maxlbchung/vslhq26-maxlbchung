using SwarmRT.Agents;
using SwarmRT.Contracts;
using SwarmRT.Org;

namespace SwarmRT.Responders;

/// <summary>
/// Design §3.4's recommended responder: a persona reacts according to weighted traits
/// rather than a model call, which makes a demo run reproducible and free.
/// <para>
/// The pretext supplies which susceptibility dials matter and how much; the tactic
/// pair leans on its own dials; matching synthetic exposure attributes make the story
/// more plausible; and verification habit, training recency, and technical literacy
/// push back. A small seeded jitter keeps identical persona/pretext pairs from being
/// perfectly predictable while leaving the run reproducible for a given seed.
/// </para>
/// </summary>
public sealed class RuleWeightedResponder : IEmployeeResponder
{
    // Blend of the pretext's own trait weighting against the tactic pair's dials.
    private const double PretextShare = 0.60;
    private const double TacticShare = 0.40;

    // Each matching synthetic exposure attribute makes the pretext more plausible.
    private const double ExposureBonusEach = 0.10;
    private const double ExposureBonusCap = 0.20;

    // Resistance side.
    private const double VerificationWeight = 0.40;
    private const double TrainingWeight = 0.22;
    private const double TechnicalWeightForTechnicalPretext = 0.22;
    private const double TechnicalWeightOtherwise = 0.06;

    private const double JitterRange = 0.09;

    // Compliance thresholds. A favorable reply means reaching PartialThreshold, so that
    // value sets the engagement's overall success rate; it is calibrated so a roster with a
    // deliberate spread of susceptible and resistant personas lands in the range published
    // phishing-simulation studies report rather than at a flattering extreme.
    private const double ComplyThreshold = 0.58;
    private const double PartialThreshold = 0.46;

    // Vigilance thresholds, used only once compliance has failed.
    private const double ReportThreshold = 0.62;
    private const double QuestionThreshold = 0.38;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReplyBehavior> _groundTruth =
        new(StringComparer.Ordinal);

    public string Description => "rule-weighted personas (deterministic for a given seed)";

    /// <summary>
    /// What this responder actually decided, keyed by attempt id.
    /// <para>
    /// The engineering agent never sees this — it judges from the reply text alone
    /// (design §6). The orchestrator reads it afterwards purely to report how often the
    /// agent's judgment agreed with the persona model, which is how the judge gets
    /// checked rather than trusted.
    /// </para>
    /// </summary>
    public bool TryGetBehavior(string attemptId, out ReplyBehavior behavior) =>
        _groundTruth.TryGetValue(attemptId, out behavior);

    public Task<SimulatedReply> RespondAsync(
        ComposedLure lure,
        Employee target,
        PretextType pretext,
        AgentAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lure);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(pretext);
        ArgumentNullException.ThrowIfNull(assignment);

        Random random = new(assignment.Seed);
        double compliance = ScoreCompliance(target, pretext, assignment.Tactic, random);
        ReplyBehavior behavior = compliance >= ComplyThreshold
            ? ReplyBehavior.Complied
            : compliance >= PartialThreshold
                ? ReplyBehavior.PartiallyDisclosed
                : ChooseResistantBehavior(target);

        string text = ReplyScripts.For(behavior, random);
        _groundTruth[assignment.AttemptId] = behavior;

        return Task.FromResult(new SimulatedReply
        {
            Text = text,
            Behavior = behavior,
            ComplianceScore = Math.Round(compliance, 3),
        });
    }

    /// <summary>
    /// Compliance score for a persona against a pretext/tactic pairing. Exposed so the
    /// planner and tests can reason about the model without going through a reply.
    /// </summary>
    public static double ScoreCompliance(
        Employee target, PretextType pretext, string tactic, Random random)
    {
        double pretextScore = pretext.TraitWeights
            .Sum(pair => pair.Value * target.Traits[pair.Key]);

        double tacticScore = TacticScore(target, tactic);

        int matchedTriggers = pretext.ExposureTriggers.Count(target.HasExposure);
        double exposureBonus = Math.Min(matchedTriggers * ExposureBonusEach, ExposureBonusCap);

        double technicalWeight = pretext.TechnicalPretext
            ? TechnicalWeightForTechnicalPretext
            : TechnicalWeightOtherwise;

        double resistance =
            (VerificationWeight * target.Traits.VerificationHabit) +
            (TrainingWeight * target.Traits.TrainingRecency) +
            (technicalWeight * target.Traits.TechnicalLiteracy);

        double jitter = ((random.NextDouble() * 2.0) - 1.0) * JitterRange;

        return (PretextShare * pretextScore)
               + (TacticShare * tacticScore)
               + exposureBonus
               - resistance
               + jitter;
    }

    private static double TacticScore(Employee target, string tactic)
    {
        double[] values = PretextCatalog.ParseTactic(tactic)
            .Select(PretextCatalog.FindTactic)
            .Where(t => t is not null)
            .Select(t => target.Traits[t!.PrimaryTrait])
            .ToArray();

        return values.Length == 0 ? 0.0 : values.Average();
    }

    /// <summary>
    /// Among the non-compliant behaviours, which one a persona lands on is a question of
    /// vigilance rather than susceptibility: a well-trained, verifying, technical persona
    /// escalates; a disengaged one simply never responds.
    /// </summary>
    private static ReplyBehavior ChooseResistantBehavior(Employee target)
    {
        double vigilance =
            (0.45 * target.Traits.VerificationHabit) +
            (0.30 * target.Traits.TrainingRecency) +
            (0.25 * target.Traits.TechnicalLiteracy);

        return vigilance >= ReportThreshold
            ? ReplyBehavior.Reported
            : vigilance >= QuestionThreshold
                ? ReplyBehavior.Questioned
                : ReplyBehavior.Ignored;
    }
}

/// <summary>
/// Reply wording per behaviour. Phrased generically so one set covers every pretext,
/// and labelled as simulated output per design §8.4.
/// </summary>
internal static class ReplyScripts
{
    private const string Label = "[SIMULATED REPLY]";

    private static readonly Dictionary<ReplyBehavior, string[]> Scripts =
        new()
        {
            [ReplyBehavior.Complied] =
            [
                "Done — I've gone ahead and actioned that for you. Let me know if anything else is needed.",
                "No problem, I've taken care of it just now. Sorry it took a moment to get to.",
                "That's handled. I assumed it was fine to proceed given the deadline you mentioned.",
                "All sorted on my side. Happy to help — just say the word if there's more.",
            ],
            [ReplyBehavior.PartiallyDisclosed] =
            [
                "I can't complete all of it from here, but I've confirmed the details you asked about and started the change.",
                "I've passed on what I can for now and begun the first step — tell me if you need the remainder.",
                "I'd rather not do the whole thing, though I've gone ahead with the part that seemed harmless enough.",
            ],
            [ReplyBehavior.Questioned] =
            [
                "Before I do anything — which ticket is this against? I don't recognise this request.",
                "Can you confirm this through the usual channel first? Something here doesn't line up for me.",
                "Who authorised this? I'd like to check with my manager before I action anything.",
                "I'm not comfortable proceeding on this alone. Please route it through the normal process.",
            ],
            [ReplyBehavior.Ignored] =
            [
                "(no response — message left unopened)",
                "(no response — recipient did not engage with the message)",
            ],
            [ReplyBehavior.Reported] =
            [
                "I've forwarded this to the security team as a suspected phishing attempt and won't be replying further.",
                "This looks like a phishing attempt. Reported to security; not acting on it.",
                "Not actioning this. I've raised it with security and blocked the sender.",
            ],
        };

    public static string For(ReplyBehavior behavior, Random random)
    {
        string[] options = Scripts[behavior];
        string chosen = options[random.Next(options.Length)];

        // An ignored message produces no prose to label.
        return behavior == ReplyBehavior.Ignored ? chosen : $"{Label} {chosen}";
    }
}
