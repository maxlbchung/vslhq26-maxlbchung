using System.Text.Json;
using SwarmRT.Contracts;
using SwarmRT.Logging;
using SwarmRT.Org;
using SwarmRT.Orchestration;
using SwarmRT.Reporting;

namespace SwarmRT.Cli;

/// <summary>
/// Regenerates reports from an existing JSONL log, without re-running the engagement.
/// <para>
/// This is what makes the log the single source of truth rather than a by-product: the
/// deliverable can be rebuilt from the audit trail alone at any time. When a run manifest
/// sits beside the log its recorded hash chain is verified first, so a report is never
/// built silently on top of an edited log.
/// </para>
/// </summary>
public static class ReportCommand
{
    public static async Task<int> ExecuteAsync(Arguments args, CancellationToken cancellationToken)
    {
        string logPath = Path.GetFullPath(args.Required("log"));
        string orgPath = OrgLoader.ResolveDefaultPath(args.String("org"));
        string? outOverride = args.String("out");
        bool ignoreDigest = args.Flag("ignore-digest");
        args.EnsureAllConsumed();

        SyntheticOrg org = OrgLoader.Load(orgPath);
        IReadOnlyList<AttemptResult> rows = AttemptLogReader.Read(logPath);

        if (rows.Count == 0)
        {
            Console.Error.WriteLine($"error: '{logPath}' contains no attempt rows.");
            return 1;
        }

        foreach (AttemptResult row in rows)
        {
            string[] problems = row.Validate().ToArray();
            if (problems.Length > 0)
            {
                Console.Error.WriteLine(
                    $"error: row '{row.AttemptId}' does not satisfy the result contract: " +
                    string.Join("; ", problems));
                return 1;
            }
        }

        EngagementManifest? manifest = TryLoadManifest(logPath, rows[0].EngagementId);

        if (manifest is not null && !ignoreDigest)
        {
            string actual = AttemptLogReader.ComputeDigest(logPath);
            if (!string.Equals(actual, manifest.LogSha256Chain, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    "error: the log's hash chain does not match the digest recorded in its run manifest.");
                Console.Error.WriteLine($"       manifest: {manifest.LogSha256Chain}");
                Console.Error.WriteLine($"       log:      {actual}");
                Console.Error.WriteLine(
                    "       The log has been modified since the run. Pass --ignore-digest to report anyway.");
                return 1;
            }

            Console.WriteLine("Log hash chain matches the run manifest.");
        }

        string outputDirectory = Path.GetFullPath(
            outOverride ?? Path.Combine(Path.GetDirectoryName(logPath) ?? ".", "reports"));

        EngagementStatistics stats = EngagementStatistics.From(rows, org);
        ReportOutputs reports = await new ReportGenerator()
            .GenerateAsync(stats, outputDirectory, manifest, narrative: null, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine(
            $"Rebuilt {reports.FileCount} report file(s) from {rows.Count} logged attempt(s).");
        Console.WriteLine($"  {reports.OrgSummaryPath}");
        Console.WriteLine($"  + {reports.EmployeeReportPaths.Count} individual reports in " +
                          $"{Path.Combine(outputDirectory, "employees")}");

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
