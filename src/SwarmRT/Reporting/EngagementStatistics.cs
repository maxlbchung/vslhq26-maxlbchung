using SwarmRT.Contracts;
using SwarmRT.Org;
using SwarmRT.Safety;

namespace SwarmRT.Reporting;

/// <summary>
/// How a target resisted, inferred from the failure reason the agent wrote. Escalation
/// and verification are the behaviours training aims to produce; disengagement is a
/// weaker form of safety because the same person may act next time.
/// </summary>
public enum ResistanceSignal
{
    Escalated,
    Verified,
    Disengaged,
    Unclassified,
}

/// <summary>Outcome counts for any slice of the log.</summary>
public sealed record OutcomeTally(int Success, int Failure, int Blocked)
{
    public int Total => Success + Failure + Blocked;

    /// <summary>Attempts that actually reached a target. Blocked lures never did.</summary>
    public int Delivered => Success + Failure;

    /// <summary>Successes as a share of delivered attempts, or null when nothing was delivered.</summary>
    public double? SuccessRate => Delivered == 0 ? null : (double)Success / Delivered;

    public static OutcomeTally From(IEnumerable<AttemptResult> results)
    {
        int success = 0, failure = 0, blocked = 0;
        foreach (AttemptResult r in results)
        {
            switch (r.Outcome)
            {
                case AttemptOutcome.Success: success++; break;
                case AttemptOutcome.Failure: failure++; break;
                case AttemptOutcome.Blocked: blocked++; break;
            }
        }

        return new OutcomeTally(success, failure, blocked);
    }
}

/// <summary>A pretext or tactic slice, with the tally and which targets it landed against.</summary>
public sealed record SliceStats(string Key, OutcomeTally Tally, IReadOnlyList<string> SucceededAgainst);

/// <summary>Everything the reports need about one employee.</summary>
public sealed record EmployeeStats(
    Employee Employee,
    IReadOnlyList<AttemptResult> Attempts,
    OutcomeTally Tally,
    IReadOnlyDictionary<ResistanceSignal, int> ResistanceSignals)
{
    public IEnumerable<AttemptResult> Successes =>
        Attempts.Where(a => a.Outcome == AttemptOutcome.Success);

    public IEnumerable<AttemptResult> Failures =>
        Attempts.Where(a => a.Outcome == AttemptOutcome.Failure);

    /// <summary>Individual tactic levers that appeared in successful attempts, most frequent first.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> WinningLevers => Successes
        .SelectMany(a => PretextCatalog.ParseTactic(a.Tactic))
        .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
        .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
        .OrderByDescending(p => p.Value)
        .ThenBy(p => p.Key, StringComparer.Ordinal)
        .ToArray();

    /// <summary>Levers that were tried and never landed.</summary>
    public IReadOnlyList<string> IneffectiveLevers
    {
        get
        {
            HashSet<string> winning = new(WinningLevers.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
            return Failures
                .SelectMany(a => PretextCatalog.ParseTactic(a.Tactic))
                .Where(t => !winning.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToArray();
        }
    }
}

/// <summary>
/// Design §3.7 — pure aggregation over the JSONL log. No model calls, no side effects, so
/// the same log always yields the same statistics and the reports can be regenerated from
/// the audit trail at any time.
/// </summary>
public sealed class EngagementStatistics
{
    private EngagementStatistics(
        SyntheticOrg org,
        IReadOnlyList<AttemptResult> allRows,
        IReadOnlyList<AttemptResult> attempts,
        IReadOnlyList<AttemptResult> controlRows,
        IReadOnlyList<EmployeeStats> employees,
        IReadOnlyList<SliceStats> byPretext,
        IReadOnlyList<SliceStats> byTacticCombination,
        IReadOnlyList<SliceStats> byLever)
    {
        Org = org;
        AllRows = allRows;
        Attempts = attempts;
        ControlRows = controlRows;
        Employees = employees;
        ByPretext = byPretext;
        ByTacticCombination = byTacticCombination;
        ByLever = byLever;
        Tally = OutcomeTally.From(attempts);
        ControlTally = OutcomeTally.From(controlRows);
    }

    public SyntheticOrg Org { get; }

    /// <summary>Every row in the log, control tests included.</summary>
    public IReadOnlyList<AttemptResult> AllRows { get; }

    /// <summary>Attack attempts only — control rows excluded, so they never skew susceptibility.</summary>
    public IReadOnlyList<AttemptResult> Attempts { get; }

    public IReadOnlyList<AttemptResult> ControlRows { get; }

    public OutcomeTally Tally { get; }

    public OutcomeTally ControlTally { get; }

    public IReadOnlyList<EmployeeStats> Employees { get; }

    public IReadOnlyList<SliceStats> ByPretext { get; }

    public IReadOnlyList<SliceStats> ByTacticCombination { get; }

    /// <summary>Breakdown by individual influence lever, split out of the tactic pairs.</summary>
    public IReadOnlyList<SliceStats> ByLever { get; }

    public string EngagementId => AllRows.Count > 0 ? AllRows[0].EngagementId : "(none)";

    public DateTimeOffset? WindowStart =>
        ParseTimestamps() is { Count: > 0 } stamps ? stamps.Min() : null;

    public DateTimeOffset? WindowEnd =>
        ParseTimestamps() is { Count: > 0 } stamps ? stamps.Max() : null;

    /// <summary>Employees the plan never reached, listed so coverage gaps are visible.</summary>
    public IReadOnlyList<Employee> UntestedEmployees => Org.Employees
        .Where(e => Employees.All(s => s.Employee.Id != e.Id))
        .ToArray();

    /// <summary>Employees ordered by susceptibility: success rate first, then raw successes.</summary>
    public IReadOnlyList<EmployeeStats> SusceptibilityRanking => Employees
        .OrderByDescending(s => s.Tally.SuccessRate ?? -1)
        .ThenByDescending(s => s.Tally.Success)
        .ThenBy(s => s.Employee.Id, StringComparer.Ordinal)
        .ToArray();

    public static EngagementStatistics From(IReadOnlyList<AttemptResult> rows, SyntheticOrg org)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(org);

        AttemptResult[] control = rows.Where(r => SafetyProbe.IsControlRow(r.PretextType)).ToArray();
        AttemptResult[] attempts = rows.Where(r => !SafetyProbe.IsControlRow(r.PretextType)).ToArray();

        List<EmployeeStats> employees = [];
        foreach (IGrouping<string, AttemptResult> group in attempts
                     .GroupBy(r => r.TargetEmployeeId, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Employee? employee = org.Find(group.Key);
            if (employee is null)
            {
                // A log row for an id the roster no longer contains: surface it rather than
                // silently dropping the attempt from the totals.
                throw new InvalidDataException(
                    $"Log references employee '{group.Key}', which is not in roster '{org.OrgName}'. " +
                    "Reports must be generated against the roster the engagement ran with.");
            }

            AttemptResult[] ordered = group
                .OrderBy(r => r.AttemptId, StringComparer.Ordinal)
                .ToArray();

            employees.Add(new EmployeeStats(
                employee,
                ordered,
                OutcomeTally.From(ordered),
                CountResistanceSignals(ordered)));
        }

        return new EngagementStatistics(
            org,
            rows,
            attempts,
            control,
            employees,
            SliceBy(attempts, r => [r.PretextType]),
            SliceBy(attempts, r => [r.Tactic]),
            SliceBy(attempts, r => PretextCatalog.ParseTactic(r.Tactic)));
    }

    /// <summary>
    /// Groups attempts by one or more keys per row, ordered by what landed most. A row can
    /// contribute to several slices, which is how a tactic pair feeds both of its levers.
    /// </summary>
    private static IReadOnlyList<SliceStats> SliceBy(
        IEnumerable<AttemptResult> attempts, Func<AttemptResult, IEnumerable<string>> keySelector)
    {
        Dictionary<string, List<AttemptResult>> buckets = new(StringComparer.OrdinalIgnoreCase);

        foreach (AttemptResult attempt in attempts)
        {
            foreach (string key in keySelector(attempt))
            {
                if (!buckets.TryGetValue(key, out List<AttemptResult>? bucket))
                {
                    bucket = [];
                    buckets[key] = bucket;
                }

                bucket.Add(attempt);
            }
        }

        return buckets
            .Select(pair => new SliceStats(
                pair.Key,
                OutcomeTally.From(pair.Value),
                pair.Value
                    .Where(r => r.Outcome == AttemptOutcome.Success)
                    .Select(r => r.TargetEmployeeId)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray()))
            .OrderByDescending(s => s.Tally.Success)
            .ThenByDescending(s => s.Tally.SuccessRate ?? -1)
            .ThenBy(s => s.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<ResistanceSignal, int> CountResistanceSignals(
        IEnumerable<AttemptResult> attempts)
    {
        Dictionary<ResistanceSignal, int> counts = [];

        foreach (AttemptResult attempt in attempts.Where(a => a.Outcome == AttemptOutcome.Failure))
        {
            ResistanceSignal signal = Classify(attempt.FailureReason);
            counts[signal] = counts.GetValueOrDefault(signal) + 1;
        }

        return counts;
    }

    /// <summary>
    /// Reads the failure reason to tell escalation from verification from simple
    /// disengagement. The agent is prompted to name the control it ran into, which is what
    /// makes this classifiable; anything unrecognised stays unclassified rather than being
    /// counted as a good outcome.
    /// </summary>
    public static ResistanceSignal Classify(string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(failureReason))
        {
            return ResistanceSignal.Unclassified;
        }

        string text = failureReason.ToLowerInvariant();

        if (Contains(text, "report", "escalat", "security team", "raised it", "flagged it", "notified security"))
        {
            return ResistanceSignal.Escalated;
        }

        if (Contains(text, "verif", "confirm", "known channel", "usual channel", "authoris", "authoriz",
                "ticket", "manager", "policy", "normal process", "questioned", "challenged", "pushed back",
                "refused", "declined"))
        {
            return ResistanceSignal.Verified;
        }

        if (Contains(text, "ignor", "did not engage", "no response", "unopened", "never engaged",
                "no evidence", "did not reply"))
        {
            return ResistanceSignal.Disengaged;
        }

        return ResistanceSignal.Unclassified;
    }

    private static bool Contains(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.Ordinal));

    private List<DateTimeOffset> ParseTimestamps() => AllRows
        .Select(r => DateTimeOffset.TryParse(r.Timestamp, out DateTimeOffset parsed)
            ? parsed
            : (DateTimeOffset?)null)
        .Where(t => t.HasValue)
        .Select(t => t!.Value)
        .ToList();
}
