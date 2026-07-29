using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwarmRT.Model;

/// <summary>Where to reach the backend and how to authenticate.</summary>
public sealed record ModelBackendOptions
{
    /// <summary>GitHub Models' OpenAI-compatible inference root (design §7).</summary>
    public const string GitHubModelsEndpoint = "https://models.github.ai/inference";

    public const string DefaultModel = "openai/gpt-4o-mini";

    /// <summary>Environment variables checked in order for the API key.</summary>
    public static readonly IReadOnlyList<string> DefaultKeyVariables =
        ["SWARMRT_API_KEY", "GITHUB_MODELS_TOKEN", "GITHUB_TOKEN", "GH_TOKEN"];

    public string Endpoint { get; init; } = GitHubModelsEndpoint;

    public string Model { get; init; } = DefaultModel;

    /// <summary>Explicit key variable to read; when null the defaults are tried in order.</summary>
    public string? KeyVariable { get; init; }

    public string DisplayName { get; init; } = "GitHub Models";

    public int RequestsPerMinute { get; init; } = 10;

    public int MaxConcurrency { get; init; } = 1;

    public int MaxAttempts { get; init; } = 4;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Reads the key from the environment, or returns null with the variables tried.</summary>
    public (string? Key, string VariableUsed) ResolveKey()
    {
        IEnumerable<string> candidates = KeyVariable is null
            ? DefaultKeyVariables
            : [KeyVariable];

        foreach (string variable in candidates)
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return (value.Trim(), variable);
            }
        }

        return (null, string.Join(", ", candidates));
    }
}

/// <summary>
/// Talks to any OpenAI-compatible <c>/chat/completions</c> endpoint. Each call is a
/// standalone request carrying only a system prompt and one user message, so
/// statelessness is a property of the transport rather than something to enforce
/// (design §2).
/// </summary>
public sealed class OpenAiCompatibleModelClient : IModelClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly ModelBackendOptions _options;
    private readonly ModelThrottle _throttle;
    private readonly bool _ownsHttp;
    private readonly Random _jitter;
    private readonly Lock _usageLock = new();
    private ModelUsage _usage = ModelUsage.Zero;

    public OpenAiCompatibleModelClient(
        ModelBackendOptions options,
        string apiKey,
        ModelThrottle throttle,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(throttle);

        _options = options;
        _throttle = throttle;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.Timeout = options.Timeout;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SwarmRT/1.0");
        _jitter = new Random(20260729);
    }

    public string Description => $"{_options.DisplayName} / {_options.Model}";

    public ModelUsage Usage
    {
        get
        {
            lock (_usageLock)
            {
                return _usage;
            }
        }
    }

    public async Task<string> CallModelAsync(
        ModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Uri uri = BuildChatCompletionsUri(_options.Endpoint);
        ModelCallException? last = null;

        for (int attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            using ModelThrottle.Lease lease = await _throttle.AcquireAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                return await SendOnceAsync(uri, request, cancellationToken).ConfigureAwait(false);
            }
            catch (ModelCallException ex) when (ex.IsRetryable && attempt < _options.MaxAttempts)
            {
                last = ex;
                TimeSpan backoff = BackoffFor(attempt, ex);
                await _throttle.PenalizeAsync(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < _options.MaxAttempts)
            {
                last = new ModelCallException($"Transport failure calling {uri}: {ex.Message}", ex);
                await _throttle.PenalizeAsync(BackoffFor(attempt, null), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested
                                                   && attempt < _options.MaxAttempts)
            {
                last = new ModelCallException($"Request to {uri} timed out.", ex);
                await _throttle.PenalizeAsync(BackoffFor(attempt, null), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw last ?? new ModelCallException($"Model call for {request.Kind} failed with no diagnostic.");
    }

    private async Task<string> SendOnceAsync(
        Uri uri, ModelRequest request, CancellationToken cancellationToken)
    {
        JsonObject body = new()
        {
            ["model"] = _options.Model,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxOutputTokens,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt },
            },
        };

        if (request.ExpectJson)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        if (request.Seed is { } seed)
        {
            body["seed"] = seed;
        }

        using HttpResponseMessage response = await _http
            .PostAsJsonAsync(uri, body, cancellationToken)
            .ConfigureAwait(false);

        string payload = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new ModelCallException(
                $"{(int)response.StatusCode} {response.ReasonPhrase} from {uri}: {JsonPayload.Excerpt(payload)}")
            {
                StatusCode = (int)response.StatusCode,
            };
        }

        RecordUsage(payload);
        return ExtractContent(payload);
    }

    private static Uri BuildChatCompletionsUri(string endpoint)
    {
        string trimmed = endpoint.TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed);
        }

        return new Uri($"{trimmed}/chat/completions");
    }

    private static string ExtractContent(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);

        if (!document.RootElement.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new ModelCallException(
                $"Response had no choices array: {JsonPayload.Excerpt(payload)}");
        }

        JsonElement first = choices[0];

        if (first.TryGetProperty("finish_reason", out JsonElement finish)
            && finish.ValueKind == JsonValueKind.String
            && finish.GetString() is "content_filter")
        {
            throw new ModelCallException("Backend refused the request with finish_reason 'content_filter'.")
            {
                StatusCode = 422,
            };
        }

        if (!first.TryGetProperty("message", out JsonElement message)
            || !message.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.String)
        {
            throw new ModelCallException(
                $"Response choice had no message content: {JsonPayload.Excerpt(payload)}");
        }

        string? text = content.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ModelCallException("Backend returned empty content.");
        }

        return text;
    }

    private void RecordUsage(string payload)
    {
        int prompt = 0;
        int completion = 0;

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("usage", out JsonElement usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out JsonElement p) &&
                    p.ValueKind == JsonValueKind.Number)
                {
                    prompt = p.GetInt32();
                }

                if (usage.TryGetProperty("completion_tokens", out JsonElement c) &&
                    c.ValueKind == JsonValueKind.Number)
                {
                    completion = c.GetInt32();
                }
            }
        }
        catch (JsonException)
        {
            // Usage accounting is best-effort; a missing block must not fail the call.
        }

        lock (_usageLock)
        {
            _usage = new ModelUsage(
                _usage.Calls + 1,
                _usage.PromptTokens + prompt,
                _usage.CompletionTokens + completion);
        }
    }

    /// <summary>
    /// Honours <c>Retry-After</c> when the backend sends one, otherwise backs off
    /// exponentially from two seconds with jitter to avoid a synchronised retry.
    /// </summary>
    private TimeSpan BackoffFor(int attempt, ModelCallException? failure)
    {
        if (failure?.StatusCode == (int)HttpStatusCode.TooManyRequests
            && TryReadRetryAfterSeconds(failure.Message, out double seconds))
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, 120));
        }

        double baseSeconds = 2.0 * Math.Pow(2, attempt - 1);
        double jittered = baseSeconds + _jitter.NextDouble();
        return TimeSpan.FromSeconds(Math.Min(jittered, 60));
    }

    /// <summary>
    /// GitHub Models reports the wait inside the error body rather than a header on
    /// some responses, e.g. "Please wait 27 seconds before retrying".
    /// </summary>
    private static bool TryReadRetryAfterSeconds(string message, out double seconds)
    {
        seconds = 0;
        int index = message.IndexOf("wait ", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        string tail = message[(index + 5)..];
        string digits = new(tail.TakeWhile(char.IsAsciiDigit).ToArray());
        return digits.Length > 0
               && double.TryParse(digits, CultureInfo.InvariantCulture, out seconds);
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
