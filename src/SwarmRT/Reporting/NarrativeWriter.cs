using System.Text;
using System.Text.Json.Serialization;
using SwarmRT.Model;
using SwarmRT.Safety;

namespace SwarmRT.Reporting;

/// <summary>
/// Optional model phrasing for the report's executive paragraph (design §3.7).
/// <para>
/// The model is given aggregate counts only — never lure text, never a reply — and its
/// output is screened before it can reach the file. Everything else in the report stays
/// pure aggregation, so a narrative that fails to generate costs nothing but a paragraph.
/// </para>
/// </summary>
public sealed class NarrativeWriter(IModelClient model, LogTextSanitizer sanitizer)
{
    private sealed record NarrativeOutput
    {
        [JsonPropertyName("summary")]
        public string? Summary { get; init; }
    }

    private const string SystemPrompt = """
        You write the opening paragraph of a security-awareness report for the organisation
        that was tested. You are given aggregate counts from a simulated engagement against
        a fabricated company.

        Write one paragraph, 3 to 4 sentences, for a manager who will act on it:
        - lead with what the numbers mean for the organisation, not with methodology
        - name the approach and the influence lever that worked best
        - name what worked well in the organisation's favour, if anything did
        - stay neutral and non-alarmist; never congratulate the attack side
        - no bullet points, no headings, no invented figures beyond what you are given
        - do not name any real company, product, or brand

        Reply with JSON only: {"summary": "<paragraph>"}
        """;

    public async Task<string?> WriteAsync(
        EngagementStatistics stats, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stats);

        StringBuilder facts = new();
        facts.AppendLine($"Synthetic organisation: {stats.Org.OrgName} ({stats.Org.Employees.Count} staff on the roster).");
        facts.AppendLine($"Attack attempts: {stats.Tally.Total}; delivered {stats.Tally.Delivered}; blocked pre-delivery {stats.Tally.Blocked}.");
        facts.AppendLine($"Favorable replies: {stats.Tally.Success} of {stats.Tally.Delivered} delivered ({Percent(stats.Tally.SuccessRate)}).");
        facts.AppendLine($"Individuals tested: {stats.Employees.Count}.");

        foreach (SliceStats slice in stats.ByPretext.Take(3))
        {
            facts.AppendLine(
                $"Pretext '{slice.Key}': {slice.Tally.Success} of {slice.Tally.Delivered} delivered attempts landed.");
        }

        foreach (SliceStats slice in stats.ByLever.Take(3))
        {
            facts.AppendLine(
                $"Influence lever '{slice.Key}': {slice.Tally.Success} of {slice.Tally.Delivered} delivered attempts landed.");
        }

        int escalated = stats.Employees
            .Sum(e => e.ResistanceSignals.GetValueOrDefault(ResistanceSignal.Escalated));
        int verified = stats.Employees
            .Sum(e => e.ResistanceSignals.GetValueOrDefault(ResistanceSignal.Verified));

        facts.AppendLine($"Attempts escalated to security by the target: {escalated}.");
        facts.AppendLine($"Attempts stopped by the target verifying first: {verified}.");

        ModelRequest request = new()
        {
            Kind = ModelCallKind.ReportNarrative,
            SystemPrompt = SystemPrompt,
            UserPrompt = facts.ToString(),
            Temperature = 0.4,
            MaxOutputTokens = 400,
        };

        string raw;
        try
        {
            raw = await model.CallModelAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ModelCallException)
        {
            // The narrative is decoration; the aggregated report stands without it.
            return null;
        }

        NarrativeOutput output;
        try
        {
            output = JsonPayload.Parse<NarrativeOutput>(raw, ModelCallKind.ReportNarrative);
        }
        catch (ModelCallException)
        {
            return null;
        }

        return sanitizer.TrySanitize(output.Summary, out string screened, MaxNarrativeLength)
            ? screened
            : null;
    }

    /// <summary>A paragraph is allowed more room than a single logged sentence.</summary>
    private const int MaxNarrativeLength = 1200;

    internal static string Percent(double? rate) =>
        rate is null ? "n/a" : $"{rate.Value * 100:0}%";
}
