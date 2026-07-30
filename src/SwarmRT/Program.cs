using SwarmRT.Cli;
using SwarmRT.Model;
using SwarmRT.Org;

namespace SwarmRT;

/// <summary>
/// Entry point for the SwarmRT console orchestrator.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cancellation = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Console.Error.WriteLine();
            Console.Error.WriteLine("Cancelling after the current attempt; the log keeps what completed.");
            cancellation.Cancel();
        };

        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        string[] rest = args.Length > 1 ? args[1..] : [];

        try
        {
            switch (verb)
            {
                case "run":
                    return await RunCommand.ExecuteAsync(new Arguments(rest), cancellation.Token)
                        .ConfigureAwait(false);

                case "simple":
                    return await SimpleCommand.ExecuteAsync(new Arguments(rest), cancellation.Token)
                        .ConfigureAwait(false);

                case "report":
                    return await ReportCommand.ExecuteAsync(new Arguments(rest), cancellation.Token)
                        .ConfigureAwait(false);

                case "view":
                    return await ViewCommand.ExecuteAsync(new Arguments(rest), cancellation.Token)
                        .ConfigureAwait(false);

                case "plan":
                    return await PlanCommand.ExecuteAsync(new Arguments(rest), cancellation.Token)
                        .ConfigureAwait(false);

                case "help" or "--help" or "-h" or "/?":
                    PrintUsage();
                    return 0;

                default:
                    Console.Error.WriteLine($"error: unknown command '{verb}'.");
                    Console.Error.WriteLine();
                    PrintUsage();
                    return 2;
            }
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }
        catch (NonSyntheticTargetException ex)
        {
            // The guardrail fired. Report it prominently: this is the tool working, not a bug.
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or ModelCallException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            SwarmRT — stateless agent-swarm social-engineering simulator

            A defensive security-awareness tool. An orchestrator dispatches a swarm of
            stateless agents; each makes one simulated attempt against a fabricated employee
            at a synthetic company, then is discarded. Every attempt is logged and the log
            becomes an awareness report. Nothing is ever delivered to a real person.

            USAGE
              swarmrt simple  [options]      Single-item exercise: can agents talk one support
                                             inbox out of one code? Logs and reports the result
              swarmrt run     [options]      Run a full multi-pretext engagement
              swarmrt report  --log <path>   Rebuild reports from an existing log
              swarmrt view    --log <path>   Replay a recorded run in the browser dashboard
              swarmrt plan    [options]      Print the attempt plan without executing it
              swarmrt help                   Show this message

            SIMPLE OPTIONS
              --attempts <n>          Attempts to run (default: 12)
              --out <dir>             Output directory (default: ./out)
              --run-id <id>           Run id used for the output filenames
              --seed <n>              Seed for intel assignment (default: 20260729)
              --history <n>           Earlier attempts each new agent is shown (default: 6)
              --no-transcript         Don't write the full emails and replies to disk
              --model / --endpoint / --key-env / --rpm / --quiet   As for run

            RUN OPTIONS
              --org <path>            Synthetic roster JSON (default: bundled Northwind Traders)
              --engagement-id <id>    Engagement id (default: <org short code>-<yyyy-MM>)
              --attempts <n>          Attack attempts to plan (default: 24)
              --target <employee-id>  Aim every attempt at one persona (single-victim stress
                                      test). Reuses pretexts freely and ignores the unique-pair
                                      cap; run `plan` first to see the employee ids
              --out <dir>             Output directory (default: ./out)
              --seed <n>              Seed for plan and persona jitter (default: 20260729)
              --overwrite             Replace an existing log for this engagement id

              --narrative             Add a model-written summary paragraph to the org report

              --model <id>            Model id (default: openai/gpt-4o-mini)
              --endpoint <url>        OpenAI-compatible endpoint (default: GitHub Models)
              --key-env <var>         Environment variable holding the API key
              --rpm <n>               Request pacing, requests per minute (default: 10)
              --concurrency <n>       Attempts in flight (default: 1; see the rate-limit note)

              --safety-probe          Add in-band content-safety control tests that prove the
                                      gate blocks known-bad inputs (off by default)
              --no-safety-self-check  Skip the model self-check gate; keep only the
                                      deterministic safety heuristics
              --fail-fast             Stop at the first attempt that errors
              --quiet                 Suppress per-attempt output
              --dashboard             Serve a live browser dashboard and open it; the run
                                      stays up afterwards so the report can be browsed (Ctrl+C)
              --port <n>              Dashboard port (default: 8760)
              --pace <ms>             Extra pause between attempts to watch a run more slowly
                                      (default: 0; the model backend already paces via --rpm)

            REPORT OPTIONS
              --log <path>            Attempt log to report on (required)
              --org <path>            Roster the engagement ran against
              --out <dir>             Report directory (default: <log dir>/reports)
              --ignore-digest         Report even if the log no longer matches its manifest

            VIEW OPTIONS
              --log <path>            Recorded attempt log to replay (required; its
                                      <id>.run.json manifest must sit beside it)
              --org <path>            Roster the engagement ran against
              --port <n>              Dashboard port (default: 8760)

            BACKEND
              A model backend is required: the swarm and the target persona are both model
              agents. Set a GitHub PAT with Models access in GITHUB_TOKEN (or SWARMRT_API_KEY,
              GITHUB_MODELS_TOKEN, GH_TOKEN). A run with no key configured is an error.

            EXIT CODES
              0 success   1 run error   2 usage error   3 roster failed the synthetic-only check

            EXAMPLES
              swarmrt plan --attempts 12
              swarmrt run --attempts 24
              swarmrt run --target emp-002 --attempts 20 --narrative
              swarmrt report --log out/NWT-2026-07.jsonl
              swarmrt view --log out/emp-013-split.jsonl
            """);
    }
}
