using SwarmRT.Model;
using SwarmRT.Simple;

namespace SwarmRT.Cli;

/// <summary>
/// Runs the single-item exercise: agents take turns trying to talk one support inbox out
/// of one code, and the orchestrator logs and reports what happened.
/// <para>
/// Unlike <c>run</c>, this verb has no deterministic fallback. Both sides of the exchange
/// are model calls — a scripted version of either would only be testing the script.
/// </para>
/// </summary>
public static class SimpleCommand
{
    public static async Task<int> ExecuteAsync(Arguments args, CancellationToken cancellationToken)
    {
        string outputDirectory = Path.GetFullPath(args.String("out", "out")!);
        int attempts = args.Int("attempts", 12, min: 1, max: 500);
        int seed = args.Int("seed", 20260729);
        int historyDepth = args.Int("history", 6, min: 0, max: 50);
        int requestsPerMinute = args.Int("rpm", 10, min: 1, max: 600);
        bool transcript = args.Flag("transcript", defaultValue: true);
        bool quiet = args.Flag("quiet");
        string? endpoint = args.String("endpoint");
        string? modelId = args.String("model");
        string? keyVariable = args.String("key-env");
        string runId = args.String("run-id", $"simple-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}")!;
        bool printPrompts = args.Flag("print-prompts");

        args.EnsureAllConsumed();

        // The prompts are the whole experiment, so they are inspectable without spending a
        // single call. Tune wording here, then run.
        if (printPrompts)
        {
            Console.WriteLine("=== SUPPORT INBOX (the target) ===");
            Console.WriteLine();
            Console.WriteLine(Scenario.SupportSystemPrompt);
            Console.WriteLine();
            Console.WriteLine("=== SOCIAL ENGINEER (the swarm) ===");
            Console.WriteLine();
            Console.WriteLine(Scenario.EngineerSystemPrompt);
            return 0;
        }

        ModelBackendOptions backendOptions = new()
        {
            Endpoint = endpoint ?? ModelBackendOptions.GitHubModelsEndpoint,
            Model = modelId ?? ModelBackendOptions.DefaultModel,
            KeyVariable = keyVariable,
            RequestsPerMinute = requestsPerMinute,
            MaxConcurrency = 1,
            DisplayName = endpoint is null ? "GitHub Models" : "OpenAI-compatible endpoint",
        };

        (string? apiKey, string variablesTried) = backendOptions.ResolveKey();
        if (apiKey is null)
        {
            Console.Error.WriteLine($"error: this exercise needs a model backend. Set one of: {variablesTried}.");
            Console.Error.WriteLine("       For GitHub Models, create a PAT with Models access and set GITHUB_TOKEN.");
            return 2;
        }

        using ModelThrottle throttle = new(requestsPerMinute, maxConcurrency: 1);
        using OpenAiCompatibleModelClient model = new(backendOptions, apiKey, throttle);

        if (!quiet)
        {
            Console.WriteLine($"{Scenario.Company} — support inbox exercise");
            Console.WriteLine($"Item under test: VPN enrollment code · {attempts} attempts · {model.Description}");
            Console.WriteLine();
        }

        SimpleOrchestrator orchestrator = new(model);

        SimpleRunResult result = await orchestrator.RunAsync(
            new SimpleRunOptions
            {
                OutputDirectory = outputDirectory,
                RunId = runId,
                Attempts = attempts,
                Seed = seed,
                HistoryDepth = historyDepth,
                KeepTranscript = transcript,
                OnAttempt = quiet ? null : Trace,
                OnError = (n, ex) =>
                    Console.Error.WriteLine($"  attempt {n} errored, skipping: {ex.Message}"),
            },
            cancellationToken).ConfigureAwait(false);

        int leaked = result.Attempts.Count(a => a.Disclosed);

        Console.WriteLine();
        Console.WriteLine($"Code released in {leaked} of {result.Attempts.Count} attempts.");
        Console.WriteLine($"  log:    {result.LogPath}");
        if (result.TranscriptPath is not null)
        {
            Console.WriteLine($"  emails: {result.TranscriptPath}");
        }

        Console.WriteLine($"  report: {result.ReportPath}");

        return 0;
    }

    private static void Trace(SimpleAttempt attempt, SimulatedEmail email, string reply)
    {
        string verdict = attempt.Disclosed ? "CODE RELEASED" : "refused";
        string knew = attempt.NamedEmployee ?? "—";

        Console.WriteLine(
            $"[{attempt.Attempt,3}] {knew,-18} {Truncate(attempt.Approach, 42),-42} → {verdict}");
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..(limit - 1)] + "…";
}
