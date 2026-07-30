using System.Net;
using SwarmRT.Agents;
using SwarmRT.Contracts;
using SwarmRT.Model;
using SwarmRT.Org;
using SwarmRT.Orchestration;
using SwarmRT.Reporting;
using SwarmRT.Responders;
using SwarmRT.Safety;

namespace SwarmRT.Cli;

/// <summary>
/// Wires the components together and runs one engagement end to end: plan, dispatch the
/// swarm, log, report.
/// <para>
/// The choice of engine is deliberately explicit in both directions. With a token present
/// the agents are real model calls; without one the run falls back to the deterministic
/// engine and says so on the console, in the run manifest, and in every generated report —
/// template output is never presented as model output.
/// </para>
/// </summary>
public static class RunCommand
{
    public static async Task<int> ExecuteAsync(Arguments args, CancellationToken cancellationToken)
    {
        string orgPath = OrgLoader.ResolveDefaultPath(args.String("org"));
        string outputDirectory = Path.GetFullPath(args.String("out", "out")!);
        int seed = args.Int("seed", 20260729);
        int attempts = args.Int("attempts", 24, min: 1, max: 5000);
        int requestsPerMinute = args.Int("rpm", 10, min: 1, max: 600);
        int concurrency = args.Int("concurrency", 1, min: 1, max: 8);
        bool includeProbe = args.Flag("safety-probe", defaultValue: false);
        bool selfCheck = args.Flag("safety-self-check", defaultValue: true);
        bool narrative = args.Flag("narrative");
        bool failFast = args.Flag("fail-fast");
        bool overwrite = args.Flag("overwrite");
        bool quiet = args.Flag("quiet");
        bool dashboard = args.Flag("dashboard");
        int port = args.Int("port", 8760, min: 1, max: 65535);
        int paceMs = args.Int("pace", 0, min: 0, max: 60000);
        string? endpoint = args.String("endpoint");
        string? modelId = args.String("model");
        string? keyVariable = args.String("key-env");
        string? engagementIdOverride = args.String("engagement-id");
        string? target = args.String("target");

        args.EnsureAllConsumed();

        // Load and validate the roster before anything else: a bad roster must stop the run
        // rather than be discovered mid-engagement.
        SyntheticOrg org = OrgLoader.Load(orgPath);

        string engagementId = engagementIdOverride
                              ?? $"{org.ResolveShortCode()}-{DateTimeOffset.UtcNow:yyyy-MM}";

        // The unique-pair cap only applies to the round-robin plan; single-target mode
        // deliberately reuses pretexts, so any attempt count is meaningful there.
        int maxUseful = AttemptPlanner.MaximumAttempts(org);
        if (target is null && attempts > maxUseful)
        {
            Console.Error.WriteLine(
                $"note: --attempts {attempts} exceeds the {maxUseful} unique persona/pretext pairs " +
                $"available for this roster; planning {maxUseful} instead.");
            attempts = maxUseful;
        }

        string logPath = Path.Combine(outputDirectory, $"{engagementId}.jsonl");
        if (File.Exists(logPath) && new FileInfo(logPath).Length > 0)
        {
            if (!overwrite)
            {
                Console.Error.WriteLine(
                    $"error: '{logPath}' already exists and holds a previous engagement.");
                Console.Error.WriteLine(
                    "       The log is append-only, so continuing would mix two engagements into one report.");
                Console.Error.WriteLine(
                    "       Pass --overwrite to replace it, or --engagement-id / --out to write elsewhere.");
                return 2;
            }

            File.Delete(logPath);
        }

        // ------------------------------------------------------------ model backend
        // This tool exists to watch a swarm of model agents socially-engineer a model
        // persona. There is no non-model path: composer, victim, and judge are all model
        // calls, so a run without a backend is a configuration error, not a fallback.

        ModelBackendOptions backendOptions = new()
        {
            Endpoint = endpoint ?? ModelBackendOptions.GitHubModelsEndpoint,
            Model = modelId ?? ModelBackendOptions.DefaultModel,
            KeyVariable = keyVariable,
            RequestsPerMinute = requestsPerMinute,
            MaxConcurrency = concurrency,
            DisplayName = endpoint is null ? "GitHub Models" : "OpenAI-compatible endpoint",
        };

        (string? apiKey, string variablesTried) = backendOptions.ResolveKey();
        if (apiKey is null)
        {
            Console.Error.WriteLine(
                $"error: no API key found in {variablesTried}. This tool runs the agents against a");
            Console.Error.WriteLine(
                "       model, so a backend is required. For GitHub Models, create a PAT with Models");
            Console.Error.WriteLine(
                "       access and set GITHUB_TOKEN.");
            return 2;
        }

        using ModelThrottle throttle = new(requestsPerMinute, concurrency);
        using OpenAiCompatibleModelClient model = new(backendOptions, apiKey, throttle);

        // The safety gate, sanitizer, and synthetic-org guard are deterministic on purpose:
        // they are guardrails, not the experiment. They stay regardless of the backend.
        HeuristicSafetyScreen heuristics = new();
        LogTextSanitizer sanitizer = new(heuristics);

        ILureComposer composer = new ModelLureComposer(model);
        IReplyJudge judge = new ModelReplyJudge(model);
        IEmployeeResponder responder = new ModelPersonaResponder(model);

        LayeredContentSafetyGate gate = new(
            heuristics,
            selfCheck ? new ModelSelfCheckGate(model) : null);

        AgentDefinition definition = new()
        {
            Composer = composer,
            Gate = gate,
            Responder = responder,
            Judge = judge,
            Sanitizer = sanitizer,
            Time = TimeProvider.System,
        };

        IReadOnlyList<PlannedAttempt> plan = AttemptPlanner.Plan(
            org, engagementId, attempts, seed, includeProbe, target);

        if (!quiet)
        {
            PrintHeader(org, engagementId, plan, definition, model, throttle, outputDirectory);
        }

        // ------------------------------------------------------------------- the run

        LiveDashboard? dash = null;
        if (dashboard)
        {
            try
            {
                dash = new LiveDashboard(port);
                dash.Start();
            }
            catch (HttpListenerException ex)
            {
                Console.Error.WriteLine(
                    $"note: could not start the dashboard on port {port} ({ex.Message}); " +
                    "continuing without it. Try --port with a free port.");
                dash = null;
            }
        }

        Orchestrator orchestrator = new(
            definition, new ControlTestLureComposer(), sanitizer, TimeProvider.System);

        // The model backend already paces itself through the throttle; --pace only exists to
        // slow a run down further if you want to watch it attempt-by-attempt.
        TimeSpan pace = paceMs > 0 ? TimeSpan.FromMilliseconds(paceMs) : TimeSpan.Zero;

        int completed = 0;
        OrchestratorOptions options = new()
        {
            OutputDirectory = outputDirectory,
            EngagementSeed = seed,
            Concurrency = concurrency,
            FailFast = failFast,
            DelayBetweenAttempts = pace,
            OnAttemptCompleted = (planned, result) =>
            {
                completed++;
                dash?.PublishAttempt(completed, plan.Count, planned, result);
                if (!quiet)
                {
                    PrintAttempt(completed, plan.Count, planned, result);
                }
            },
            OnAttemptFailed = (planned, error) =>
            {
                completed++;
                dash?.PublishError(completed, plan.Count, planned, error);
                Console.Error.WriteLine(
                    $"[{completed}/{plan.Count}] {planned.Assignment.AttemptId} " +
                    $"{planned.Assignment.PretextType} -> ERROR {error.Message}");
            },
        };

        EngagementResult engagement = await orchestrator
            .RunAsync(org, plan, options, model, throttle, cancellationToken)
            .ConfigureAwait(false);

        // ---------------------------------------------------------------- reporting

        EngagementStatistics stats = EngagementStatistics.From(engagement.Results, org);

        NarrativeWriter? narrativeWriter = narrative ? new NarrativeWriter(model, sanitizer) : null;

        string reportDirectory = Path.Combine(outputDirectory, "reports");
        ReportOutputs reports = await new ReportGenerator()
            .GenerateAsync(stats, reportDirectory, engagement.Manifest, narrativeWriter, cancellationToken)
            .ConfigureAwait(false);

        PrintSummary(engagement, stats, reports, gate);
        dash?.Complete(reports.OrgSummaryPath, ReportSummary.From(stats, engagement.Manifest));

        // A control test that failed to block means the safety claim is unproven.
        int exitCode = 0;
        if (engagement.Manifest.ControlTestsRun > 0 &&
            engagement.Manifest.ControlTestsBlocked != engagement.Manifest.ControlTestsRun)
        {
            Console.Error.WriteLine(
                "error: one or more content-safety control tests were not blocked. " +
                "Treat the gate as unverified.");
            exitCode = 1;
        }
        else if (engagement.Manifest.Errors.Count > 0 && failFast)
        {
            exitCode = 1;
        }

        if (dash is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"  dashboard  {dash.Url}  (Ctrl+C to stop)");
            dash.OpenBrowser();

            // Keep serving so the run can be browsed after it finishes; Ctrl+C ends it.
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            dash.Dispose();
        }

        return exitCode;
    }

    private static void PrintHeader(
        SyntheticOrg org,
        string engagementId,
        IReadOnlyList<PlannedAttempt> plan,
        AgentDefinition definition,
        IModelClient? model,
        ModelThrottle? throttle,
        string outputDirectory)
    {
        int attackAttempts = plan.Count(p => !p.IsControlTest);
        int controlTests = plan.Count - attackAttempts;

        Console.WriteLine("SwarmRT — stateless agent-swarm social-engineering simulator");
        Console.WriteLine("Simulation only. Synthetic org, non-routable domain, nothing delivered anywhere.");
        Console.WriteLine();
        Console.WriteLine($"  engagement    {engagementId}");
        Console.WriteLine($"  synthetic org {org.OrgName} ({org.Domain}), {org.Employees.Count} personas");
        Console.WriteLine($"  plan          {attackAttempts} attack attempts + {controlTests} safety control tests");
        Console.WriteLine($"  composer      {definition.Composer.Description}");
        Console.WriteLine($"  safety gate   {definition.Gate.Description}");
        Console.WriteLine($"  responder     {definition.Responder.Description}");
        Console.WriteLine($"  judgment      {definition.Judge.Description}");
        Console.WriteLine($"  output        {outputDirectory}");

        if (model is not null && throttle is not null)
        {
            int callsPerAttempt = EstimateCallsPerAttempt(definition);
            double minutes = plan.Count * callsPerAttempt / (double)throttle.RequestsPerMinute;
            Console.WriteLine(
                $"  pacing        {throttle.RequestsPerMinute} req/min, concurrency {throttle.MaxConcurrency}; " +
                $"~{plan.Count * callsPerAttempt} calls, ~{minutes:0.0} min");
        }

        Console.WriteLine();
    }

    private static int EstimateCallsPerAttempt(AgentDefinition definition)
    {
        int calls = 0;
        if (definition.Composer is ModelLureComposer)
        {
            calls++;
        }

        if (definition.Gate is LayeredContentSafetyGate { Description: var d } &&
            d.Contains("self-check", StringComparison.OrdinalIgnoreCase))
        {
            calls++;
        }

        if (definition.Responder is ModelPersonaResponder)
        {
            calls++;
        }

        if (definition.Judge is ModelReplyJudge)
        {
            calls++;
        }

        return Math.Max(calls, 1);
    }

    private static void PrintAttempt(
        int index, int total, PlannedAttempt planned, AttemptResult result)
    {
        string outcome = result.Outcome switch
        {
            AttemptOutcome.Success => "SUCCESS",
            AttemptOutcome.Failure => "failure",
            _ => "BLOCKED",
        };

        string label = planned.IsControlTest
            ? $"control:{planned.Assignment.Tactic}"
            : planned.Assignment.PretextType;

        Console.WriteLine(
            $"[{index,3}/{total}] {result.AttemptId}  {planned.Target.Name,-18} " +
            $"{Truncate(label, 30),-30} {outcome}");
    }

    private static void PrintSummary(
        EngagementResult engagement,
        EngagementStatistics stats,
        ReportOutputs reports,
        LayeredContentSafetyGate gate)
    {
        EngagementManifest manifest = engagement.Manifest;

        Console.WriteLine();
        Console.WriteLine("Engagement complete.");
        Console.WriteLine();
        Console.WriteLine(
            $"  attack attempts   {stats.Tally.Total}  " +
            $"(success {stats.Tally.Success}, failure {stats.Tally.Failure}, blocked {stats.Tally.Blocked})");
        Console.WriteLine(
            $"  success rate      {(stats.Tally.SuccessRate is null ? "n/a" : $"{stats.Tally.SuccessRate * 100:0}%")} " +
            $"of {stats.Tally.Delivered} delivered");
        Console.WriteLine(
            $"  control tests     {manifest.ControlTestsBlocked}/{manifest.ControlTestsRun} blocked pre-delivery");
        Console.WriteLine($"  lures screened    {gate.Screened} (gate blocked {gate.Blocked})");

        if (manifest.JudgeAgreement is { } agreement)
        {
            Console.WriteLine(
                $"  judge agreement   {agreement.Agreements}/{agreement.ComparableAttempts} " +
                $"({agreement.AgreementRate * 100:0}%) vs the persona model");
        }

        if (manifest.LogFieldsRedacted > 0)
        {
            Console.WriteLine($"  fields redacted   {manifest.LogFieldsRedacted} before write");
        }

        if (manifest.ModelCalls > 0)
        {
            Console.WriteLine(
                $"  model usage       {manifest.ModelCalls} calls, {manifest.ModelTokens} tokens, " +
                $"{manifest.ThrottleWaitSeconds:0.0}s throttled");
        }

        if (manifest.Errors.Count > 0)
        {
            Console.WriteLine($"  failed attempts   {manifest.Errors.Count} (see run manifest; not logged as outcomes)");
        }

        Console.WriteLine();
        Console.WriteLine($"  log        {engagement.LogPath}");
        Console.WriteLine($"  manifest   {engagement.ManifestPath}");
        Console.WriteLine($"  reports    {reports.OrgSummaryPath}");
        Console.WriteLine($"             + {reports.EmployeeReportPaths.Count} individual reports");
        Console.WriteLine();

        EmployeeStats? top = stats.SusceptibilityRanking.FirstOrDefault(e => e.Tally.Success > 0);
        if (top is not null)
        {
            Console.WriteLine(
                $"  most susceptible  {top.Employee.Name} ({top.Employee.Role}) — " +
                $"{top.Tally.Success}/{top.Tally.Delivered} landed");
        }

        SliceStats? topPretext = stats.ByPretext.FirstOrDefault(s => s.Tally.Success > 0);
        if (topPretext is not null)
        {
            Console.WriteLine(
                $"  top pretext       {topPretext.Key} — {topPretext.Tally.Success}/{topPretext.Tally.Delivered} landed");
        }
    }

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..(length - 1)] + "…";
}
