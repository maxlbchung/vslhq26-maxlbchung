using SwarmRT.Agents;
using SwarmRT.Contracts;
using SwarmRT.Org;
using SwarmRT.Orchestration;
using SwarmRT.Responders;
using SwarmRT.Safety;

namespace SwarmRT.Tests;

/// <summary>Design §2, §3.2, §6 — the stateless clone-and-wipe agent.</summary>
public class EngineeringAgentTests
{
    private const string Watermark = HeuristicSafetyScreen.RequiredWatermark;

    private static readonly string CleanStub =
        $"{Watermark} Poses as the internal service desk asking for an access re-confirmation.";

    [Fact]
    public async Task MakesExactlyOneAttemptAndRefusesASecond()
    {
        AgentDefinition definition = TestSupport.Definition(
            composer: new FixedComposer(CleanStub, "Attempted a pretext."),
            responder: new FixedResponder("[SIMULATED REPLY] Done, I've actioned that."),
            judge: new RecordingJudge(favorable: true));

        using EngineeringAgent agent = definition.Spawn(TestSupport.Assignment());

        AttemptResult first = await agent.RunAsync(TestSupport.Compliant(), TestSupport.Pretext());

        Assert.Equal(AttemptOutcome.Success, first.Outcome);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.RunAsync(TestSupport.Compliant(), TestSupport.Pretext()));

        Assert.Contains("already made its one attempt", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WipesTheLureBufferOnDisposal()
    {
        AgentDefinition definition = TestSupport.Definition(
            composer: new FixedComposer(CleanStub, "Attempted a pretext."),
            responder: new FixedResponder("[SIMULATED REPLY] Done."),
            judge: new RecordingJudge());

        EngineeringAgent agent = definition.Spawn(TestSupport.Assignment());
        await agent.RunAsync(TestSupport.Compliant(), TestSupport.Pretext());

        Assert.False(agent.IsWiped);
        agent.Dispose();
        Assert.True(agent.IsWiped);
    }

    [Fact]
    public void RevealingAScrubbedLureThrowsRatherThanReturningAStaleBuffer()
    {
        ComposedLure lure = new(CleanStub, "summary", "email");

        Assert.Equal(CleanStub, lure.Reveal());

        lure.Dispose();

        Assert.True(lure.IsScrubbed);
        Assert.Throws<ObjectDisposedException>(() => lure.Reveal());
    }

    [Fact]
    public async Task ADisposedAgentCannotBeRun()
    {
        AgentDefinition definition = TestSupport.Definition();
        EngineeringAgent agent = definition.Spawn(TestSupport.Assignment());
        agent.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => agent.RunAsync(TestSupport.Compliant(), TestSupport.Pretext()));
    }

    [Fact]
    public async Task AFlaggedLureIsNeverDelivered()
    {
        FixedResponder responder = new("[SIMULATED REPLY] Done.");

        AgentDefinition definition = TestSupport.Definition(
            // Missing the watermark, so the gate must refuse it.
            composer: new FixedComposer("Unlabelled content", "Attempted a pretext."),
            responder: responder);

        using EngineeringAgent agent = definition.Spawn(TestSupport.Assignment());
        AttemptResult result = await agent.RunAsync(TestSupport.Compliant(), TestSupport.Pretext());

        Assert.Equal(AttemptOutcome.Blocked, result.Outcome);
        Assert.True(result.ContentSafetyFlagged);
        Assert.Equal(0, responder.Calls);
        Assert.Contains("missing_simulation_label", result.AttemptSummary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Design §6 makes the judgment a reading of the reply. The judge must therefore receive
    /// the reply text and nothing else — not the responder's internal behaviour label.
    /// </summary>
    [Fact]
    public async Task TheJudgeSeesOnlyTheReplyText()
    {
        RecordingJudge judge = new();
        const string reply = "[SIMULATED REPLY] I've raised it with security.";

        AgentDefinition definition = TestSupport.Definition(
            composer: new FixedComposer(CleanStub, "Attempted a pretext."),
            responder: new FixedResponder(reply, ReplyBehavior.Complied),
            judge: judge);

        using EngineeringAgent agent = definition.Spawn(TestSupport.Assignment());
        AttemptResult result = await agent.RunAsync(TestSupport.Compliant(), TestSupport.Pretext());

        Assert.Equal(reply, Assert.Single(judge.SeenReplies));

        // The responder claimed Complied; the judge said otherwise, and the judge decides.
        Assert.Equal(AttemptOutcome.Failure, result.Outcome);
    }

    [Fact]
    public async Task TheLureTextNeverAppearsInTheResultObject()
    {
        const string canary = "ZZCANARY4471ZZ";

        AgentDefinition definition = TestSupport.Definition(
            composer: new FixedComposer($"{Watermark} Canary stub {canary} in play.", "Attempted a pretext."),
            responder: new FixedResponder("[SIMULATED REPLY] Done, actioned."),
            judge: new RecordingJudge(favorable: true));

        using EngineeringAgent agent = definition.Spawn(TestSupport.Assignment());
        AttemptResult result = await agent.RunAsync(TestSupport.Compliant(), TestSupport.Pretext());

        string serialized = System.Text.Json.JsonSerializer.Serialize(result, SwarmJson.Line);
        Assert.DoesNotContain(canary, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnsafeAttemptSummaryIsReplacedBeforeItCanBeLogged()
    {
        HeuristicSafetyScreen screen = new();
        LogTextSanitizer sanitizer = new(screen);

        AgentDefinition definition = TestSupport.Definition(
            composer: new FixedComposer(CleanStub, "Target was sent to nw-login.com to sign in."),
            responder: new FixedResponder("[SIMULATED REPLY] Done, actioned."),
            judge: new RecordingJudge(favorable: true),
            sanitizer: sanitizer);

        using EngineeringAgent agent = definition.Spawn(TestSupport.Assignment());
        AttemptResult result = await agent.RunAsync(TestSupport.Compliant(), TestSupport.Pretext());

        Assert.DoesNotContain("nw-login.com", result.AttemptSummary, StringComparison.Ordinal);
        Assert.Equal(1, sanitizer.RedactionCount);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public async Task ResultTimestampComesFromTheInjectedClock()
    {
        FixedTime clock = new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        AgentDefinition definition = TestSupport.Definition(
            composer: new FixedComposer(CleanStub, "Attempted a pretext."),
            responder: new FixedResponder("[SIMULATED REPLY] Done."),
            judge: new RecordingJudge(),
            time: clock);

        using EngineeringAgent agent = definition.Spawn(TestSupport.Assignment());
        AttemptResult result = await agent.RunAsync(TestSupport.Compliant(), TestSupport.Pretext());

        Assert.Equal("2026-01-02T03:04:05Z", result.Timestamp);
    }

    /// <summary>
    /// Statelessness in practice: two clones from one definition, run against the same target,
    /// must produce identical results. Any drift would mean state survived between them.
    /// </summary>
    [Fact]
    public async Task TwoClonesFromOneDefinitionDoNotInfluenceEachOther()
    {
        AgentDefinition definition = TestSupport.Definition();
        Employee target = TestSupport.Compliant();
        PretextType pretext = TestSupport.Pretext();

        using EngineeringAgent first = definition.Spawn(TestSupport.Assignment("att-0001", seed: 7));
        AttemptResult a = await first.RunAsync(target, pretext);

        using EngineeringAgent second = definition.Spawn(TestSupport.Assignment("att-0001", seed: 7));
        AttemptResult b = await second.RunAsync(target, pretext);

        Assert.Equal(a, b);
    }
}

/// <summary>Design §3.1 — the attempt plan.</summary>
public class AttemptPlannerTests
{
    private static SyntheticOrg Roster() => TestSupport.Org(
        TestSupport.Employee(id: "emp-001", name: "One"),
        TestSupport.Employee(id: "emp-002", name: "Two"),
        TestSupport.Employee(id: "emp-003", name: "Three"));

    [Fact]
    public void IsReproducibleForAGivenSeed()
    {
        IReadOnlyList<PlannedAttempt> first = AttemptPlanner.Plan(Roster(), "TST", 9, seed: 5);
        IReadOnlyList<PlannedAttempt> second = AttemptPlanner.Plan(Roster(), "TST", 9, seed: 5);

        Assert.Equal(
            first.Select(p => p.Assignment).ToArray(),
            second.Select(p => p.Assignment).ToArray());
    }

    [Fact]
    public void DiffersBetweenSeeds()
    {
        IReadOnlyList<PlannedAttempt> a = AttemptPlanner.Plan(Roster(), "TST", 9, seed: 5);
        IReadOnlyList<PlannedAttempt> b = AttemptPlanner.Plan(Roster(), "TST", 9, seed: 6);

        Assert.NotEqual(
            a.Select(p => p.Assignment.PretextType).ToArray(),
            b.Select(p => p.Assignment.PretextType).ToArray());
    }

    [Fact]
    public void NeverRepeatsAPersonaPretextPair()
    {
        SyntheticOrg org = Roster();
        int everyPair = AttemptPlanner.MaximumAttempts(org);

        IReadOnlyList<PlannedAttempt> plan = AttemptPlanner.Plan(
            org, "TST", everyPair, seed: 11, includeSafetyProbe: false);

        var pairs = plan
            .Select(p => (p.Assignment.TargetEmployeeId, p.Assignment.PretextType))
            .ToArray();

        Assert.Equal(pairs.Length, pairs.Distinct().Count());
    }

    [Fact]
    public void SpreadsAcrossPersonasBeforeRetryingAnyone()
    {
        IReadOnlyList<PlannedAttempt> plan = AttemptPlanner.Plan(
            Roster(), "TST", 3, seed: 3, includeSafetyProbe: false);

        Assert.Equal(3, plan.Select(p => p.Assignment.TargetEmployeeId).Distinct().Count());
    }

    [Fact]
    public void PrefersPretextsMatchingSyntheticExposure()
    {
        SyntheticOrg org = TestSupport.Org(
            TestSupport.Employee(id: "emp-001", exposure: ["badge_access_main_office"]));

        IReadOnlyList<PlannedAttempt> plan = AttemptPlanner.Plan(
            org, "TST", 1, seed: 1, includeSafetyProbe: false);

        Assert.Equal("tailgating_pretext", plan[0].Assignment.PretextType);
    }

    [Fact]
    public void AppendsOneControlTestPerProbeCase()
    {
        IReadOnlyList<PlannedAttempt> plan = AttemptPlanner.Plan(
            Roster(), "TST", 3, seed: 1, includeSafetyProbe: true);

        PlannedAttempt[] control = plan.Where(p => p.IsControlTest).ToArray();

        Assert.Equal(SafetyProbe.Cases.Count, control.Length);
        Assert.Equal(
            SafetyProbe.Cases.Select(c => c.Id).ToArray(),
            control.Select(c => c.Assignment.Tactic).ToArray());
    }

    [Fact]
    public void OmitsControlTestsWhenAskedTo()
    {
        IReadOnlyList<PlannedAttempt> plan = AttemptPlanner.Plan(
            Roster(), "TST", 3, seed: 1, includeSafetyProbe: false);

        Assert.DoesNotContain(plan, p => p.IsControlTest);
    }

    [Fact]
    public void AttemptIdsAreSequentialAndUnique()
    {
        IReadOnlyList<PlannedAttempt> plan = AttemptPlanner.Plan(Roster(), "TST", 6, seed: 2);

        string[] ids = plan.Select(p => p.Assignment.AttemptId).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal("att-0001", ids[0]);
        Assert.Equal(ids.OrderBy(i => i, StringComparer.Ordinal).ToArray(), ids);
    }

    [Fact]
    public void EveryTacticIsDrawnFromItsPretextsCombinations()
    {
        IReadOnlyList<PlannedAttempt> plan = AttemptPlanner.Plan(
            Roster(), "TST", AttemptPlanner.MaximumAttempts(Roster()), seed: 4, includeSafetyProbe: false);

        foreach (PlannedAttempt planned in plan)
        {
            string[] allowed = planned.Pretext.TacticCombinations
                .Select(PretextCatalog.FormatTactic)
                .ToArray();

            Assert.Contains(planned.Assignment.Tactic, allowed);
        }
    }

    /// <summary>
    /// Seeds must be derived with a stable mix. <see cref="HashCode"/> is randomised per
    /// process, so using it would silently break reproducibility across runs.
    /// </summary>
    [Fact]
    public void DerivedSeedsAreStableAndDistinct()
    {
        int a = AttemptPlanner.DeriveSeed(100, "att-0001");
        int b = AttemptPlanner.DeriveSeed(100, "att-0002");

        Assert.Equal(a, AttemptPlanner.DeriveSeed(100, "att-0001"));
        Assert.NotEqual(a, b);
        Assert.True(a >= 0);
    }
}

/// <summary>Design §3.4 — the rule-weighted persona responder.</summary>
public class RuleWeightedResponderTests
{
    [Fact]
    public async Task IsDeterministicForAGivenSeed()
    {
        RuleWeightedResponder responder = new();
        using ComposedLure lure = new("[SIMULATED] stub", "summary", "email");
        Employee target = TestSupport.Employee();

        SimulatedReply first = await responder.RespondAsync(
            lure, target, TestSupport.Pretext(), TestSupport.Assignment(seed: 99));
        SimulatedReply second = await responder.RespondAsync(
            lure, target, TestSupport.Pretext(), TestSupport.Assignment(seed: 99));

        Assert.Equal(first.Behavior, second.Behavior);
        Assert.Equal(first.Text, second.Text);
    }

    [Fact]
    public void ScoresASusceptiblePersonaAboveAResistantOne()
    {
        PretextType pretext = TestSupport.Pretext();

        double soft = RuleWeightedResponder.ScoreCompliance(
            TestSupport.Compliant(), pretext, "urgency + authority", new Random(1));
        double hard = RuleWeightedResponder.ScoreCompliance(
            TestSupport.Resistant(), pretext, "urgency + authority", new Random(1));

        Assert.True(soft > hard, $"expected soft ({soft:0.###}) > hard ({hard:0.###})");
    }

    [Fact]
    public void MatchingExposureRaisesTheScore()
    {
        PretextType pretext = PretextCatalog.Require("tailgating_pretext");
        PersonaTraits traits = TestSupport.Traits(helpfulness: 0.8, verification: 0.2);

        double without = RuleWeightedResponder.ScoreCompliance(
            TestSupport.Employee(traits: traits), pretext, "reciprocity + familiarity", new Random(1));
        double with = RuleWeightedResponder.ScoreCompliance(
            TestSupport.Employee(exposure: ["badge_access_main_office"], traits: traits),
            pretext, "reciprocity + familiarity", new Random(1));

        Assert.True(with > without);
    }

    [Fact]
    public async Task AHighlySusceptiblePersonaReplaysFavorably()
    {
        RuleWeightedResponder responder = new();
        using ComposedLure lure = new("[SIMULATED] stub", "summary", "email");

        SimulatedReply reply = await responder.RespondAsync(
            lure, TestSupport.Compliant(), TestSupport.Pretext(), TestSupport.Assignment(seed: 3));

        Assert.NotNull(reply.Behavior);
        Assert.True(SimulatedReply.IsFavorable(reply.Behavior!.Value));
    }

    [Fact]
    public async Task AResistantPersonaEscalates()
    {
        RuleWeightedResponder responder = new();
        using ComposedLure lure = new("[SIMULATED] stub", "summary", "email");

        SimulatedReply reply = await responder.RespondAsync(
            lure, TestSupport.Resistant(), TestSupport.Pretext(), TestSupport.Assignment(seed: 3));

        Assert.Equal(ReplyBehavior.Reported, reply.Behavior);
    }

    [Fact]
    public async Task RecordsGroundTruthForTheOrchestratorButNotTheAgent()
    {
        RuleWeightedResponder responder = new();
        using ComposedLure lure = new("[SIMULATED] stub", "summary", "email");
        AgentAssignment assignment = TestSupport.Assignment("att-0042");

        Assert.False(responder.TryGetBehavior("att-0042", out _));

        SimulatedReply reply = await responder.RespondAsync(
            lure, TestSupport.Compliant(), TestSupport.Pretext(), assignment);

        Assert.True(responder.TryGetBehavior("att-0042", out ReplyBehavior recorded));
        Assert.Equal(reply.Behavior, recorded);
    }

    [Fact]
    public async Task RepliesAreLabelledExceptWhenThereIsNoReply()
    {
        RuleWeightedResponder responder = new();
        using ComposedLure lure = new("[SIMULATED] stub", "summary", "email");

        SimulatedReply reply = await responder.RespondAsync(
            lure, TestSupport.Compliant(), TestSupport.Pretext(), TestSupport.Assignment(seed: 3));

        Assert.StartsWith("[SIMULATED REPLY]", reply.Text, StringComparison.Ordinal);
    }
}

/// <summary>Design §6 — judging a reply from its wording alone.</summary>
public class HeuristicReplyJudgeTests
{
    private readonly HeuristicReplyJudge _judge = new();

    private async Task<ReplyJudgment> Judge(string reply) =>
        await _judge.JudgeAsync(reply, TestSupport.Assignment(), TestSupport.Pretext());

    [Theory]
    [InlineData("[SIMULATED REPLY] Done — I've gone ahead and actioned that for you.")]
    [InlineData("[SIMULATED REPLY] No problem, I've taken care of it just now.")]
    [InlineData("[SIMULATED REPLY] I've passed on what I can and started the change.")]
    public async Task TreatsComplianceAsFavorable(string reply) =>
        Assert.True((await Judge(reply)).Favorable);

    [Theory]
    [InlineData("[SIMULATED REPLY] I've forwarded this to the security team as a suspected phishing attempt.")]
    [InlineData("[SIMULATED REPLY] Which ticket is this against? I don't recognise this request.")]
    [InlineData("(no response — message left unopened)")]
    [InlineData("[SIMULATED REPLY] Who authorised this? I'd like to check with my manager.")]
    public async Task TreatsResistanceAsUnfavorable(string reply) =>
        Assert.False((await Judge(reply)).Favorable);

    /// <summary>An escalation can mention acting; resistance must be tested first.</summary>
    [Fact]
    public async Task PrefersEscalationOverAnApparentAction()
    {
        ReplyJudgment judgment = await Judge(
            "[SIMULATED REPLY] Not actioning this. I've raised it with security and blocked the sender.");

        Assert.False(judgment.Favorable);
        Assert.Contains("escalated", judgment.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Success must require positive evidence, never the absence of a refusal.</summary>
    [Fact]
    public async Task DefaultsToUnfavorableForAnUnrecognisedReply()
    {
        ReplyJudgment judgment = await Judge("[SIMULATED REPLY] Hmm.");

        Assert.False(judgment.Favorable);
        Assert.Contains("no evidence", judgment.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AlwaysProducesANonEmptyReason()
    {
        foreach (string reply in new[] { "Done — actioned.", "Reported to security.", "???" })
        {
            Assert.False(string.IsNullOrWhiteSpace((await Judge(reply)).Reason));
        }
    }
}
