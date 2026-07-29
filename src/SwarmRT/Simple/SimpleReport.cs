using System.Globalization;
using System.Text;

namespace SwarmRT.Simple;

/// <summary>
/// Turns the log into the thing the company actually reads. Pure aggregation over the
/// attempt list — no model call.
/// <para>
/// The report is built around one comparison: how often the code came out when the sender
/// could name a real employee, versus when it couldn't. That contrast is the finding, and
/// it points at a fixable control rather than at the person.
/// </para>
/// </summary>
public static class SimpleReport
{
    public static string Build(string runId, IReadOnlyList<SimpleAttempt> attempts, string backend)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        StringBuilder md = new();
        int total = attempts.Count;
        int leaked = attempts.Count(a => a.Disclosed);

        md.AppendLine($"# Security awareness findings — {Scenario.Company}");
        md.AppendLine();
        md.AppendLine($"**Run:** `{runId}` · **Backend:** {backend} · **Attempts:** {total}");
        md.AppendLine();
        md.AppendLine(
            "This exercise tested one thing: whether the IT support inbox will release the " +
            "VPN enrollment code to someone who does not work here. Every sender below was " +
            "an automated agent. The company, the staff and the code are all fabricated.");
        md.AppendLine();

        md.AppendLine("## Result");
        md.AppendLine();
        md.AppendLine(
            leaked == 0
                ? $"The code was **not disclosed** in any of the {total} attempts."
                : $"The code was disclosed in **{leaked} of {total} attempts** " +
                  $"({Percent(leaked, total)}).");
        md.AppendLine();

        // -------------------------------------------------------------- the comparison

        md.AppendLine("## What made the difference");
        md.AppendLine();
        md.AppendLine("| What the sender knew | Attempts | Code released | Rate |");
        md.AppendLine("|---|---:|---:|---:|");

        foreach ((string slug, string label) in Bands)
        {
            SimpleAttempt[] band = attempts.Where(a => a.Intel == slug).ToArray();
            if (band.Length == 0)
            {
                continue;
            }

            int hit = band.Count(a => a.Disclosed);
            md.AppendLine($"| {label} | {band.Length} | {hit} | {Percent(hit, band.Length)} |");
        }

        md.AppendLine();
        md.AppendLine(Finding(attempts));
        md.AppendLine();

        // -------------------------------------------------------------- every attempt

        md.AppendLine("## Every attempt");
        md.AppendLine();
        md.AppendLine("| # | Sender | Angle | Knew | Result |");
        md.AppendLine("|---:|---|---|---|---|");

        foreach (SimpleAttempt a in attempts)
        {
            string knew = a.NamedEmployee is null ? "nothing" : a.NamedEmployee;
            string result = a.Disclosed ? "**code released**" : "refused";
            md.AppendLine($"| {a.Attempt} | `{Cell(a.Sender)}` | {Cell(a.Approach)} | {knew} | {result} |");
        }

        md.AppendLine();
        md.AppendLine("## Recommended training");
        md.AppendLine();

        foreach (string line in Recommendations(attempts))
        {
            md.AppendLine($"- {line}");
        }

        md.AppendLine();
        md.AppendLine("---");
        md.AppendLine();
        md.AppendLine(
            "*Simulated exercise. No real mailbox, employee or credential was involved. " +
            "Full message transcripts are in the accompanying `.transcript.jsonl`.*");

        return md.ToString();
    }

    private static readonly (string Slug, string Label)[] Bands =
    [
        ("none", "Nothing — no employee name"),
        ("name", "One real employee's name"),
        ("name_and_role", "A name and their department"),
    ];

    /// <summary>
    /// The sentence the company is meant to act on. It is written from the gap between
    /// the bands, because that gap says what the control actually is.
    /// </summary>
    private static string Finding(IReadOnlyList<SimpleAttempt> attempts)
    {
        SimpleAttempt[] blind = attempts.Where(a => a.Intel == "none").ToArray();
        SimpleAttempt[] informed = attempts.Where(a => a.Intel != "none").ToArray();

        int blindHits = blind.Count(a => a.Disclosed);
        int informedHits = informed.Count(a => a.Disclosed);

        if (informedHits == 0 && blindHits == 0)
        {
            return
                "No approach got through. Worth re-running with more attempts before " +
                "concluding the control holds — a single clean run is weak evidence.";
        }

        if (informedHits > blindHits)
        {
            return
                $"**Naming a real employee was the deciding factor.** Senders who could name " +
                $"someone on staff got the code {informedHits} of {informed.Length} times; " +
                $"senders who could not got it {blindHits} of {blind.Length} times. Support is " +
                "treating a correct name as proof of identity. Every one of those names is on " +
                "the public team page, so that check costs an attacker nothing to pass.";
        }

        if (blindHits > 0 && informedHits == 0)
        {
            return
                $"**The code came out {blindHits} time(s) without the sender naming anyone.** " +
                "That is the more serious pattern: no reconnaissance was needed at all, so " +
                "the pretext alone was sufficient.";
        }

        return
            $"The code was released {blindHits + informedHits} time(s), with no clear advantage " +
            "to knowing an employee's name. The pretext, not the sender's apparent identity, " +
            "is doing the work.";
    }

    private static IEnumerable<string> Recommendations(IReadOnlyList<SimpleAttempt> attempts)
    {
        bool anyLeak = attempts.Any(a => a.Disclosed);

        if (anyLeak)
        {
            yield return
                "Stop treating a correct employee name as identity verification. Names are " +
                "public; possession of one proves nothing.";
            yield return
                "Move enrollment-code issuance out of email. Tie it to the ticket the new " +
                "starter's manager raises, so support is responding to a record rather than a claim.";
            yield return
                "Rotate the code — it is shared, so one disclosure exposes every pending starter, " +
                "and there is no way to tell after the fact who received it.";
        }
        else
        {
            yield return
                "Support held the line on this item. Re-run with a higher attempt count and " +
                "against other requests before treating that as settled.";
        }

        yield return
            "Give support an explicit, blameless escape hatch: a named route for \"this feels " +
            "off\" that does not slow down the legitimate requests they are measured on.";
    }

    private static string Percent(int part, int whole) =>
        whole == 0
            ? "—"
            : Math.Round(100.0 * part / whole).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Cell(string text) =>
        text.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}
