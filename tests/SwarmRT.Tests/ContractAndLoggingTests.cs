using System.Text.Json;
using SwarmRT.Contracts;
using SwarmRT.Logging;

namespace SwarmRT.Tests;

/// <summary>Design §5.2 — the result-object invariants the log and reports depend on.</summary>
public class AttemptResultContractTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 29, 18, 42, 11, TimeSpan.Zero);

    [Fact]
    public void SuccessFactoryProducesAValidResult()
    {
        AttemptResult result = AttemptResult.ForSuccess(
            TestSupport.Assignment(), At, "Target acted without verifying.", "Attempted a pretext.");

        Assert.Empty(result.Validate());
        Assert.Equal(AttemptOutcome.Success, result.Outcome);
        Assert.Null(result.FailureReason);
        Assert.False(result.ContentSafetyFlagged);
    }

    [Fact]
    public void FailureFactoryProducesAValidResult()
    {
        AttemptResult result = AttemptResult.ForFailure(
            TestSupport.Assignment(), At, "Target escalated it.", "Attempted a pretext.");

        Assert.Empty(result.Validate());
        Assert.Null(result.SuccessReason);
    }

    [Fact]
    public void BlockedFactoryLeavesBothReasonsNullAndFlagsContentSafety()
    {
        AttemptResult result = AttemptResult.ForBlocked(
            TestSupport.Assignment(), At, "Blocked by the gate.");

        Assert.Empty(result.Validate());
        Assert.Null(result.SuccessReason);
        Assert.Null(result.FailureReason);
        Assert.True(result.ContentSafetyFlagged);
    }

    [Fact]
    public void SuccessWithoutAReasonIsInvalid()
    {
        AttemptResult result = AttemptResult.ForSuccess(
            TestSupport.Assignment(), At, "reason", "summary") with { SuccessReason = null };

        Assert.Contains("success requires a success_reason", result.Validate());
        Assert.Throws<InvalidOperationException>(result.EnsureValid);
    }

    [Fact]
    public void PopulatingBothReasonsIsInvalid()
    {
        AttemptResult result = AttemptResult.ForSuccess(
            TestSupport.Assignment(), At, "reason", "summary") with { FailureReason = "also this" };

        Assert.Contains("success must leave failure_reason null", result.Validate());
    }

    [Fact]
    public void BlockedWithAReasonIsInvalid()
    {
        AttemptResult result = AttemptResult.ForBlocked(
            TestSupport.Assignment(), At, "summary") with { FailureReason = "why" };

        Assert.Contains("blocked must leave both reason fields null", result.Validate());
    }

    [Fact]
    public void FlaggedMustMeanBlocked()
    {
        AttemptResult result = AttemptResult.ForFailure(
            TestSupport.Assignment(), At, "reason", "summary") with { ContentSafetyFlagged = true };

        Assert.Contains(
            "content_safety_flagged must be true exactly when outcome is blocked", result.Validate());
    }

    [Fact]
    public void AnEmptyAttemptSummaryIsInvalid()
    {
        AttemptResult result = AttemptResult.ForFailure(
            TestSupport.Assignment(), At, "reason", "summary") with { AttemptSummary = "  " };

        Assert.Contains("attempt_summary is required on every outcome", result.Validate());
    }

    [Fact]
    public void SerializesWithTheFieldNamesFromTheDesign()
    {
        AttemptResult result = AttemptResult.ForSuccess(
            TestSupport.Assignment(), At, "Target acted.", "Attempted a pretext.");

        string json = JsonSerializer.Serialize(result, SwarmJson.Line);

        Assert.Contains("\"attempt_id\":\"att-0001\"", json, StringComparison.Ordinal);
        Assert.Contains("\"content_safety_flagged\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"success\"", json, StringComparison.Ordinal);
        Assert.Contains("\"failure_reason\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"timestamp\":\"2026-07-29T18:42:11Z\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AttemptOutcome.Success, "success")]
    [InlineData(AttemptOutcome.Failure, "failure")]
    [InlineData(AttemptOutcome.Blocked, "blocked")]
    public void OutcomeEnumSerializesLowerCase(AttemptOutcome outcome, string expected)
    {
        string json = JsonSerializer.Serialize(outcome, SwarmJson.Line);

        Assert.Equal($"\"{expected}\"", json);
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        AttemptResult original = AttemptResult.ForFailure(
            TestSupport.Assignment(), At, "Target escalated it.", "Attempted a pretext.");

        string json = JsonSerializer.Serialize(original, SwarmJson.Line);
        AttemptResult? restored = JsonSerializer.Deserialize<AttemptResult>(json, SwarmJson.Reading);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void AssignmentSeedIsNotPartOfTheWireContract()
    {
        string json = JsonSerializer.Serialize(TestSupport.Assignment(seed: 999), SwarmJson.Line);

        Assert.DoesNotContain("999", json, StringComparison.Ordinal);
        Assert.DoesNotContain("seed", json, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Design §3.6 and §5.3 — the append-only JSONL log.</summary>
public class JsonlAttemptLoggerTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 29, 18, 42, 11, TimeSpan.Zero);

    [Fact]
    public void WritesOneLinePerAttempt()
    {
        string directory = TestSupport.TempDirectory();
        try
        {
            string path = Path.Combine(directory, "TST.jsonl");

            using (JsonlAttemptLogger logger = new(path))
            {
                logger.Append(AttemptResult.ForSuccess(
                    TestSupport.Assignment("att-0001"), At, "acted", "summary"));
                logger.Append(AttemptResult.ForFailure(
                    TestSupport.Assignment("att-0002"), At, "escalated", "summary"));
                Assert.Equal(2, logger.LineCount);
            }

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.All(lines, line => Assert.StartsWith("{", line, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AppendsRatherThanRewriting()
    {
        string directory = TestSupport.TempDirectory();
        try
        {
            string path = Path.Combine(directory, "TST.jsonl");

            using (JsonlAttemptLogger first = new(path))
            {
                first.Append(AttemptResult.ForSuccess(
                    TestSupport.Assignment("att-0001"), At, "acted", "summary"));
            }

            using (JsonlAttemptLogger second = new(path))
            {
                second.Append(AttemptResult.ForFailure(
                    TestSupport.Assignment("att-0002"), At, "escalated", "summary"));
            }

            Assert.Equal(2, File.ReadAllLines(path).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RefusesToWriteAResultThatViolatesTheContract()
    {
        string directory = TestSupport.TempDirectory();
        try
        {
            string path = Path.Combine(directory, "TST.jsonl");
            using JsonlAttemptLogger logger = new(path);

            AttemptResult invalid = AttemptResult.ForSuccess(
                TestSupport.Assignment(), At, "acted", "summary") with { SuccessReason = null };

            Assert.Throws<InvalidOperationException>(() => logger.Append(invalid));
            Assert.Equal(0, logger.LineCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DigestMatchesARecomputationOverTheFile()
    {
        string directory = TestSupport.TempDirectory();
        try
        {
            string path = Path.Combine(directory, "TST.jsonl");
            string digest;

            using (JsonlAttemptLogger logger = new(path))
            {
                logger.Append(AttemptResult.ForSuccess(
                    TestSupport.Assignment("att-0001"), At, "acted", "summary"));
                logger.Append(AttemptResult.ForFailure(
                    TestSupport.Assignment("att-0002"), At, "escalated", "summary"));
                digest = logger.Digest;
            }

            Assert.Equal(digest, AttemptLogReader.ComputeDigest(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DigestChangesWhenTheLogIsEdited()
    {
        string directory = TestSupport.TempDirectory();
        try
        {
            string path = Path.Combine(directory, "TST.jsonl");
            string digest;

            using (JsonlAttemptLogger logger = new(path))
            {
                logger.Append(AttemptResult.ForSuccess(
                    TestSupport.Assignment("att-0001"), At, "acted", "summary"));
                digest = logger.Digest;
            }

            string tampered = File.ReadAllText(path).Replace("acted", "never acted", StringComparison.Ordinal);
            File.WriteAllText(path, tampered);

            Assert.NotEqual(digest, AttemptLogReader.ComputeDigest(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReaderRoundTripsEveryRow()
    {
        string directory = TestSupport.TempDirectory();
        try
        {
            string path = Path.Combine(directory, "TST.jsonl");

            using (JsonlAttemptLogger logger = new(path))
            {
                logger.Append(AttemptResult.ForSuccess(
                    TestSupport.Assignment("att-0001"), At, "acted", "summary"));
                logger.Append(AttemptResult.ForBlocked(
                    TestSupport.Assignment("att-0002"), At, "blocked by the gate"));
            }

            IReadOnlyList<AttemptResult> rows = AttemptLogReader.Read(path);

            Assert.Equal(2, rows.Count);
            Assert.Equal(AttemptOutcome.Success, rows[0].Outcome);
            Assert.Equal(AttemptOutcome.Blocked, rows[1].Outcome);
            Assert.All(rows, r => Assert.Empty(r.Validate()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReaderFailsLoudlyOnAMalformedLine()
    {
        string directory = TestSupport.TempDirectory();
        try
        {
            string path = Path.Combine(directory, "TST.jsonl");
            File.WriteAllText(path, "{\"attempt_id\": broken}\n");

            Assert.Throws<InvalidDataException>(() => AttemptLogReader.Read(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
