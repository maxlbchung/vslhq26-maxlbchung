using System.Text.Json;
using SwarmRT.Contracts;
using SwarmRT.Logging;
using SwarmRT.Orchestration;
using SwarmRT.Org;
using SwarmRT.Reporting;
using SwarmRT.Safety;

namespace SwarmRT.Cli;

/// <summary>
/// Serves an already-recorded engagement in the live dashboard, without re-running it.
/// <para>
/// The plan is fully determined by the engagement seed (recorded in the run manifest), so it
/// is regenerated here to recover each attempt's target, pretext, and orchestrator rationale;
/// the logged <see cref="AttemptResult"/> supplies the outcome. Each pair is pushed through the
/// same <see cref="LiveDashboard.PublishAttempt"/> path a live run uses, so a replayed run and a
/// live one render identically. This is how you review a past run — like the one saved as
/// <c>out/&lt;id&gt;.jsonl</c> — in the browser.
/// </para>
/// </summary>
public static class ViewCommand
{
    public static async Task<int> ExecuteAsync(Arguments args, CancellationToken cancellationToken)
    {
        string logPath = Path.GetFullPath(args.Required("log"));
        string orgPath = OrgLoader.ResolveDefaultPath(args.String("org"));
        int port = args.Int("port", 8760, min: 1, max: 65535);
        args.EnsureAllConsumed();

        SyntheticOrg org = OrgLoader.Load(orgPath);
        IReadOnlyList<AttemptResult> rows = AttemptLogReader.Read(logPath);
        if (rows.Count == 0)
        {
            Console.Error.WriteLine($"error: '{logPath}' contains no attempt rows.");
            return 1;
        }

        string engagementId = rows[0].EngagementId;
        EngagementManifest? manifest = TryLoadManifest(logPath, engagementId);
        if (manifest is null)
        {
            Console.Error.WriteLine(
                $"error: no run manifest '{engagementId}.run.json' beside the log. `view` needs it to " +
                "reproduce the plan (targets, pretexts, rationale) from the recorded seed.");
            return 1;
        }

        // Rebuild the exact plan the run used. The plan is a pure function of the seed, so the
        // attempt ids line up with the logged rows and each attempt's assignment is recovered.
        bool includeProbe = rows.Any(r => SafetyProbe.IsControlRow(r.PretextType));
        AttemptResult[] attacks = rows.Where(r => !SafetyProbe.IsControlRow(r.PretextType)).ToArray();
        string[] targets = attacks.Select(r => r.TargetEmployeeId).Distinct(StringComparer.Ordinal).ToArray();
        string? target = targets.Length == 1 ? targets[0] : null;

        IReadOnlyList<PlannedAttempt> plan = AttemptPlanner.Plan(
            org, engagementId, Math.Max(attacks.Length, 1), manifest.Seed, includeProbe, target);
        Dictionary<string, PlannedAttempt> byId =
            plan.ToDictionary(p => p.Assignment.AttemptId, StringComparer.Ordinal);

        EngagementStatistics stats = EngagementStatistics.From(rows, org);

        // Reports back the "VIEW REPORT" view. Written to a temp dir so viewing never clobbers
        // the reports already sitting beside the log. No model needed (narrative: null).
        string reportDir = Path.Combine(Path.GetTempPath(), "swarmrt-view", engagementId);
        ReportOutputs reports = await new ReportGenerator()
            .GenerateAsync(stats, reportDir, manifest, narrative: null, cancellationToken)
            .ConfigureAwait(false);

        using LiveDashboard dash = new(port);
        dash.Start();

        int index = 0;
        int missing = 0;
        foreach (AttemptResult row in rows)
        {
            index++;
            if (byId.TryGetValue(row.AttemptId, out PlannedAttempt? planned))
            {
                dash.PublishAttempt(index, rows.Count, planned, row);
            }
            else
            {
                missing++;
            }
        }

        dash.Complete(reports.OrgSummaryPath, ReportSummary.From(stats, manifest));

        Console.WriteLine($"Replaying {engagementId} — {rows.Count} attempt(s) " +
                          $"(success {stats.Tally.Success}, failure {stats.Tally.Failure}, blocked {stats.Tally.Blocked}).");
        if (missing > 0)
        {
            Console.Error.WriteLine(
                $"note: {missing} logged row(s) had no matching planned attempt and were skipped — " +
                "the log seed may not match this build's planner.");
        }

        Console.WriteLine();
        Console.WriteLine($"  dashboard  {dash.Url}  (Ctrl+C to stop)");
        dash.OpenBrowser();

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    /// <summary>Loads <c>{engagement_id}.run.json</c> from beside the log, if it is there.</summary>
    private static EngagementManifest? TryLoadManifest(string logPath, string engagementId)
    {
        string? directory = Path.GetDirectoryName(logPath);
        if (directory is null)
        {
            return null;
        }

        string manifestPath = Path.Combine(directory, $"{engagementId}.run.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EngagementManifest>(
                File.ReadAllText(manifestPath), SwarmJson.Reading);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"note: ignoring unreadable run manifest '{manifestPath}': {ex.Message}");
            return null;
        }
    }
}
