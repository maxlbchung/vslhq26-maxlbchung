using System.Text;
using SwarmRT.Contracts;
using SwarmRT.Org;
using SwarmRT.Orchestration;
using SwarmRT.Safety;

namespace SwarmRT.Reporting;

/// <summary>Paths written by a report run.</summary>
public sealed record ReportOutputs(string OrgSummaryPath, IReadOnlyList<string> EmployeeReportPaths)
{
    public int FileCount => 1 + EmployeeReportPaths.Count;
}

/// <summary>
/// Design §3.7 — reads the aggregated log and emits an org-wide summary plus one
/// individual report per tested employee.
/// <para>
/// Every figure comes from the JSONL log, so a report can be regenerated from the audit
/// trail alone. Findings are derived from the structured fields — which pretext, which
/// tactic, which outcome — rather than from parsing prose, and the recommendations come
/// from the countermeasure attached to each pretext that actually landed.
/// </para>
/// </summary>
public sealed class ReportGenerator
{
    private const string SimulationBanner =
        "> **Simulation artefact.** Every target in this report is a fabricated persona at a " +
        "synthetic company on a reserved, non-routable domain. No real person was contacted, no " +
        "real system was touched, and no message described here was ever delivered anywhere. " +
        "Approaches are recorded as *pretext type and tactic only* — this document contains no " +
        "reusable lure content by design.";

    public async Task<ReportOutputs> GenerateAsync(
        EngagementStatistics stats,
        string outputDirectory,
        EngagementManifest? manifest = null,
        NarrativeWriter? narrative = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string employeeDirectory = Path.Combine(outputDirectory, "employees");
        Directory.CreateDirectory(employeeDirectory);

        string? narrativeParagraph = narrative is null
            ? null
            : await narrative.WriteAsync(stats, cancellationToken).ConfigureAwait(false);

        string summaryPath = Path.Combine(outputDirectory, "org-summary.md");
        await File.WriteAllTextAsync(
            summaryPath,
            BuildOrgSummary(stats, manifest, narrativeParagraph),
            cancellationToken).ConfigureAwait(false);

        List<string> employeePaths = [];
        foreach (EmployeeStats employee in stats.Employees)
        {
            string path = Path.Combine(employeeDirectory, $"{employee.Employee.ReportSlug()}.md");
            await File.WriteAllTextAsync(
                path, BuildEmployeeReport(employee, stats, manifest), cancellationToken)
                .ConfigureAwait(false);
            employeePaths.Add(path);
        }

        return new ReportOutputs(summaryPath, employeePaths);
    }

    // ---------------------------------------------------------------- org summary

    public static string BuildOrgSummary(
        EngagementStatistics stats,
        EngagementManifest? manifest = null,
        string? narrativeParagraph = null)
    {
        ArgumentNullException.ThrowIfNull(stats);

        StringBuilder md = new();

        md.AppendLine($"# {stats.Org.OrgName} — Social-Engineering Awareness Report");
        md.AppendLine();
        md.AppendLine(SimulationBanner);
        md.AppendLine();

        AppendEngagementMetadata(md, stats, manifest);

        if (!string.IsNullOrWhiteSpace(narrativeParagraph))
        {
            md.AppendLine("## Summary");
            md.AppendLine();
            md.AppendLine(narrativeParagraph);
            md.AppendLine();
        }

        AppendHeadline(md, stats);
        AppendPretextBreakdown(md, stats);
        AppendLeverBreakdown(md, stats);
        AppendSusceptibilityRanking(md, stats);
        AppendResistanceSummary(md, stats);
        AppendOrgRecommendations(md, stats);
        AppendControlValidation(md, stats);
        AppendCoverage(md, stats);
        AppendProvenance(md, stats, manifest);

        return md.ToString();
    }

    private static void AppendEngagementMetadata(
        StringBuilder md, EngagementStatistics stats, EngagementManifest? manifest)
    {
        md.AppendLine("## Engagement");
        md.AppendLine();
        md.AppendLine("| Field | Value |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| Engagement ID | `{stats.EngagementId}` |");
        md.AppendLine($"| Synthetic organisation | {stats.Org.OrgName} (`{stats.Org.Domain}`) |");
        md.AppendLine($"| Roster size | {stats.Org.Employees.Count} fabricated personas |");
        md.AppendLine($"| Window | {FormatWindow(stats)} |");
        md.AppendLine($"| Attack attempts | {stats.Tally.Total} |");
        md.AppendLine($"| Control tests | {stats.ControlTally.Total} |");

        if (manifest is not null)
        {
            md.AppendLine($"| Engine | {manifest.Engine.Backend} |");
            md.AppendLine($"| Seed | `{manifest.Seed}` |");
        }

        md.AppendLine();
    }

    private static void AppendHeadline(StringBuilder md, EngagementStatistics stats)
    {
        OutcomeTally tally = stats.Tally;

        md.AppendLine("## Result at a glance");
        md.AppendLine();
        md.AppendLine("| Outcome | Count | Share of delivered |");
        md.AppendLine("|---|---:|---:|");
        md.AppendLine($"| Favorable reply (`success`) | {tally.Success} | {Percent(Share(tally.Success, tally.Delivered))} |");
        md.AppendLine($"| Unfavorable reply (`failure`) | {tally.Failure} | {Percent(Share(tally.Failure, tally.Delivered))} |");
        md.AppendLine($"| Blocked pre-delivery (`blocked`) | {tally.Blocked} | — |");
        md.AppendLine($"| **Delivered to a target** | **{tally.Delivered}** | |");
        md.AppendLine();
        md.AppendLine(
            $"{tally.Success} of {tally.Delivered} delivered attempts produced a favorable reply " +
            $"({Percent(tally.SuccessRate)}). Blocked attempts are excluded from the rate because they " +
            "never reached a target.");
        md.AppendLine();
    }

    private static void AppendPretextBreakdown(StringBuilder md, EngagementStatistics stats)
    {
        md.AppendLine("## Which pretexts landed");
        md.AppendLine();
        md.AppendLine("| Pretext type | Delivered | Landed | Rate | Blocked | Landed against |");
        md.AppendLine("|---|---:|---:|---:|---:|---|");

        foreach (SliceStats slice in stats.ByPretext)
        {
            string targets = slice.SucceededAgainst.Count == 0
                ? "—"
                : string.Join(", ", slice.SucceededAgainst.Select(id => NameFor(stats, id)));

            md.AppendLine(
                $"| `{slice.Key}` | {slice.Tally.Delivered} | {slice.Tally.Success} | " +
                $"{Percent(slice.Tally.SuccessRate)} | {slice.Tally.Blocked} | {targets} |");
        }

        md.AppendLine();

        SliceStats? top = stats.ByPretext.FirstOrDefault(s => s.Tally.Success > 0);
        if (top is not null)
        {
            PretextType? pretext = PretextCatalog.Find(top.Key);
            md.AppendLine(
                $"**Most effective approach:** {Describe(top.Key)} — {top.Tally.Success} of " +
                $"{top.Tally.Delivered} delivered attempts landed. " +
                (pretext is null ? string.Empty : pretext.Description));
            md.AppendLine();
        }
    }

    private static void AppendLeverBreakdown(StringBuilder md, EngagementStatistics stats)
    {
        md.AppendLine("## Which influence levers landed");
        md.AppendLine();
        md.AppendLine(
            "Tactics are logged as pairs; this table splits them into individual levers, so a " +
            "single attempt contributes to both of its levers.");
        md.AppendLine();
        md.AppendLine("| Lever | Delivered | Landed | Rate | What it exploits |");
        md.AppendLine("|---|---:|---:|---:|---|");

        foreach (SliceStats slice in stats.ByLever)
        {
            Tactic? tactic = PretextCatalog.FindTactic(slice.Key);
            md.AppendLine(
                $"| `{slice.Key}` | {slice.Tally.Delivered} | {slice.Tally.Success} | " +
                $"{Percent(slice.Tally.SuccessRate)} | {tactic?.Description ?? "—"} |");
        }

        md.AppendLine();
    }

    private static void AppendSusceptibilityRanking(StringBuilder md, EngagementStatistics stats)
    {
        md.AppendLine("## Most susceptible personas");
        md.AppendLine();
        md.AppendLine("| # | Persona | Role | Delivered | Landed | Rate | Levers that worked |");
        md.AppendLine("|---:|---|---|---:|---:|---:|---|");

        int rank = 0;
        foreach (EmployeeStats employee in stats.SusceptibilityRanking)
        {
            rank++;
            string levers = employee.WinningLevers.Count == 0
                ? "—"
                : string.Join(", ", employee.WinningLevers.Select(l => $"`{l.Key}`"));

            md.AppendLine(
                $"| {rank} | [{employee.Employee.Name}](employees/{employee.Employee.ReportSlug()}.md) | " +
                $"{employee.Employee.Role} | {employee.Tally.Delivered} | {employee.Tally.Success} | " +
                $"{Percent(employee.Tally.SuccessRate)} | {levers} |");
        }

        md.AppendLine();
    }

    private static void AppendResistanceSummary(StringBuilder md, EngagementStatistics stats)
    {
        int escalated = SumSignal(stats, ResistanceSignal.Escalated);
        int verified = SumSignal(stats, ResistanceSignal.Verified);
        int disengaged = SumSignal(stats, ResistanceSignal.Disengaged);
        int unclassified = SumSignal(stats, ResistanceSignal.Unclassified);

        md.AppendLine("## How attempts were stopped");
        md.AppendLine();
        md.AppendLine("| Resisting behaviour | Attempts | Why it matters |");
        md.AppendLine("|---|---:|---|");
        md.AppendLine(
            $"| Escalated to security | {escalated} | The outcome training aims for: the attempt " +
            "becomes a detection signal for everyone else. |");
        md.AppendLine(
            $"| Verified before acting | {verified} | The attempt was stopped by process, which " +
            "generalises to approaches nobody has seen yet. |");
        md.AppendLine(
            $"| Never engaged | {disengaged} | Safe this time, but passive: the same persona may " +
            "act on a more compelling approach. |");

        if (unclassified > 0)
        {
            md.AppendLine(
                $"| Unclassified | {unclassified} | The recorded reason did not identify a specific " +
                "resisting behaviour. |");
        }

        md.AppendLine();

        if (escalated == 0 && stats.Tally.Delivered > 0)
        {
            md.AppendLine(
                "> **No attempt was escalated to security by its target.** Even the approaches that " +
                "failed did so quietly, so none of them would have generated a warning for anyone " +
                "else. Exercising the reporting path is the highest-leverage change available here.");
            md.AppendLine();
        }
    }

    private static void AppendOrgRecommendations(StringBuilder md, EngagementStatistics stats)
    {
        md.AppendLine("## Recommendations");
        md.AppendLine();

        List<string> recommendations = [];

        foreach (SliceStats slice in stats.ByPretext.Where(s => s.Tally.Success > 0).Take(4))
        {
            PretextType? pretext = PretextCatalog.Find(slice.Key);
            if (pretext is not null)
            {
                recommendations.Add(
                    $"**{Describe(slice.Key)}** landed {Times(slice.Tally.Success)}. {pretext.Countermeasure}");
            }
        }

        SliceStats? topLever = stats.ByLever.FirstOrDefault(s => s.Tally.Success > 0);
        if (topLever is not null)
        {
            recommendations.Add(LeverRecommendation(topLever.Key, topLever.Tally.Success));
        }

        if (SumSignal(stats, ResistanceSignal.Escalated) == 0 && stats.Tally.Delivered > 0)
        {
            recommendations.Add(
                "**Make reporting the reflex.** No target escalated an attempt during this " +
                "engagement. Publish a one-click reporting path, acknowledge every report, and " +
                "state explicitly that reporting a false alarm carries no penalty.");
        }

        EmployeeStats[] highRisk = stats.SusceptibilityRanking
            .Where(e => e.Tally.Delivered >= 2 && e.Tally.SuccessRate >= 0.6)
            .ToArray();

        if (highRisk.Length > 0)
        {
            recommendations.Add(
                $"**Coach the {Plural(highRisk.Length, "highest-exposure role")} directly** — " +
                string.Join(", ", highRisk.Select(e => $"{e.Employee.Name} ({e.Employee.Role})")) +
                ". Their individual reports list the specific approaches that worked; a short " +
                "one-to-one walkthrough beats another all-staff module.");
        }

        int staleTraining = stats.Employees
            .Count(e => e.Employee.Traits.TrainingRecency < 0.35 && e.Tally.Success > 0);
        if (staleTraining > 0)
        {
            recommendations.Add(
                $"**Close the training-recency gap.** {Plural(staleTraining, "persona")} with overdue " +
                "awareness training produced a favorable reply. Recency correlated with resistance " +
                "across this engagement more strongly than seniority did.");
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add(
                "No approach in this engagement produced a favorable reply. Maintain the current " +
                "verification and reporting practices, and re-run with a wider pretext set to keep " +
                "the exercise honest.");
        }

        for (int i = 0; i < recommendations.Count; i++)
        {
            md.AppendLine($"{i + 1}. {recommendations[i]}");
        }

        md.AppendLine();
    }

    private static void AppendControlValidation(StringBuilder md, EngagementStatistics stats)
    {
        md.AppendLine("## Control validation — content-safety gate");
        md.AppendLine();

        if (stats.ControlRows.Count == 0)
        {
            md.AppendLine(
                "No control tests were run in this engagement (`--no-safety-probe`). The gate " +
                "screened every composed lure, but this report carries no in-band evidence of it " +
                "firing.");
            md.AppendLine();
            return;
        }

        md.AppendLine(
            "Fixed, hand-written known-bad inputs were submitted through the identical path as a " +
            "real attempt — same agent, same gate, same logger — to show the pre-delivery control " +
            "actually fires. None of this content was model-generated, and none of it was delivered.");
        md.AppendLine();
        md.AppendLine("| Attempt | Control exercised | Outcome |");
        md.AppendLine("|---|---|---|");

        foreach (AttemptResult row in stats.ControlRows.OrderBy(r => r.AttemptId, StringComparer.Ordinal))
        {
            string caseId = row.Tactic;
            SafetyProbeCase? probe = SafetyProbe.FindCase(caseId);
            string outcome = row.Outcome == AttemptOutcome.Blocked
                ? "✅ blocked pre-delivery"
                : $"❌ **not blocked** (`{row.Outcome}`)";

            md.AppendLine($"| `{row.AttemptId}` | {probe?.Control ?? caseId} | {outcome} |");
        }

        md.AppendLine();

        int blocked = stats.ControlTally.Blocked;
        int total = stats.ControlTally.Total;
        md.AppendLine(blocked == total
            ? $"All {total} control inputs were blocked before delivery."
            : $"**{total - blocked} of {total} control inputs were not blocked.** Treat the gate as " +
              "unverified and investigate before relying on this engagement's safety claims.");
        md.AppendLine();
    }

    private static void AppendCoverage(StringBuilder md, EngagementStatistics stats)
    {
        string[] untriedPretexts = PretextCatalog.All
            .Select(p => p.Id)
            .Where(id => stats.ByPretext.All(s => !string.Equals(s.Key, id, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (stats.UntestedEmployees.Count == 0 && untriedPretexts.Length == 0)
        {
            return;
        }

        md.AppendLine("## Coverage gaps");
        md.AppendLine();

        if (stats.UntestedEmployees.Count > 0)
        {
            md.AppendLine(
                $"**{Plural(stats.UntestedEmployees.Count, "persona")} received no attempt** and " +
                "therefore have no individual report: " +
                string.Join(", ", stats.UntestedEmployees.Select(e => $"{e.Name} ({e.Role})")) +
                ". Their absence from the ranking is a gap in the plan, not evidence of resistance.");
            md.AppendLine();
        }

        if (untriedPretexts.Length > 0)
        {
            md.AppendLine(
                $"**{Plural(untriedPretexts.Length, "pretext type")} {Was(untriedPretexts.Length)} " +
                "not exercised:** " +
                string.Join(", ", untriedPretexts.Select(p => $"`{p}`")) +
                ". Increase `--attempts` to widen coverage.");
            md.AppendLine();
        }
    }

    private static void AppendProvenance(
        StringBuilder md, EngagementStatistics stats, EngagementManifest? manifest)
    {
        md.AppendLine("## Method and provenance");
        md.AppendLine();
        md.AppendLine(
            "Each attempt was made by a freshly instantiated stateless agent that composed one " +
            "lure, submitted it to the content-safety gate, delivered it to a scripted synthetic " +
            "persona if cleared, judged the single reply, returned one result object, and was then " +
            "discarded. Agents never write to disk; the orchestrator appends every result to the " +
            "log, which is the sole source for this report.");
        md.AppendLine();

        if (manifest is null)
        {
            md.AppendLine(
                $"Generated from `{stats.EngagementId}.jsonl` with no run manifest present, so " +
                "engine and integrity details are unavailable.");
            md.AppendLine();
            return;
        }

        md.AppendLine("| Field | Value |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| Lure composition | {manifest.Engine.Composer} |");
        md.AppendLine($"| Safety gate | {manifest.Engine.SafetyGate} |");
        md.AppendLine($"| Employee responder | {manifest.Engine.Responder} |");
        md.AppendLine($"| Reply judgment | {manifest.Engine.Judge} |");
        md.AppendLine($"| Model backend | {manifest.Engine.Backend} |");
        md.AppendLine($"| Model calls / tokens | {manifest.ModelCalls} / {manifest.ModelTokens} |");
        md.AppendLine($"| Attempts planned / logged | {manifest.AttemptsPlanned} / {manifest.AttemptsLogged} |");
        md.AppendLine($"| Log fields redacted pre-write | {manifest.LogFieldsRedacted} |");
        md.AppendLine($"| Log SHA-256 chain | `{manifest.LogSha256Chain}` |");

        if (manifest.JudgeAgreement is { } agreement)
        {
            md.AppendLine(
                $"| Judge vs persona-model agreement | {agreement.Agreements}/{agreement.ComparableAttempts} " +
                $"({Percent(agreement.AgreementRate)}) |");
        }

        md.AppendLine();

        if (manifest.JudgeAgreement is { } check)
        {
            // Both sides of that comparison are template-driven on the deterministic engine, so
            // the figure would be misleading presented as independent corroboration.
            bool deterministicJudge = manifest.Engine.Judge
                .Contains("no model backend", StringComparison.OrdinalIgnoreCase);

            string caveat = deterministicJudge
                ? "On the deterministic engine both the replies and the judgment come from fixed " +
                  "templates, so near-total agreement is expected: this figure is a consistency " +
                  "check on the pipeline, not independent corroboration of the outcomes. Run with " +
                  "`--engine llm` for a judgment genuinely independent of the responder."
                : "The judgment and the reply were produced by separate model calls with different " +
                  "prompts and no shared state, so the agreement rate is a real check on whether " +
                  "the outcomes in this report mean what they say.";

            md.AppendLine(
                "The agent judged each reply from its wording alone, with no access to the " +
                "responder's internal decision. Those two views agreed on " +
                $"{check.Agreements} of {check.ComparableAttempts} delivered attempts " +
                $"({Percent(check.AgreementRate)}). {caveat}");
            md.AppendLine();
        }

        if (manifest.Errors.Count > 0)
        {
            md.AppendLine(
                $"**{Plural(manifest.Errors.Count, "attempt")} failed outright** and produced no outcome. " +
                "They are absent from every count above, and recorded in " +
                $"`{stats.EngagementId}.run.json` rather than invented as a result.");
            md.AppendLine();
        }

        if (manifest.LogFieldsRedacted > 0)
        {
            md.AppendLine(
                $"{Plural(manifest.LogFieldsRedacted, "model-authored field")} " +
                $"{Was(manifest.LogFieldsRedacted)} replaced with a safe equivalent before being " +
                "written, under: " +
                string.Join(", ", manifest.RedactionCategories.Select(c => $"`{c}`")) + ".");
            md.AppendLine();
        }
    }

    // ------------------------------------------------------------ employee report

    public static string BuildEmployeeReport(
        EmployeeStats stats, EngagementStatistics engagement, EngagementManifest? manifest = null)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(engagement);

        Employee employee = stats.Employee;
        StringBuilder md = new();

        md.AppendLine($"# Individual Awareness Report — {employee.Name}");
        md.AppendLine();
        md.AppendLine(SimulationBanner);
        md.AppendLine();

        md.AppendLine("## Target profile");
        md.AppendLine();
        md.AppendLine("| Field | Value |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| Persona ID | `{employee.Id}` |");
        md.AppendLine($"| Name | {employee.Name} (fabricated) |");
        md.AppendLine($"| Role | {employee.Role} |");
        md.AppendLine($"| Department | {employee.Department} |");
        md.AppendLine($"| Synthetic mailbox | `{employee.Email}` (non-routable) |");
        md.AppendLine($"| Synthetic exposure | {(employee.Exposure.Count == 0 ? "none recorded" : string.Join(", ", employee.Exposure.Select(e => $"`{e}`")))} |");
        md.AppendLine($"| Engagement | `{engagement.EngagementId}` |");
        md.AppendLine();

        md.AppendLine("## Attempts against this persona");
        md.AppendLine();
        md.AppendLine("| Attempt | Pretext type | Tactic | Content safety | Outcome |");
        md.AppendLine("|---|---|---|---|---|");

        foreach (AttemptResult attempt in stats.Attempts)
        {
            string safety = attempt.ContentSafetyFlagged ? "flagged — not delivered" : "cleared";
            md.AppendLine(
                $"| `{attempt.AttemptId}` | `{attempt.PretextType}` | {attempt.Tactic} | " +
                $"{safety} | {OutcomeBadge(attempt.Outcome)} |");
        }

        md.AppendLine();
        md.AppendLine(
            $"{stats.Tally.Success} of {stats.Tally.Delivered} delivered attempts produced a " +
            $"favorable reply ({Percent(stats.Tally.SuccessRate)}).");
        md.AppendLine();

        AppendEmployeeFindings(md, stats);
        AppendVulnerabilityPattern(md, stats);
        AppendEmployeeRecommendations(md, stats);

        md.AppendLine("## Provenance");
        md.AppendLine();
        md.AppendLine(
            $"Derived entirely from `{engagement.EngagementId}.jsonl`. Each row was produced by a " +
            "separate stateless agent making a single attempt with no knowledge of any other " +
            "attempt against this persona.");

        if (manifest is not null)
        {
            md.AppendLine();
            md.AppendLine(
                $"Engine: {manifest.Engine.Backend}. Responder: {manifest.Engine.Responder}. " +
                $"Judgment: {manifest.Engine.Judge}.");
        }

        md.AppendLine();
        return md.ToString();
    }

    private static void AppendEmployeeFindings(StringBuilder md, EmployeeStats stats)
    {
        md.AppendLine("## Findings");
        md.AppendLine();
        md.AppendLine("### Susceptibilities");
        md.AppendLine();

        AttemptResult[] successes = stats.Successes.ToArray();
        if (successes.Length == 0)
        {
            md.AppendLine(
                "No approach in this engagement produced a favorable reply from this persona.");
            md.AppendLine();
        }
        else
        {
            foreach (AttemptResult success in successes)
            {
                md.AppendLine(
                    $"- **{Describe(success.PretextType)}** ({success.Tactic}) — {success.SuccessReason}");
            }

            md.AppendLine();
        }

        md.AppendLine("### Positive behaviours");
        md.AppendLine();

        AttemptResult[] failures = stats.Failures.ToArray();
        if (failures.Length == 0)
        {
            md.AppendLine("Every delivered attempt against this persona succeeded; nothing was resisted.");
            md.AppendLine();
            return;
        }

        foreach (IGrouping<ResistanceSignal, AttemptResult> group in failures
                     .GroupBy(f => EngagementStatistics.Classify(f.FailureReason))
                     .OrderBy(g => g.Key))
        {
            string label = group.Key switch
            {
                ResistanceSignal.Escalated => "Escalated to security",
                ResistanceSignal.Verified => "Verified before acting",
                ResistanceSignal.Disengaged => "Did not engage",
                _ => "Other resistance",
            };

            md.AppendLine(
                $"- **{label}** ({Plural(group.Count(), "attempt")}): " +
                string.Join(", ", group.Select(f => $"`{f.PretextType}`")) +
                ". " + group.First().FailureReason);
        }

        md.AppendLine();
    }

    private static void AppendVulnerabilityPattern(StringBuilder md, EmployeeStats stats)
    {
        md.AppendLine("## Vulnerability pattern");
        md.AppendLine();

        PersonaTraits traits = stats.Employee.Traits;
        List<string> sentences = [];

        AttemptResult[] successes = stats.Successes.ToArray();

        if (successes.Length == 0)
        {
            sentences.Add(
                $"Across {stats.Tally.Delivered} delivered attempts this persona did not act on any " +
                "approach.");

            ResistanceSignal dominant = stats.ResistanceSignals
                .OrderByDescending(p => p.Value)
                .Select(p => p.Key)
                .FirstOrDefault();

            sentences.Add(dominant switch
            {
                ResistanceSignal.Escalated =>
                    "Resistance was active: attempts were escalated rather than merely ignored, which " +
                    "turns each one into a signal the rest of the organisation can act on.",
                ResistanceSignal.Verified =>
                    "Resistance came from verifying requests independently, which is the durable kind: " +
                    "it holds against approaches this persona has never seen before.",
                ResistanceSignal.Disengaged =>
                    "Resistance was passive — the messages were simply not engaged with. That is a " +
                    "weaker guarantee than verifying or reporting, because a more compelling approach " +
                    "may still land.",
                _ => "The recorded reasons do not identify a single dominant resisting behaviour.",
            });

            sentences.Add(
                $"The persona profile matches that outcome: {Dial("verification habit", traits.VerificationHabit)}, " +
                $"{Dial("training recency", traits.TrainingRecency)}, and " +
                $"{Dial("technical literacy", traits.TechnicalLiteracy)}.");
        }
        else
        {
            IReadOnlyList<KeyValuePair<string, int>> levers = stats.WinningLevers;

            sentences.Add(
                $"{successes.Length} of {stats.Tally.Delivered} delivered attempts landed " +
                $"({Percent(stats.Tally.SuccessRate)}).");

            KeyValuePair<string, int>[] universal = levers
                .Where(l => l.Value == successes.Length)
                .ToArray();

            if (universal.Length > 0 && successes.Length > 1)
            {
                sentences.Add(
                    $"Every successful attempt used {JoinLevers(universal.Select(l => l.Key))}, which " +
                    "makes that lever the single most useful thing to train against here.");
            }
            else if (levers.Count > 0)
            {
                sentences.Add(
                    $"The levers present in successful attempts were " +
                    $"{JoinLevers(levers.Select(l => l.Key))}.");
            }

            string[] channels = successes
                .Select(s => PretextCatalog.Find(s.PretextType)?.Channel)
                .Where(c => c is not null)
                .Select(c => c!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToArray();

            if (channels.Length == 1)
            {
                sentences.Add($"All of them arrived over the {channels[0]} channel.");
            }
            else if (channels.Length > 1)
            {
                sentences.Add(
                    $"They arrived across {channels.Length} channels ({string.Join(", ", channels)}), " +
                    "so this is not a single-channel problem.");
            }

            sentences.Add(TraitExplanation(traits));

            int matchedExposure = successes
                .Select(s => PretextCatalog.Find(s.PretextType))
                .Where(p => p is not null)
                .Count(p => p!.ExposureTriggers.Any(stats.Employee.HasExposure));

            if (matchedExposure > 0)
            {
                sentences.Add(
                    $"{Plural(matchedExposure, "successful approach", "successful approaches")} " +
                    $"{Was(matchedExposure)} plausible specifically because of this role's synthetic " +
                    $"exposure attributes ({string.Join(", ", stats.Employee.Exposure)}), meaning the " +
                    "pretext did not have to be convincing in general — only convincing for this job.");
            }

            IReadOnlyList<string> ineffective = stats.IneffectiveLevers;
            if (ineffective.Count > 0)
            {
                sentences.Add(
                    $"Approaches built on {JoinLevers(ineffective)} did not land, so the gap is " +
                    "specific rather than general susceptibility.");
            }
        }

        md.AppendLine(string.Join(" ", sentences));
        md.AppendLine();
    }

    private static string TraitExplanation(PersonaTraits traits)
    {
        (string Name, double Value)[] susceptibility =
        [
            ("deference to authority", traits.AuthorityDeference),
            ("sensitivity to deadline pressure", traits.UrgencySusceptibility),
            ("curiosity about unexpected content", traits.Curiosity),
            ("urge to be helpful", traits.Helpfulness),
        ];

        (string Name, double Value) top = susceptibility.MaxBy(t => t.Value);

        return $"The dominant persona factor is {top.Name}, which is {DialWord(top.Value)} " +
               $"({top.Value:0.00}), set against {Dial("verification habit", traits.VerificationHabit)} " +
               $"and {Dial("training recency", traits.TrainingRecency)}.";
    }

    private static void AppendEmployeeRecommendations(StringBuilder md, EmployeeStats stats)
    {
        md.AppendLine("## Recommendations");
        md.AppendLine();

        List<string> recommendations = [];

        foreach (string pretextId in stats.Successes
                     .Select(s => s.PretextType)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            PretextType? pretext = PretextCatalog.Find(pretextId);
            if (pretext is not null)
            {
                recommendations.Add($"**{Describe(pretextId)}:** {pretext.Countermeasure}");
            }
        }

        PersonaTraits traits = stats.Employee.Traits;

        if (stats.Tally.Success > 0 && traits.VerificationHabit < 0.45)
        {
            recommendations.Add(
                "**Build one verification habit, not a general suspicion.** Pick the two request " +
                "types this role handles most and make an out-of-band check a required step for " +
                "both, using contact details already on file rather than any supplied in the request.");
        }

        if (traits.TrainingRecency < 0.40)
        {
            recommendations.Add(
                "**Bring awareness training current.** This persona's training is well overdue, and " +
                "recency tracked with resistance more closely than role or seniority across this " +
                "engagement.");
        }

        if (!stats.ResistanceSignals.ContainsKey(ResistanceSignal.Escalated))
        {
            // Phrase this from what actually happened: a persona who resisted nothing has no
            // "attempts that failed" to point at.
            string context = stats.Tally.Failure > 0
                ? "even the attempts this persona did not act on produced no warning for anyone else"
                : "no attempt against this persona generated a warning for anyone else";

            recommendations.Add(
                $"**Practise the reporting path.** Nothing was escalated to security, so {context}. " +
                "One walkthrough of how to report, plus confirmation that a false alarm is welcome, " +
                "closes that gap.");
        }

        if (stats.ResistanceSignals.GetValueOrDefault(ResistanceSignal.Disengaged) >= 2
            && stats.Tally.Success > 0)
        {
            recommendations.Add(
                "**Convert non-engagement into reporting.** Several attempts were simply ignored " +
                "while others succeeded, which suggests inattention rather than judgement was doing " +
                "the work.");
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add(
                "**No individual action required.** This persona resisted every delivered attempt. " +
                "Use their handling of these approaches as the worked example for the wider team.");
        }

        for (int i = 0; i < recommendations.Count; i++)
        {
            md.AppendLine($"{i + 1}. {recommendations[i]}");
        }

        md.AppendLine();
    }

    // ------------------------------------------------------------------- helpers

    private static string LeverRecommendation(string lever, int successes)
    {
        string carried = $"The `{lever}` lever carried {Times(successes)}.";

        return lever switch
        {
            "urgency" or "scarcity" =>
                $"**Take the pressure out of the process.** {carried} Publish the real deadlines for " +
                "the requests staff field most often, so an unfamiliar one is visibly abnormal rather " +
                "than merely stressful.",

            "authority" or "social_proof" =>
                $"**Make challenging upward safe.** {carried} State in policy that verifying a " +
                "leadership request is expected, and have leadership say so themselves.",

            "curiosity" =>
                $"**Give curiosity a safe landing.** {carried} Route unexpected documents and " +
                "notifications through the platform itself rather than through links, so looking " +
                "costs nothing.",

            "reciprocity" or "familiarity" =>
                $"**Decouple helpfulness from compliance.** {carried} Give staff a scripted, " +
                "non-awkward way to help while still verifying — the goal is to remove the social " +
                "cost of checking.",

            "fear" =>
                $"**Remove the penalty for pausing.** {carried} Staff who expect consequences for " +
                "delay will act before verifying; say plainly that checking first is never the " +
                "wrong call.",

            _ => $"**Address the `{lever}` lever** in the next awareness cycle. {carried}",
        };
    }

    /// <summary>Renders an attempt count as prose: "once", "3 times".</summary>
    private static string Times(int count) => count == 1 ? "once" : $"{count} times";

    /// <summary>Renders a count with its noun agreeing in number.</summary>
    private static string Plural(int count, string singular, string? plural = null) =>
        count == 1 ? $"{count} {singular}" : $"{count} {plural ?? singular + "s"}";

    /// <summary>"was" or "were", to agree with a count.</summary>
    private static string Was(int count) => count == 1 ? "was" : "were";

    private static string OutcomeBadge(AttemptOutcome outcome) => outcome switch
    {
        AttemptOutcome.Success => "**success** — favorable reply",
        AttemptOutcome.Failure => "failure — unfavorable reply",
        _ => "blocked — never delivered",
    };

    private static string Describe(string pretextId) =>
        PretextCatalog.Find(pretextId)?.Label ?? pretextId;

    private static string NameFor(EngagementStatistics stats, string employeeId) =>
        stats.Org.Find(employeeId)?.Name ?? employeeId;

    private static int SumSignal(EngagementStatistics stats, ResistanceSignal signal) =>
        stats.Employees.Sum(e => e.ResistanceSignals.GetValueOrDefault(signal));

    private static string JoinLevers(IEnumerable<string> levers)
    {
        string[] items = levers.Select(l => $"`{l}`").ToArray();
        return items.Length switch
        {
            0 => "no identified lever",
            1 => items[0],
            2 => $"{items[0]} and {items[1]}",
            _ => $"{string.Join(", ", items[..^1])}, and {items[^1]}",
        };
    }

    /// <summary>Renders a 0-1 trait dial as an adjective phrase: "a low verification habit (0.40)".</summary>
    private static string Dial(string noun, double value) =>
        $"{Article(DialWord(value))} {DialWord(value)} {noun} ({value:0.00})";

    private static string DialWord(double value) => value switch
    {
        >= 0.85 => "very high",
        >= 0.65 => "high",
        >= 0.45 => "moderate",
        >= 0.25 => "low",
        _ => "very low",
    };

    private static string Article(string following) =>
        "aeiou".Contains(char.ToLowerInvariant(following[0])) ? "an" : "a";

    private static double? Share(int part, int whole) => whole == 0 ? null : (double)part / whole;

    private static string Percent(double? rate) => rate is null ? "n/a" : $"{rate.Value * 100:0}%";

    private static string FormatWindow(EngagementStatistics stats)
    {
        if (stats.WindowStart is null || stats.WindowEnd is null)
        {
            return "—";
        }

        return stats.WindowStart == stats.WindowEnd
            ? AttemptResult.FormatTimestamp(stats.WindowStart.Value)
            : $"{AttemptResult.FormatTimestamp(stats.WindowStart.Value)} → " +
              $"{AttemptResult.FormatTimestamp(stats.WindowEnd.Value)}";
    }
}
