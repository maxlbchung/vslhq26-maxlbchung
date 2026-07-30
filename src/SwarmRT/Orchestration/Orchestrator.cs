using System.Text.Json.Serialization;
using SwarmRT.Agents;
using SwarmRT.Contracts;
using SwarmRT.Logging;
using SwarmRT.Model;
using SwarmRT.Org;
using SwarmRT.Responders;
using SwarmRT.Safety;

namespace SwarmRT.Orchestration;

/// <summary>An attempt that could not produce a result object, usually a backend failure.</summary>
public sealed record AttemptError
{
    [JsonPropertyName("attempt_id")]
    public required string AttemptId { get; init; }

    [JsonPropertyName("target_employee_id")]
    public required string TargetEmployeeId { get; init; }

    [JsonPropertyName("pretext_type")]
    public required string PretextType { get; init; }

    [JsonPropertyName("error")]
    public required string Error { get; init; }
}

/// <summary>
/// Run metadata written beside the log as <c>{engagement_id}.run.json</c>.
/// <para>
/// This exists so the JSONL log can stay exactly what design §5.3 specifies — result
/// objects, verbatim, nothing else. Anything the run needs to record about itself lives
/// here instead: which engine actually produced the engagement, the log's hash chain,
/// and any attempt that failed outright rather than producing an outcome.
/// </para>
/// </summary>
public sealed record EngagementManifest
{
    [JsonPropertyName("engagement_id")]
    public required string EngagementId { get; init; }

    [JsonPropertyName("synthetic_org")]
    public required string SyntheticOrg { get; init; }

    [JsonPropertyName("started_at")]
    public required string StartedAt { get; init; }

    [JsonPropertyName("finished_at")]
    public required string FinishedAt { get; init; }

    [JsonPropertyName("seed")]
    public required int Seed { get; init; }

    [JsonPropertyName("engine")]
    public required EngineDescription Engine { get; init; }

    [JsonPropertyName("attempts_planned")]
    public required int AttemptsPlanned { get; init; }

    [JsonPropertyName("attempts_logged")]
    public required int AttemptsLogged { get; init; }

    [JsonPropertyName("outcome_tally")]
    public required IReadOnlyDictionary<string, int> OutcomeTally { get; init; }

    [JsonPropertyName("control_tests_run")]
    public required int ControlTestsRun { get; init; }

    [JsonPropertyName("control_tests_blocked")]
    public required int ControlTestsBlocked { get; init; }

    /// <summary>Agreement between the agent's judgment and the persona model's own decision.</summary>
    [JsonPropertyName("judge_agreement")]
    public JudgeAgreement? JudgeAgreement { get; init; }

    [JsonPropertyName("log_file")]
    public required string LogFile { get; init; }

    [JsonPropertyName("log_sha256_chain")]
    public required string LogSha256Chain { get; init; }

    [JsonPropertyName("model_calls")]
    public required int ModelCalls { get; init; }

    [JsonPropertyName("model_tokens")]
    public required int ModelTokens { get; init; }

    [JsonPropertyName("throttle_wait_seconds")]
    public required double ThrottleWaitSeconds { get; init; }

    [JsonPropertyName("log_fields_redacted")]
    public required int LogFieldsRedacted { get; init; }

    [JsonPropertyName("redaction_categories")]
    public required IReadOnlyList<string> RedactionCategories { get; init; }

    [JsonPropertyName("errors")]
    public required IReadOnlyList<AttemptError> Errors { get; init; }
}

/// <summary>Which implementation handled each stage, so no output is mistaken for model output.</summary>
public sealed record EngineDescription
{
    [JsonPropertyName("composer")]
    public required string Composer { get; init; }

    [JsonPropertyName("safety_gate")]
    public required string SafetyGate { get; init; }

    [JsonPropertyName("responder")]
    public required string Responder { get; init; }

    [JsonPropertyName("judge")]
    public required string Judge { get; init; }

    [JsonPropertyName("backend")]
    public required string Backend { get; init; }
}

public sealed record JudgeAgreement
{
    [JsonPropertyName("comparable_attempts")]
    public required int ComparableAttempts { get; init; }

    [JsonPropertyName("agreements")]
    public required int Agreements { get; init; }

    [JsonPropertyName("agreement_rate")]
    public required double AgreementRate { get; init; }
}

/// <summary>Everything a run produced, returned to the CLI for display.</summary>
public sealed record EngagementResult(
    EngagementManifest Manifest,
    IReadOnlyList<AttemptResult> Results,
    string LogPath,
    string ManifestPath);

public sealed record OrchestratorOptions
{
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// The engagement seed, recorded in the manifest so a run can be reproduced. Per-attempt
    /// seeds are derived from it, so this is the only value worth writing down.
    /// </summary>
    public int EngagementSeed { get; init; }

    /// <summary>
    /// Attempts in flight at once. Design §2 recommends 1 for the free tier; the throttle
    /// paces calls either way, but only a sequential run guarantees log order matches
    /// plan order.
    /// </summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>Abort the run on the first attempt that fails outright.</summary>
    public bool FailFast { get; init; }

    /// <summary>
    /// Optional pause between sequential attempts. The model engine paces itself through the
    /// throttle; this exists so the deterministic engine — which has nothing to wait on — can
    /// still be watched attempt-by-attempt on the live dashboard. Ignored when concurrency > 1.
    /// </summary>
    public TimeSpan DelayBetweenAttempts { get; init; }

    public Action<PlannedAttempt, AttemptResult>? OnAttemptCompleted { get; init; }

    public Action<PlannedAttempt, Exception>? OnAttemptFailed { get; init; }
}

/// <summary>
/// Design §3.1 — the single stateful component, and the only one that writes to disk.
/// <para>
/// For each planned attempt it clones a fresh agent, hands over the assignment, awaits the
/// one result object, and appends it to the log. Agents return data; the orchestrator
/// persists it. That split is what makes the log an audit trail rather than a
/// self-report: no agent can write, amend, or suppress its own row.
/// </para>
/// </summary>
public sealed class Orchestrator(
    AgentDefinition definition,
    ILureComposer controlComposer,
    LogTextSanitizer sanitizer,
    TimeProvider time)
{
    private readonly Lock _logLock = new();

    public async Task<EngagementResult> RunAsync(
        SyntheticOrg org,
        IReadOnlyList<PlannedAttempt> plan,
        OrchestratorOptions options,
        IModelClient? model = null,
        ModelThrottle? throttle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(org);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);

        if (plan.Count == 0)
        {
            throw new ArgumentException("Attempt plan is empty.", nameof(plan));
        }

        // Re-check the roster here rather than trusting the caller: this is the last point
        // before anything is "delivered" to a target (design §8.1).
        SyntheticDataGuard.EnsureSynthetic(org);

        string engagementId = plan[0].Assignment.EngagementId;
        DateTimeOffset startedAt = time.GetUtcNow();

        Directory.CreateDirectory(options.OutputDirectory);
        string logPath = Path.Combine(options.OutputDirectory, $"{engagementId}.jsonl");

        List<AttemptResult> results = [];
        List<AttemptError> errors = [];

        using JsonlAttemptLogger logger = new(logPath);

        if (options.Concurrency <= 1)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool ok = await ExecuteAndLogAsync(
                    plan[i], logger, results, errors, options, cancellationToken).ConfigureAwait(false);

                if (!ok && options.FailFast)
                {
                    break;
                }

                if (options.DelayBetweenAttempts > TimeSpan.Zero && i < plan.Count - 1)
                {
                    await Task.Delay(options.DelayBetweenAttempts, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else
        {
            await RunConcurrentlyAsync(
                plan, logger, results, errors, options, cancellationToken).ConfigureAwait(false);
        }

        EngagementManifest manifest = BuildManifest(
            org, plan, results, errors, logger, startedAt, options.EngagementSeed, model, throttle);

        string manifestPath = Path.Combine(options.OutputDirectory, $"{engagementId}.run.json");
        await File.WriteAllTextAsync(
            manifestPath,
            System.Text.Json.JsonSerializer.Serialize(manifest, SwarmJson.Pretty),
            cancellationToken).ConfigureAwait(false);

        return new EngagementResult(manifest, results, logger.Path, manifestPath);
    }

    private async Task RunConcurrentlyAsync(
        IReadOnlyList<PlannedAttempt> plan,
        JsonlAttemptLogger logger,
        List<AttemptResult> results,
        List<AttemptError> errors,
        OrchestratorOptions options,
        CancellationToken cancellationToken)
    {
        using SemaphoreSlim slots = new(options.Concurrency, options.Concurrency);

        IEnumerable<Task> running = plan.Select(async planned =>
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ExecuteAndLogAsync(planned, logger, results, errors, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                slots.Release();
            }
        });

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>
    /// Clones an agent, runs its one attempt, and appends the result. Returns false when
    /// the attempt failed outright.
    /// </summary>
    private async Task<bool> ExecuteAndLogAsync(
        PlannedAttempt planned,
        JsonlAttemptLogger logger,
        List<AttemptResult> results,
        List<AttemptError> errors,
        OrchestratorOptions options,
        CancellationToken cancellationToken)
    {
        // Control rows travel the same path as attacks but start from fixed known-bad text,
        // so the gate is exercised rather than described.
        AgentDefinition forThisAttempt = planned.IsControlTest
            ? definition with { Composer = controlComposer }
            : definition;

        AttemptResult result;

        // One clone, one attempt, then disposal — the wipe in design §2.
        using (EngineeringAgent agent = forThisAttempt.Spawn(planned.Assignment))
        {
            try
            {
                result = await agent.RunAsync(planned.Target, planned.Pretext, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AttemptError error = new()
                {
                    AttemptId = planned.Assignment.AttemptId,
                    TargetEmployeeId = planned.Assignment.TargetEmployeeId,
                    PretextType = planned.Assignment.PretextType,
                    Error = $"{ex.GetType().Name}: {ex.Message}",
                };

                lock (_logLock)
                {
                    errors.Add(error);
                }

                options.OnAttemptFailed?.Invoke(planned, ex);
                return false;
            }
        }

        // A failed attempt is never written as a fabricated outcome; only real results are
        // logged, and failures are recorded in the manifest instead.
        lock (_logLock)
        {
            logger.Append(result);
            results.Add(result);
        }

        options.OnAttemptCompleted?.Invoke(planned, result);
        return true;
    }

    private EngagementManifest BuildManifest(
        SyntheticOrg org,
        IReadOnlyList<PlannedAttempt> plan,
        IReadOnlyList<AttemptResult> results,
        IReadOnlyList<AttemptError> errors,
        JsonlAttemptLogger logger,
        DateTimeOffset startedAt,
        int engagementSeed,
        IModelClient? model,
        ModelThrottle? throttle)
    {
        Dictionary<string, int> tally = new(StringComparer.Ordinal)
        {
            ["success"] = results.Count(r => r.Outcome == AttemptOutcome.Success),
            ["failure"] = results.Count(r => r.Outcome == AttemptOutcome.Failure),
            ["blocked"] = results.Count(r => r.Outcome == AttemptOutcome.Blocked),
        };

        AttemptResult[] controlRows = results
            .Where(r => SafetyProbe.IsControlRow(r.PretextType))
            .ToArray();

        return new EngagementManifest
        {
            EngagementId = plan[0].Assignment.EngagementId,
            SyntheticOrg = org.OrgName,
            StartedAt = AttemptResult.FormatTimestamp(startedAt),
            FinishedAt = AttemptResult.FormatTimestamp(time.GetUtcNow()),
            Seed = engagementSeed,
            Engine = new EngineDescription
            {
                Composer = definition.Composer.Description,
                SafetyGate = definition.Gate.Description,
                Responder = definition.Responder.Description,
                Judge = definition.Judge.Description,
                Backend = model?.Description ?? "none (deterministic engine)",
            },
            AttemptsPlanned = plan.Count,
            AttemptsLogged = results.Count,
            OutcomeTally = tally,
            ControlTestsRun = controlRows.Length,
            ControlTestsBlocked = controlRows.Count(r => r.Outcome == AttemptOutcome.Blocked),
            JudgeAgreement = MeasureJudgeAgreement(results),
            LogFile = Path.GetFileName(logger.Path),
            LogSha256Chain = logger.Digest,
            ModelCalls = model?.Usage.Calls ?? 0,
            ModelTokens = model?.Usage.TotalTokens ?? 0,
            ThrottleWaitSeconds = Math.Round(throttle?.TotalWait.TotalSeconds ?? 0, 1),
            LogFieldsRedacted = sanitizer.RedactionCount,
            RedactionCategories = sanitizer.Redactions.Distinct().OrderBy(c => c).ToArray(),
            Errors = errors.ToArray(),
        };
    }

    /// <summary>
    /// Compares each judged outcome against the persona model's own decision, where one
    /// exists. Only the rule-weighted responder keeps ground truth; a model persona has
    /// none, so this returns null in that mode rather than inventing a figure.
    /// </summary>
    private JudgeAgreement? MeasureJudgeAgreement(IReadOnlyList<AttemptResult> results)
    {
        if (definition.Responder is not RuleWeightedResponder rules)
        {
            return null;
        }

        int comparable = 0;
        int agreements = 0;

        foreach (AttemptResult result in results)
        {
            if (result.Outcome == AttemptOutcome.Blocked)
            {
                continue;
            }

            if (!rules.TryGetBehavior(result.AttemptId, out ReplyBehavior behavior))
            {
                continue;
            }

            comparable++;
            bool judgedFavorable = result.Outcome == AttemptOutcome.Success;
            if (judgedFavorable == SimulatedReply.IsFavorable(behavior))
            {
                agreements++;
            }
        }

        if (comparable == 0)
        {
            return null;
        }

        return new JudgeAgreement
        {
            ComparableAttempts = comparable,
            Agreements = agreements,
            AgreementRate = Math.Round((double)agreements / comparable, 3),
        };
    }
}
