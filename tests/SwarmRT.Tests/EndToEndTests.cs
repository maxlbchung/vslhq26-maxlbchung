using SwarmRT.Agents;
using SwarmRT.Contracts;
using SwarmRT.Logging;
using SwarmRT.Org;
using SwarmRT.Orchestration;
using SwarmRT.Reporting;
using SwarmRT.Responders;
using SwarmRT.Safety;

namespace SwarmRT.Tests;

/// <summary>
/// A composer that plants a traceable string in every lure, so a test can prove where that
/// text does and does not end up.
/// </summary>
internal sealed class CanaryComposer(string canary) : ILureComposer
{
    public string Description => "canary composer";

    public Task<ComposedLure> ComposeAsync(
        AgentAssignment assignment,
        Employee target,
        PretextType pretext,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ComposedLure(
            $"{HeuristicSafetyScreen.RequiredWatermark} Canary stub {canary} standing in for a pretext.",
            $"Attempted {pretext.SentenceLabel} using {assignment.Tactic}.",
            pretext.Channel));
}

/// <summary>Full-pipeline runs: orchestrator, logger, statistics, and report generation.</summary>
public class EndToEndTests : IDisposable
{
    private readonly string _directory = TestSupport.TempDirectory();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SyntheticOrg Roster() => TestSupport.Org(
        TestSupport.Compliant("emp-001"),
        TestSupport.Resistant("emp-002"),
        TestSupport.Employee(id: "emp-003", name: "Middle Ground"));

    private async Task<EngagementResult> RunAsync(
        SyntheticOrg org,
        int attempts = 9,
        bool includeProbe = true,
        ILureComposer? composer = null,
        LogTextSanitizer? sanitizer = null,
        int concurrency = 1)
    {
        HeuristicSafetyScreen screen = new();
        LogTextSanitizer effectiveSanitizer = sanitizer ?? new LogTextSanitizer(screen);

        AgentDefinition definition = new()
        {
            Composer = composer ?? new TemplateLureComposer(),
            Gate = new LayeredContentSafetyGate(screen),
            Responder = new RuleWeightedResponder(),
            Judge = new HeuristicReplyJudge(),
            Sanitizer = effectiveSanitizer,
            Time = TimeProvider.System,
        };

        Orchestrator orchestrator = new(
            definition, new ControlTestLureComposer(), effectiveSanitizer, TimeProvider.System);

        IReadOnlyList<PlannedAttempt> plan = AttemptPlanner.Plan(
            org, "TST-2026-07", attempts, seed: 20260729, includeSafetyProbe: includeProbe);

        return await orchestrator.RunAsync(
            org,
            plan,
            new OrchestratorOptions
            {
                OutputDirectory = _directory,
                EngagementSeed = 20260729,
                Concurrency = concurrency,
            });
    }

    [Fact]
    public async Task LogsExactlyOneRowPerPlannedAttempt()
    {
        EngagementResult engagement = await RunAsync(Roster(), attempts: 9);

        Assert.Equal(engagement.Manifest.AttemptsPlanned, engagement.Manifest.AttemptsLogged);
        Assert.Equal(engagement.Results.Count, File.ReadAllLines(engagement.LogPath).Length);

        IReadOnlyList<AttemptResult> reread = AttemptLogReader.Read(engagement.LogPath);
        Assert.Equal(engagement.Results, reread);
    }

    [Fact]
    public async Task EveryLoggedRowSatisfiesTheDesignContract()
    {
        EngagementResult engagement = await RunAsync(Roster());

        Assert.All(AttemptLogReader.Read(engagement.LogPath), row => Assert.Empty(row.Validate()));
    }

    [Fact]
    public async Task AttemptIdsAreUniqueAcrossTheLog()
    {
        EngagementResult engagement = await RunAsync(Roster());

        string[] ids = engagement.Results.Select(r => r.AttemptId).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    /// <summary>
    /// The central safety property of design §8.3: the composed lure exists only in memory.
    /// Nothing written to the output directory — log, manifest, or any report — may contain it.
    /// </summary>
    [Fact]
    public async Task NoLureTextEverReachesDisk()
    {
        const string canary = "ZZCANARY8823ZZ";

        EngagementResult engagement = await RunAsync(
            Roster(), attempts: 9, composer: new CanaryComposer(canary));

        EngagementStatistics stats = EngagementStatistics.From(engagement.Results, Roster());
        await new ReportGenerator().GenerateAsync(
            stats, Path.Combine(_directory, "reports"), engagement.Manifest);

        string[] files = Directory.GetFiles(_directory, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            Assert.DoesNotContain(
                canary,
                await File.ReadAllTextAsync(file),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ControlTestsAreAllBlockedBeforeDelivery()
    {
        EngagementResult engagement = await RunAsync(Roster(), includeProbe: true);

        Assert.Equal(SafetyProbe.Cases.Count, engagement.Manifest.ControlTestsRun);
        Assert.Equal(engagement.Manifest.ControlTestsRun, engagement.Manifest.ControlTestsBlocked);

        AttemptResult[] control = engagement.Results
            .Where(r => SafetyProbe.IsControlRow(r.PretextType))
            .ToArray();

        Assert.All(control, row =>
        {
            Assert.Equal(AttemptOutcome.Blocked, row.Outcome);
            Assert.True(row.ContentSafetyFlagged);
            Assert.Null(row.SuccessReason);
            Assert.Null(row.FailureReason);
        });
    }

    [Fact]
    public async Task ManifestDigestMatchesTheLogItDescribes()
    {
        EngagementResult engagement = await RunAsync(Roster());

        Assert.Equal(
            engagement.Manifest.LogSha256Chain,
            AttemptLogReader.ComputeDigest(engagement.LogPath));
    }

    [Fact]
    public async Task ManifestRecordsTheEngagementSeedNotADerivedOne()
    {
        EngagementResult engagement = await RunAsync(Roster());

        Assert.Equal(20260729, engagement.Manifest.Seed);
    }

    [Fact]
    public async Task ManifestNamesTheEngineSoTemplateOutputIsNotMistakenForModelOutput()
    {
        EngagementResult engagement = await RunAsync(Roster());

        Assert.Contains("none", engagement.Manifest.Engine.Backend, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "no model backend", engagement.Manifest.Engine.Composer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusesToRunAgainstANonSyntheticRoster()
    {
        SyntheticOrg org = TestSupport.Org(TestSupport.Employee(email: "real@contoso.com"));

        await Assert.ThrowsAsync<NonSyntheticTargetException>(() => RunAsync(org, attempts: 2));
    }

    /// <summary>
    /// An attempt that fails outright must not be invented as an outcome. It belongs in the
    /// manifest's error list, and nowhere in the log.
    /// </summary>
    [Fact]
    public async Task AFailedAttemptIsRecordedAsAnErrorNotAFabricatedOutcome()
    {
        HeuristicSafetyScreen screen = new();
        LogTextSanitizer sanitizer = new(screen);

        AgentDefinition definition = new()
        {
            Composer = new FailingComposer("simulated backend outage"),
            Gate = new LayeredContentSafetyGate(screen),
            Responder = new RuleWeightedResponder(),
            Judge = new HeuristicReplyJudge(),
            Sanitizer = sanitizer,
            Time = TimeProvider.System,
        };

        Orchestrator orchestrator = new(
            definition, new ControlTestLureComposer(), sanitizer, TimeProvider.System);

        SyntheticOrg org = Roster();
        IReadOnlyList<PlannedAttempt> plan = AttemptPlanner.Plan(
            org, "TST-2026-07", 3, seed: 1, includeSafetyProbe: false);

        EngagementResult engagement = await orchestrator.RunAsync(
            org, plan, new OrchestratorOptions { OutputDirectory = _directory });

        Assert.Empty(engagement.Results);
        Assert.Equal(3, engagement.Manifest.Errors.Count);
        Assert.Equal(0, engagement.Manifest.AttemptsLogged);
        Assert.All(engagement.Manifest.Errors, e =>
            Assert.Contains("simulated backend outage", e.Error, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ControlTestsStillRunWhenAttackAttemptsFail()
    {
        // Control rows use their own composer, so a broken attack composer must not disable them.
        HeuristicSafetyScreen screen = new();
        LogTextSanitizer sanitizer = new(screen);

        AgentDefinition definition = new()
        {
            Composer = new FailingComposer(),
            Gate = new LayeredContentSafetyGate(screen),
            Responder = new RuleWeightedResponder(),
            Judge = new HeuristicReplyJudge(),
            Sanitizer = sanitizer,
            Time = TimeProvider.System,
        };

        Orchestrator orchestrator = new(
            definition, new ControlTestLureComposer(), sanitizer, TimeProvider.System);

        SyntheticOrg org = Roster();
        IReadOnlyList<PlannedAttempt> plan = AttemptPlanner.Plan(
            org, "TST-2026-07", 3, seed: 1, includeSafetyProbe: true);

        EngagementResult engagement = await orchestrator.RunAsync(
            org, plan, new OrchestratorOptions { OutputDirectory = _directory });

        Assert.Equal(SafetyProbe.Cases.Count, engagement.Manifest.ControlTestsBlocked);
    }

    [Fact]
    public async Task ConcurrentRunsLogEveryAttemptExactlyOnce()
    {
        EngagementResult engagement = await RunAsync(Roster(), attempts: 9, concurrency: 4);

        Assert.Equal(engagement.Manifest.AttemptsPlanned, engagement.Manifest.AttemptsLogged);

        string[] ids = AttemptLogReader.Read(engagement.LogPath)
            .Select(r => r.AttemptId)
            .ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public async Task JudgeAgreementIsMeasuredAgainstThePersonaModel()
    {
        EngagementResult engagement = await RunAsync(Roster());

        JudgeAgreement agreement = Assert.IsType<JudgeAgreement>(engagement.Manifest.JudgeAgreement);

        Assert.Equal(
            engagement.Results.Count(r => r.Outcome != AttemptOutcome.Blocked),
            agreement.ComparableAttempts);
        Assert.Equal(agreement.ComparableAttempts, agreement.Agreements);
    }

    [Fact]
    public async Task GeneratesAnOrgSummaryAndOneReportPerTestedPersona()
    {
        SyntheticOrg org = Roster();
        EngagementResult engagement = await RunAsync(org);

        EngagementStatistics stats = EngagementStatistics.From(engagement.Results, org);
        ReportOutputs reports = await new ReportGenerator().GenerateAsync(
            stats, Path.Combine(_directory, "reports"), engagement.Manifest);

        Assert.True(File.Exists(reports.OrgSummaryPath));
        Assert.Equal(stats.Employees.Count, reports.EmployeeReportPaths.Count);
        Assert.All(reports.EmployeeReportPaths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task EveryReportCarriesTheSimulationBanner()
    {
        SyntheticOrg org = Roster();
        EngagementResult engagement = await RunAsync(org);

        EngagementStatistics stats = EngagementStatistics.From(engagement.Results, org);
        ReportOutputs reports = await new ReportGenerator().GenerateAsync(
            stats, Path.Combine(_directory, "reports"), engagement.Manifest);

        foreach (string path in reports.EmployeeReportPaths.Append(reports.OrgSummaryPath))
        {
            string text = await File.ReadAllTextAsync(path);
            Assert.Contains("Simulation artefact", text, StringComparison.Ordinal);
        }
    }
}

/// <summary>Design §3.7 — aggregation over the log.</summary>
public class EngagementStatisticsTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 29, 18, 42, 11, TimeSpan.Zero);

    private static AttemptResult Success(string id, string employee, string pretext, string tactic) =>
        AttemptResult.ForSuccess(
            TestSupport.Assignment(id, employee, pretext, tactic), At,
            "Target acted without verifying the sender.", "summary");

    private static AttemptResult Failure(string id, string employee, string pretext, string reason) =>
        AttemptResult.ForFailure(
            TestSupport.Assignment(id, employee, pretext), At, reason, "summary");

    private static SyntheticOrg Roster() => TestSupport.Org(
        TestSupport.Employee(id: "emp-001", name: "One"),
        TestSupport.Employee(id: "emp-002", name: "Two"),
        TestSupport.Employee(id: "emp-003", name: "Three"));

    [Fact]
    public void TalliesOutcomesForAttackAttemptsOnly()
    {
        AttemptResult[] rows =
        [
            Success("att-0001", "emp-001", "it_helpdesk_impersonation", "urgency + authority"),
            Failure("att-0002", "emp-002", "it_helpdesk_impersonation", "Target reported it to security."),
            AttemptResult.ForBlocked(
                TestSupport.Assignment("att-0003", "emp-001", SafetyProbe.PretextId, "routable-host"),
                At, "blocked"),
        ];

        EngagementStatistics stats = EngagementStatistics.From(rows, Roster());

        Assert.Equal(2, stats.Tally.Total);
        Assert.Equal(1, stats.Tally.Success);
        Assert.Equal(1, stats.Tally.Failure);
        Assert.Equal(0, stats.Tally.Blocked);

        Assert.Equal(1, stats.ControlTally.Total);
        Assert.Equal(1, stats.ControlTally.Blocked);
    }

    [Fact]
    public void ExcludesBlockedAttemptsFromTheSuccessRate()
    {
        AttemptResult[] rows =
        [
            Success("att-0001", "emp-001", "it_helpdesk_impersonation", "urgency + authority"),
            AttemptResult.ForBlocked(
                TestSupport.Assignment("att-0002", "emp-001", "it_helpdesk_impersonation"), At, "blocked"),
        ];

        EngagementStatistics stats = EngagementStatistics.From(rows, Roster());

        Assert.Equal(1, stats.Tally.Delivered);
        Assert.Equal(1.0, stats.Tally.SuccessRate);
    }

    [Fact]
    public void SuccessRateIsNullWhenNothingWasDelivered()
    {
        AttemptResult[] rows =
        [
            AttemptResult.ForBlocked(
                TestSupport.Assignment("att-0001", "emp-001", "it_helpdesk_impersonation"), At, "blocked"),
        ];

        Assert.Null(EngagementStatistics.From(rows, Roster()).Tally.SuccessRate);
    }

    [Fact]
    public void SplitsTacticPairsIntoIndividualLevers()
    {
        AttemptResult[] rows =
        [
            Success("att-0001", "emp-001", "it_helpdesk_impersonation", "urgency + authority"),
            Failure("att-0002", "emp-002", "it_helpdesk_impersonation", "Target ignored it."),
        ];

        EngagementStatistics stats = EngagementStatistics.From(rows, Roster());

        SliceStats urgency = Assert.Single(stats.ByLever, s => s.Key == "urgency");
        Assert.Equal(1, urgency.Tally.Success);
        Assert.Contains("authority", stats.ByLever.Select(s => s.Key));
    }

    [Fact]
    public void RanksTheMostSusceptiblePersonaFirst()
    {
        AttemptResult[] rows =
        [
            Success("att-0001", "emp-001", "it_helpdesk_impersonation", "urgency + authority"),
            Success("att-0002", "emp-001", "hr_benefits_notice", "authority + curiosity"),
            Failure("att-0003", "emp-002", "it_helpdesk_impersonation", "Target reported it to security."),
        ];

        EngagementStatistics stats = EngagementStatistics.From(rows, Roster());

        Assert.Equal("emp-001", stats.SusceptibilityRanking[0].Employee.Id);
        Assert.Equal(1.0, stats.SusceptibilityRanking[0].Tally.SuccessRate);
    }

    [Fact]
    public void ListsPersonasThatReceivedNoAttempt()
    {
        AttemptResult[] rows =
        [
            Success("att-0001", "emp-001", "it_helpdesk_impersonation", "urgency + authority"),
        ];

        EngagementStatistics stats = EngagementStatistics.From(rows, Roster());

        Assert.Equal(2, stats.UntestedEmployees.Count);
        Assert.DoesNotContain("emp-001", stats.UntestedEmployees.Select(e => e.Id));
    }

    [Fact]
    public void FailsWhenTheLogReferencesSomeoneOutsideTheRoster()
    {
        AttemptResult[] rows =
        [
            Success("att-0001", "emp-999", "it_helpdesk_impersonation", "urgency + authority"),
        ];

        Assert.Throws<InvalidDataException>(() => EngagementStatistics.From(rows, Roster()));
    }

    [Theory]
    [InlineData("Target escalated the message to the security team.", ResistanceSignal.Escalated)]
    [InlineData("Target reported it rather than acting.", ResistanceSignal.Escalated)]
    [InlineData("Target asked to verify through a known channel first.", ResistanceSignal.Verified)]
    [InlineData("Target requested a ticket number before acting.", ResistanceSignal.Verified)]
    [InlineData("Target never engaged with the message.", ResistanceSignal.Disengaged)]
    [InlineData("Something else entirely happened.", ResistanceSignal.Unclassified)]
    [InlineData(null, ResistanceSignal.Unclassified)]
    public void ClassifiesResistanceFromTheFailureReason(string? reason, ResistanceSignal expected) =>
        Assert.Equal(expected, EngagementStatistics.Classify(reason));

    [Fact]
    public void CountsResistanceSignalsPerPersona()
    {
        AttemptResult[] rows =
        [
            Failure("att-0001", "emp-001", "it_helpdesk_impersonation", "Target reported it to security."),
            Failure("att-0002", "emp-001", "hr_benefits_notice", "Target asked to verify it first."),
            Failure("att-0003", "emp-001", "survey_incentive", "Target never engaged with the message."),
        ];

        EngagementStatistics stats = EngagementStatistics.From(rows, Roster());
        EmployeeStats employee = Assert.Single(stats.Employees);

        Assert.Equal(1, employee.ResistanceSignals[ResistanceSignal.Escalated]);
        Assert.Equal(1, employee.ResistanceSignals[ResistanceSignal.Verified]);
        Assert.Equal(1, employee.ResistanceSignals[ResistanceSignal.Disengaged]);
    }

    [Fact]
    public void IdentifiesWhichLeversWorkedAndWhichDidNot()
    {
        AttemptResult[] rows =
        [
            Success("att-0001", "emp-001", "it_helpdesk_impersonation", "urgency + authority"),
            Failure("att-0002", "emp-001", "shared_document_notification", "Target ignored it."),
        ];

        EngagementStatistics stats = EngagementStatistics.From(rows, Roster());
        EmployeeStats employee = Assert.Single(stats.Employees);

        Assert.Contains("urgency", employee.WinningLevers.Select(l => l.Key));
        Assert.Contains("authority", employee.WinningLevers.Select(l => l.Key));
        Assert.DoesNotContain("urgency", employee.IneffectiveLevers);
    }
}
