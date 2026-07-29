using SwarmRT.Contracts;
using SwarmRT.Org;
using SwarmRT.Safety;

namespace SwarmRT.Orchestration;

/// <summary>An assignment paired with the resolved objects the orchestrator needs.</summary>
public sealed record PlannedAttempt(AgentAssignment Assignment, Employee Target, PretextType Pretext)
{
    public bool IsControlTest => SafetyProbe.IsControlRow(Assignment.PretextType);
}

/// <summary>
/// Builds the attempt plan design §3.1 loads: which pretext types to try against which
/// employees.
/// <para>
/// The plan is not a cross product. Coverage comes first — employees are visited
/// round-robin so every persona is exercised before anyone is retried — and within an
/// employee, pretexts whose synthetic exposure attributes match are preferred, the way a
/// real engagement would pick approaches that fit what is observable about a target. No
/// employee/pretext pair is ever planned twice, so a repeated pretext against one person
/// cannot inflate their susceptibility score.
/// </para>
/// <para>
/// Everything is derived from the engagement seed, so the same seed produces the same
/// plan.
/// </para>
/// </summary>
public static class AttemptPlanner
{
    /// <summary>
    /// Plans <paramref name="attemptCount"/> attack attempts, then appends one control
    /// test per safety-probe case when <paramref name="includeSafetyProbe"/> is set.
    /// </summary>
    public static IReadOnlyList<PlannedAttempt> Plan(
        SyntheticOrg org,
        string engagementId,
        int attemptCount,
        int seed,
        bool includeSafetyProbe = true)
    {
        ArgumentNullException.ThrowIfNull(org);
        ArgumentException.ThrowIfNullOrWhiteSpace(engagementId);
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptCount, 1);

        Random random = new(seed);

        // Visit employees in a seeded order so the plan is not alphabetical.
        List<Employee> rotation = Shuffle(org.Employees.ToList(), random);

        // Per employee, the pretexts still available, best fit first.
        Dictionary<string, Queue<PretextType>> available = rotation.ToDictionary(
            employee => employee.Id,
            employee => new Queue<PretextType>(RankPretexts(employee, random)),
            StringComparer.Ordinal);

        List<(Employee Target, PretextType Pretext)> selections = [];
        int rotationIndex = 0;
        int exhausted = 0;

        while (selections.Count < attemptCount && exhausted < rotation.Count)
        {
            Employee employee = rotation[rotationIndex % rotation.Count];
            rotationIndex++;

            Queue<PretextType> queue = available[employee.Id];
            if (queue.Count == 0)
            {
                exhausted++;
                continue;
            }

            exhausted = 0;
            selections.Add((employee, queue.Dequeue()));
        }

        List<PlannedAttempt> plan = [];
        int attemptNumber = 0;

        foreach ((Employee target, PretextType pretext) in selections)
        {
            attemptNumber++;
            string attemptId = FormatAttemptId(attemptNumber);
            string tactic = PretextCatalog.FormatTactic(
                pretext.TacticCombinations[random.Next(pretext.TacticCombinations.Count)]);

            plan.Add(new PlannedAttempt(
                new AgentAssignment
                {
                    EngagementId = engagementId,
                    AttemptId = attemptId,
                    TargetEmployeeId = target.Id,
                    PretextType = pretext.Id,
                    Tactic = tactic,
                    Seed = DeriveSeed(seed, attemptId),
                },
                target,
                pretext));
        }

        if (includeSafetyProbe)
        {
            plan.AddRange(PlanControlTests(org, engagementId, seed, attemptNumber));
        }

        return plan;
    }

    /// <summary>
    /// Appends the gate-validation rows. Targets rotate across the roster so no single
    /// individual's report is skewed, though per-employee reports exclude control rows
    /// entirely.
    /// </summary>
    private static IEnumerable<PlannedAttempt> PlanControlTests(
        SyntheticOrg org, string engagementId, int seed, int startingNumber)
    {
        int attemptNumber = startingNumber;
        int index = 0;

        foreach (SafetyProbeCase probe in SafetyProbe.Cases)
        {
            attemptNumber++;
            string attemptId = FormatAttemptId(attemptNumber);
            Employee target = org.Employees[index % org.Employees.Count];
            index++;

            yield return new PlannedAttempt(
                new AgentAssignment
                {
                    EngagementId = engagementId,
                    AttemptId = attemptId,
                    TargetEmployeeId = target.Id,
                    PretextType = SafetyProbe.PretextId,

                    // The control composer reads the case id from here.
                    Tactic = probe.Id,
                    Seed = DeriveSeed(seed, attemptId),
                },
                target,
                SafetyProbe.ControlPretext);
        }
    }

    /// <summary>
    /// Orders the catalog for one employee: pretexts with matching exposure attributes
    /// first, ties broken by the seed so runs vary without becoming unpredictable.
    /// </summary>
    private static IEnumerable<PretextType> RankPretexts(Employee employee, Random random)
    {
        return PretextCatalog.All
            .Select(pretext => new
            {
                Pretext = pretext,
                Matches = pretext.ExposureTriggers.Count(employee.HasExposure),
                Tiebreak = random.Next(),
            })
            .OrderByDescending(x => x.Matches)
            .ThenBy(x => x.Tiebreak)
            .Select(x => x.Pretext)
            .ToList();
    }

    /// <summary>The largest plan the roster supports before pairs would have to repeat.</summary>
    public static int MaximumAttempts(SyntheticOrg org) =>
        org.Employees.Count * PretextCatalog.All.Count;

    public static string FormatAttemptId(int number) => $"att-{number:0000}";

    private static List<T> Shuffle<T>(List<T> items, Random random)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }

        return items;
    }

    /// <summary>
    /// Per-attempt seed. Mixed with FNV-1a rather than <see cref="HashCode"/>, whose
    /// output is randomised per process and would break reproducibility across runs.
    /// </summary>
    public static int DeriveSeed(int engagementSeed, string attemptId)
    {
        unchecked
        {
            int hash = engagementSeed ^ unchecked((int)2166136261);
            foreach (char c in attemptId)
            {
                hash = (hash ^ c) * 16777619;
            }

            return hash & 0x7FFFFFFF;
        }
    }
}
