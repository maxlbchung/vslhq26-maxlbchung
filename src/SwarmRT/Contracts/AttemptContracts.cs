using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwarmRT.Contracts;

/// <summary>Design §5.2 outcome enum. Serialized lower-case to match the log contract.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttemptOutcome>))]
public enum AttemptOutcome
{
    /// <summary>A favorable reply was received; the target took or committed to the action.</summary>
    [JsonStringEnumMemberName("success")]
    Success,

    /// <summary>The reply was unfavorable: ignored, questioned, refused, or reported.</summary>
    [JsonStringEnumMemberName("failure")]
    Failure,

    /// <summary>The composed lure was flagged by the content-safety gate and never delivered.</summary>
    [JsonStringEnumMemberName("blocked")]
    Blocked,
}

/// <summary>
/// Design §5.1 — the entire input to one cloned agent. This is the only context
/// an agent instance ever receives, which is what makes the clone stateless.
/// </summary>
public sealed record AgentAssignment
{
    [JsonPropertyName("engagement_id")]
    public required string EngagementId { get; init; }

    [JsonPropertyName("attempt_id")]
    public required string AttemptId { get; init; }

    [JsonPropertyName("target_employee_id")]
    public required string TargetEmployeeId { get; init; }

    [JsonPropertyName("pretext_type")]
    public required string PretextType { get; init; }

    [JsonPropertyName("tactic")]
    public required string Tactic { get; init; }

    /// <summary>
    /// Per-attempt seed, derived from the engagement seed and attempt id. Not part
    /// of the design's wire contract, so it is excluded from serialization; it exists
    /// so a run is reproducible without giving the agent cross-attempt state.
    /// </summary>
    [JsonIgnore]
    public int Seed { get; init; }
}

/// <summary>
/// Design §5.2 — the single object an agent returns before being discarded, and
/// verbatim the JSONL log line the orchestrator appends. Property order matches
/// the design document's field order.
/// </summary>
public sealed record AttemptResult
{
    [JsonPropertyName("attempt_id")]
    public required string AttemptId { get; init; }

    [JsonPropertyName("engagement_id")]
    public required string EngagementId { get; init; }

    /// <summary>UTC, second precision, e.g. "2026-07-29T18:42:11Z".</summary>
    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("target_employee_id")]
    public required string TargetEmployeeId { get; init; }

    [JsonPropertyName("pretext_type")]
    public required string PretextType { get; init; }

    [JsonPropertyName("tactic")]
    public required string Tactic { get; init; }

    [JsonPropertyName("content_safety_flagged")]
    public required bool ContentSafetyFlagged { get; init; }

    [JsonPropertyName("outcome")]
    public required AttemptOutcome Outcome { get; init; }

    [JsonPropertyName("success_reason")]
    public string? SuccessReason { get; init; }

    [JsonPropertyName("failure_reason")]
    public string? FailureReason { get; init; }

    /// <summary>
    /// Pretext-level description of what was attempted (design §8.3). Deliberately
    /// not the lure text — this field is written to disk, so it must stay
    /// non-deployable.
    /// </summary>
    [JsonPropertyName("attempt_summary")]
    public required string AttemptSummary { get; init; }

    public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";

    public static string FormatTimestamp(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString(TimestampFormat);

    /// <summary>The reason string that applies to this outcome, if any.</summary>
    public string? Reason => Outcome switch
    {
        AttemptOutcome.Success => SuccessReason,
        AttemptOutcome.Failure => FailureReason,
        _ => null,
    };

    /// <summary>
    /// Checks the invariants the design states as requirements: exactly one reason
    /// on non-blocked outcomes, no reason on blocked, and flagged if and only if
    /// blocked. Returns an empty sequence when the object is well-formed.
    /// </summary>
    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(AttemptId))
        {
            yield return "attempt_id is required";
        }

        if (string.IsNullOrWhiteSpace(AttemptSummary))
        {
            yield return "attempt_summary is required on every outcome";
        }

        if (!DateTimeOffset.TryParse(Timestamp, out _))
        {
            yield return $"timestamp '{Timestamp}' is not a parseable instant";
        }

        switch (Outcome)
        {
            case AttemptOutcome.Success:
                if (string.IsNullOrWhiteSpace(SuccessReason))
                {
                    yield return "success requires a success_reason";
                }

                if (FailureReason is not null)
                {
                    yield return "success must leave failure_reason null";
                }

                break;

            case AttemptOutcome.Failure:
                if (string.IsNullOrWhiteSpace(FailureReason))
                {
                    yield return "failure requires a failure_reason";
                }

                if (SuccessReason is not null)
                {
                    yield return "failure must leave success_reason null";
                }

                break;

            case AttemptOutcome.Blocked:
                if (SuccessReason is not null || FailureReason is not null)
                {
                    yield return "blocked must leave both reason fields null";
                }

                break;
        }

        bool flaggedMatchesOutcome = ContentSafetyFlagged == (Outcome == AttemptOutcome.Blocked);
        if (!flaggedMatchesOutcome)
        {
            yield return "content_safety_flagged must be true exactly when outcome is blocked";
        }
    }

    public void EnsureValid()
    {
        string[] problems = Validate().ToArray();
        if (problems.Length > 0)
        {
            throw new InvalidOperationException(
                $"Result object for '{AttemptId}' violates the design §5.2 contract: " +
                string.Join("; ", problems));
        }
    }

    public static AttemptResult ForSuccess(
        AgentAssignment assignment, DateTimeOffset at, string successReason, string attemptSummary) =>
        new()
        {
            AttemptId = assignment.AttemptId,
            EngagementId = assignment.EngagementId,
            Timestamp = FormatTimestamp(at),
            TargetEmployeeId = assignment.TargetEmployeeId,
            PretextType = assignment.PretextType,
            Tactic = assignment.Tactic,
            ContentSafetyFlagged = false,
            Outcome = AttemptOutcome.Success,
            SuccessReason = successReason,
            FailureReason = null,
            AttemptSummary = attemptSummary,
        };

    public static AttemptResult ForFailure(
        AgentAssignment assignment, DateTimeOffset at, string failureReason, string attemptSummary) =>
        new()
        {
            AttemptId = assignment.AttemptId,
            EngagementId = assignment.EngagementId,
            Timestamp = FormatTimestamp(at),
            TargetEmployeeId = assignment.TargetEmployeeId,
            PretextType = assignment.PretextType,
            Tactic = assignment.Tactic,
            ContentSafetyFlagged = false,
            Outcome = AttemptOutcome.Failure,
            SuccessReason = null,
            FailureReason = failureReason,
            AttemptSummary = attemptSummary,
        };

    /// <summary>
    /// A lure the safety gate refused. The blocking reason is carried in
    /// <paramref name="attemptSummary"/> because design §5.2 requires both reason
    /// fields to be null for this outcome.
    /// </summary>
    public static AttemptResult ForBlocked(
        AgentAssignment assignment, DateTimeOffset at, string attemptSummary) =>
        new()
        {
            AttemptId = assignment.AttemptId,
            EngagementId = assignment.EngagementId,
            Timestamp = FormatTimestamp(at),
            TargetEmployeeId = assignment.TargetEmployeeId,
            PretextType = assignment.PretextType,
            Tactic = assignment.Tactic,
            ContentSafetyFlagged = true,
            Outcome = AttemptOutcome.Blocked,
            SuccessReason = null,
            FailureReason = null,
            AttemptSummary = attemptSummary,
        };
}

/// <summary>Shared serializer settings. JSONL lines must never be indented.</summary>
public static class SwarmJson
{
    public static readonly JsonSerializerOptions Line = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Reading = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
