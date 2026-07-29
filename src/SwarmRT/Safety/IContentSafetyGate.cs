using SwarmRT.Agents;
using SwarmRT.Org;

namespace SwarmRT.Safety;

/// <summary>The gate's decision about one composed lure.</summary>
/// <param name="Flagged">True when the lure must not be delivered.</param>
/// <param name="Category">Stable machine-readable reason code.</param>
/// <param name="Rationale">Short human explanation, written into the blocked row's attempt_summary.</param>
/// <param name="Source">Which layer decided, e.g. "heuristic" or "model-self-check".</param>
public sealed record SafetyVerdict(bool Flagged, string Category, string Rationale, string Source)
{
    public static SafetyVerdict Cleared(string source) =>
        new(false, "cleared", "No prohibited content detected.", source);

    public static SafetyVerdict Block(string category, string rationale, string source) =>
        new(true, category, rationale, source);
}

/// <summary>
/// Design §3.5 — every composed lure passes this gate before simulated delivery.
/// A flagged lure is never delivered and is logged with outcome <c>blocked</c>.
/// </summary>
public interface IContentSafetyGate
{
    /// <summary>Which layers are active, recorded in the report's controls section.</summary>
    string Description { get; }

    Task<SafetyVerdict> ScreenAsync(
        ComposedLure lure,
        Employee target,
        CancellationToken cancellationToken = default);
}
