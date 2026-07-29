using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwarmRT.Model;

namespace SwarmRT.Tests;

/// <summary>Recovering structured output from a model response (design §7).</summary>
public class JsonPayloadTests
{
    private sealed record Sample
    {
        [JsonPropertyName("favorable")]
        public bool Favorable { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }

    [Fact]
    public void LocatesABareObject() =>
        Assert.Equal("{\"a\":1}", JsonPayload.Locate("{\"a\":1}"));

    [Fact]
    public void LocatesAnObjectInsideAFencedBlock() =>
        Assert.Equal("{\"a\":1}", JsonPayload.Locate("```json\n{\"a\":1}\n```"));

    [Fact]
    public void LocatesAnObjectAfterAPreamble() =>
        Assert.Equal("{\"a\":1}", JsonPayload.Locate("Sure, here you go: {\"a\":1} - hope that helps."));

    [Fact]
    public void HandlesNestedObjects() =>
        Assert.Equal("{\"a\":{\"b\":2}}", JsonPayload.Locate("{\"a\":{\"b\":2}}"));

    /// <summary>Braces inside strings must not terminate the scan early.</summary>
    [Fact]
    public void IgnoresBracesInsideStrings() =>
        Assert.Equal(
            "{\"reason\":\"they said }{ oddly\"}",
            JsonPayload.Locate("{\"reason\":\"they said }{ oddly\"}"));

    [Fact]
    public void HandlesEscapedQuotes() =>
        Assert.Equal(
            "{\"reason\":\"said \\\"stop\\\" firmly\"}",
            JsonPayload.Locate("{\"reason\":\"said \\\"stop\\\" firmly\"}"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no json at all")]
    [InlineData("{\"unterminated\": true")]
    public void ReturnsNullWhenThereIsNoCompleteObject(string raw) =>
        Assert.Null(JsonPayload.Locate(raw));

    [Fact]
    public void ParsesIntoTheTargetType()
    {
        Sample sample = JsonPayload.Parse<Sample>(
            "```json\n{\"favorable\": true, \"reason\": \"acted\"}\n```", ModelCallKind.JudgeReply);

        Assert.True(sample.Favorable);
        Assert.Equal("acted", sample.Reason);
    }

    [Fact]
    public void ThrowsADiagnosableErrorWhenThereIsNoJson()
    {
        ModelCallException error = Assert.Throws<ModelCallException>(() =>
            JsonPayload.Parse<Sample>("I'd rather not.", ModelCallKind.JudgeReply));

        Assert.Contains("JudgeReply", error.Message, StringComparison.Ordinal);
        Assert.Contains("I'd rather not.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatesLongExcerpts()
    {
        string excerpt = JsonPayload.Excerpt(new string('x', 500), limit: 40);

        Assert.Equal(41, excerpt.Length);
        Assert.True(excerpt.Length < 500);
    }
}

/// <summary>Design §2's rate-limit note — pacing calls to fit the free tier.</summary>
public class ModelThrottleTests
{
    [Fact]
    public void ComputesTheIntervalFromRequestsPerMinute()
    {
        using ModelThrottle throttle = new(requestsPerMinute: 12, maxConcurrency: 1);

        Assert.Equal(TimeSpan.FromSeconds(5), throttle.MinimumInterval);
    }

    [Fact]
    public async Task SpacesSequentialAcquisitions()
    {
        // 1200/min is a 50 ms interval, so three slots must span at least 100 ms.
        using ModelThrottle throttle = new(requestsPerMinute: 1200, maxConcurrency: 4);

        Stopwatch clock = Stopwatch.StartNew();
        for (int i = 0; i < 3; i++)
        {
            using ModelThrottle.Lease lease = await throttle.AcquireAsync();
        }

        clock.Stop();

        Assert.True(
            clock.ElapsedMilliseconds >= 90,
            $"three slots at a 50 ms interval took only {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task CapsConcurrencyUntilALeaseIsReleased()
    {
        using ModelThrottle throttle = new(requestsPerMinute: 60_000, maxConcurrency: 1);

        ModelThrottle.Lease first = await throttle.AcquireAsync();

        Task<ModelThrottle.Lease> second = throttle.AcquireAsync();
        Task completed = await Task.WhenAny(second, Task.Delay(150));

        Assert.NotSame(second, completed);

        first.Dispose();
        using ModelThrottle.Lease released = await second;
    }

    [Fact]
    public async Task PenaltyPushesTheNextSlotOut()
    {
        using ModelThrottle throttle = new(requestsPerMinute: 60_000, maxConcurrency: 4);

        await throttle.PenalizeAsync(TimeSpan.FromMilliseconds(150));

        Stopwatch clock = Stopwatch.StartNew();
        using (ModelThrottle.Lease lease = await throttle.AcquireAsync())
        {
            clock.Stop();
        }

        Assert.True(
            clock.ElapsedMilliseconds >= 120,
            $"a 150 ms penalty delayed the next slot by only {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void RejectsNonsensicalLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelThrottle(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelThrottle(10, 0));
    }
}

/// <summary>
/// The OpenAI-compatible client, exercised against a stub transport. This is the one
/// component that cannot be verified against the live backend without a token, so its
/// response handling and retry behaviour are pinned down here instead.
/// </summary>
public class OpenAiCompatibleModelClientTests
{
    /// <summary>
    /// Canned transport. Responses are supplied as factories because the client disposes each
    /// one, so a retry must be handed a fresh message rather than the disposed original.
    /// </summary>
    private sealed class StubHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _index;

        public List<string> RequestBodies { get; } = [];

        public List<Uri> RequestUris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return responses[Math.Min(_index++, responses.Length - 1)]();
        }
    }

    private static HttpResponseMessage Ok(
        string content, int promptTokens = 11, int completionTokens = 7) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  "choices": [{"message": {"role": "assistant", "content": {{JsonSerializer.Serialize(content)}}},
                               "finish_reason": "stop"}],
                  "usage": {"prompt_tokens": {{promptTokens}}, "completion_tokens": {{completionTokens}}}
                }
                """,
                Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage Error(HttpStatusCode status, string body = "{\"error\":\"nope\"}") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Raw(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ModelRequest Request() => new()
    {
        Kind = ModelCallKind.JudgeReply,
        SystemPrompt = "system",
        UserPrompt = "user",
        Seed = 5,
    };

    private static ModelBackendOptions Options() => new()
    {
        Endpoint = "https://models.github.ai/inference",
        Model = "openai/gpt-4o-mini",
        MaxAttempts = 3,
    };

    private static OpenAiCompatibleModelClient Client(
        StubHandler handler, ModelThrottle throttle, ModelBackendOptions? options = null) =>
        new(options ?? Options(), "token", throttle, new HttpClient(handler));

    [Fact]
    public async Task ExtractsTheAssistantMessageContent()
    {
        StubHandler handler = new(() => Ok("{\"favorable\": true}"));
        using ModelThrottle throttle = new(60_000, 1);
        using OpenAiCompatibleModelClient client = Client(handler, throttle);

        string result = await client.CallModelAsync(Request());

        Assert.Equal("{\"favorable\": true}", result);
    }

    [Fact]
    public async Task PostsToTheChatCompletionsPath()
    {
        StubHandler handler = new(() => Ok("{}"));
        using ModelThrottle throttle = new(60_000, 1);
        using OpenAiCompatibleModelClient client = Client(handler, throttle);

        await client.CallModelAsync(Request());

        Assert.Equal(
            "https://models.github.ai/inference/chat/completions",
            handler.RequestUris[0].ToString());
    }

    [Fact]
    public async Task DoesNotDoubleUpTheChatCompletionsPath()
    {
        StubHandler handler = new(() => Ok("{}"));
        using ModelThrottle throttle = new(60_000, 1);
        using OpenAiCompatibleModelClient client = Client(
            handler, throttle, Options() with { Endpoint = "https://example.invalid/v1/chat/completions" });

        await client.CallModelAsync(Request());

        Assert.Equal("https://example.invalid/v1/chat/completions", handler.RequestUris[0].ToString());
    }

    [Fact]
    public async Task SendsOnlyASystemAndUserMessageSoNoHistoryIsCarried()
    {
        StubHandler handler = new(() => Ok("{}"));
        using ModelThrottle throttle = new(60_000, 1);
        using OpenAiCompatibleModelClient client = Client(handler, throttle);

        await client.CallModelAsync(Request());

        string body = handler.RequestBodies[0];

        Assert.Contains("\"model\":\"openai/gpt-4o-mini\"", body, StringComparison.Ordinal);
        Assert.Contains("\"json_object\"", body, StringComparison.Ordinal);
        Assert.Contains("\"seed\":5", body, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(body, "\"role\":\"system\""));
        Assert.Equal(1, CountOccurrences(body, "\"role\":\"user\""));
        Assert.Equal(0, CountOccurrences(body, "\"role\":\"assistant\""));
    }

    [Fact]
    public async Task TracksTokenUsage()
    {
        StubHandler handler = new(() => Ok("{}", promptTokens: 30, completionTokens: 12));
        using ModelThrottle throttle = new(60_000, 1);
        using OpenAiCompatibleModelClient client = Client(handler, throttle);

        await client.CallModelAsync(Request());

        Assert.Equal(1, client.Usage.Calls);
        Assert.Equal(30, client.Usage.PromptTokens);
        Assert.Equal(42, client.Usage.TotalTokens);
    }

    [Fact]
    public async Task RetriesAfterRateLimitingAndSucceeds()
    {
        StubHandler handler = new(
            () => Error(HttpStatusCode.TooManyRequests, "Please wait 1 seconds before retrying"),
            () => Ok("{\"ok\":true}"));

        using ModelThrottle throttle = new(60_000, 1);
        using OpenAiCompatibleModelClient client = Client(handler, throttle);

        string result = await client.CallModelAsync(Request());

        Assert.Equal("{\"ok\":true}", result);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task GivesUpAfterTheAttemptLimitWithADiagnosableError()
    {
        StubHandler handler = new(() => Error(HttpStatusCode.ServiceUnavailable));

        using ModelThrottle throttle = new(60_000, 1);
        using OpenAiCompatibleModelClient client = Client(
            handler, throttle, Options() with { MaxAttempts = 2 });

        ModelCallException error =
            await Assert.ThrowsAsync<ModelCallException>(() => client.CallModelAsync(Request()));

        Assert.Equal(503, error.StatusCode);
        Assert.True(error.IsRetryable);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task DoesNotRetryANonRetryableStatus()
    {
        StubHandler handler = new(() => Error(HttpStatusCode.Unauthorized, "bad credentials"));

        using ModelThrottle throttle = new(60_000, 1);
        using OpenAiCompatibleModelClient client = Client(handler, throttle);

        ModelCallException error =
            await Assert.ThrowsAsync<ModelCallException>(() => client.CallModelAsync(Request()));

        Assert.Equal(401, error.StatusCode);
        Assert.False(error.IsRetryable);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public async Task SurfacesABackendContentFilterRatherThanReturningEmptyText()
    {
        StubHandler handler = new(() => Raw(
            "{\"choices\":[{\"message\":{\"content\":\"\"},\"finish_reason\":\"content_filter\"}]}"));

        using ModelThrottle throttle = new(60_000, 1);
        using OpenAiCompatibleModelClient client = Client(
            handler, throttle, Options() with { MaxAttempts = 1 });

        ModelCallException error =
            await Assert.ThrowsAsync<ModelCallException>(() => client.CallModelAsync(Request()));

        Assert.Contains("content_filter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAResponseWithNoChoices()
    {
        StubHandler handler = new(() => Raw("{\"choices\":[]}"));

        using ModelThrottle throttle = new(60_000, 1);
        using OpenAiCompatibleModelClient client = Client(
            handler, throttle, Options() with { MaxAttempts = 1 });

        await Assert.ThrowsAsync<ModelCallException>(() => client.CallModelAsync(Request()));
    }

    [Fact]
    public async Task RejectsAResponseWithEmptyContent()
    {
        StubHandler handler = new(() => Raw(
            "{\"choices\":[{\"message\":{\"content\":\"   \"},\"finish_reason\":\"stop\"}]}"));

        using ModelThrottle throttle = new(60_000, 1);
        using OpenAiCompatibleModelClient client = Client(
            handler, throttle, Options() with { MaxAttempts = 1 });

        await Assert.ThrowsAsync<ModelCallException>(() => client.CallModelAsync(Request()));
    }

    /// <summary>Usage accounting is best-effort and must not fail a call.</summary>
    [Fact]
    public async Task SucceedsWhenTheResponseOmitsUsage()
    {
        StubHandler handler = new(() => Raw(
            "{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}"));

        using ModelThrottle throttle = new(60_000, 1);
        using OpenAiCompatibleModelClient client = Client(handler, throttle);

        Assert.Equal("{}", await client.CallModelAsync(Request()));
        Assert.Equal(1, client.Usage.Calls);
        Assert.Equal(0, client.Usage.TotalTokens);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}

/// <summary>Backend key resolution.</summary>
public class ModelBackendOptionsTests
{
    [Fact]
    public void ReadsTheKeyFromAnExplicitVariable()
    {
        const string variable = "SWARMRT_TEST_KEY_EXPLICIT";
        Environment.SetEnvironmentVariable(variable, "abc123");
        try
        {
            (string? key, string tried) = new ModelBackendOptions { KeyVariable = variable }.ResolveKey();

            Assert.Equal("abc123", key);
            Assert.Equal(variable, tried);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void ReportsTheVariablesTriedWhenNoKeyIsSet()
    {
        (string? key, string tried) = new ModelBackendOptions
        {
            KeyVariable = "SWARMRT_TEST_KEY_ABSENT",
        }.ResolveKey();

        Assert.Null(key);
        Assert.Equal("SWARMRT_TEST_KEY_ABSENT", tried);
    }

    [Fact]
    public void DefaultsPointAtGitHubModels()
    {
        ModelBackendOptions options = new();

        Assert.Equal("https://models.github.ai/inference", options.Endpoint);
        Assert.Equal("openai/gpt-4o-mini", options.Model);
        Assert.Contains("GITHUB_TOKEN", ModelBackendOptions.DefaultKeyVariables);
    }
}
