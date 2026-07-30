using SwarmRT.Org;
using SwarmRT.Orchestration;

namespace SwarmRT.Cli;

/// <summary>
/// Prints the attempt plan without executing it. Useful for reviewing what an engagement
/// intends to do — and for confirming the roster passes the synthetic-only check — before
/// spending any model calls.
/// </summary>
public static class PlanCommand
{
    public static Task<int> ExecuteAsync(Arguments args, CancellationToken cancellationToken)
    {
        string orgPath = OrgLoader.ResolveDefaultPath(args.String("org"));
        int attempts = args.Int("attempts", 24, min: 1, max: 5000);
        int seed = args.Int("seed", 20260729);
        bool includeProbe = args.Flag("safety-probe", defaultValue: false);
        string? engagementIdOverride = args.String("engagement-id");
        string? target = args.String("target");
        args.EnsureAllConsumed();

        SyntheticOrg org = OrgLoader.Load(orgPath);
        string engagementId = engagementIdOverride
                              ?? $"{org.ResolveShortCode()}-{DateTimeOffset.UtcNow:yyyy-MM}";

        int maxUseful = AttemptPlanner.MaximumAttempts(org);
        if (target is null && attempts > maxUseful)
        {
            Console.Error.WriteLine(
                $"note: capping --attempts at {maxUseful} unique persona/pretext pairs.");
            attempts = maxUseful;
        }

        IReadOnlyList<PlannedAttempt> plan = AttemptPlanner.Plan(
            org, engagementId, attempts, seed, includeProbe, target);

        Console.WriteLine($"Roster '{org.OrgName}' passed the synthetic-only check ({org.Domain}).");
        Console.WriteLine($"Plan for engagement {engagementId}, seed {seed}:");
        Console.WriteLine();
        Console.WriteLine($"  {"attempt",-10} {"target",-20} {"pretext",-32} tactic");
        Console.WriteLine($"  {new string('-', 10)} {new string('-', 20)} {new string('-', 32)} ------");

        foreach (PlannedAttempt planned in plan)
        {
            string pretext = planned.IsControlTest
                ? "[control] " + planned.Assignment.PretextType
                : planned.Assignment.PretextType;

            Console.WriteLine(
                $"  {planned.Assignment.AttemptId,-10} {Truncate(planned.Target.Name, 20),-20} " +
                $"{Truncate(pretext, 32),-32} {planned.Assignment.Tactic}");
        }

        int attackAttempts = plan.Count(p => !p.IsControlTest);
        Console.WriteLine();
        Console.WriteLine(
            $"{attackAttempts} attack attempts across {plan.Where(p => !p.IsControlTest).Select(p => p.Target.Id).Distinct().Count()} " +
            $"personas, plus {plan.Count - attackAttempts} safety control tests. Nothing was executed.");

        return Task.FromResult(0);
    }

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..(length - 1)] + "…";
}
