using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmRT.Contracts;

namespace SwarmRT.Logging;

/// <summary>
/// Design §3.6 — the append-only writer the orchestrator owns. One result object per
/// line, verbatim, in the order attempts completed.
/// <para>
/// The file is opened in append mode and never seeked, so prior lines cannot be
/// rewritten even by mistake. Alongside the file the logger maintains a rolling
/// SHA-256 chain over the bytes written; the final digest goes into the run manifest,
/// which makes later edits to the log detectable without adding fields to the log
/// contract that design §5.3 fixes.
/// </para>
/// </summary>
public sealed class JsonlAttemptLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private byte[] _chain = new byte[32];
    private bool _disposed;

    public JsonlAttemptLogger(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Path = System.IO.Path.GetFullPath(path);
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        FileStream stream = new(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    public string Path { get; }

    public int LineCount { get; private set; }

    /// <summary>Hex SHA-256 chain over every line written by this logger instance.</summary>
    public string Digest => Convert.ToHexString(_chain).ToLowerInvariant();

    /// <summary>
    /// Validates the result against the design §5.2 contract, then appends it. A result
    /// that fails validation is refused rather than written, so the log cannot contain a
    /// row that contradicts the schema the reports depend on.
    /// </summary>
    public void Append(AttemptResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ObjectDisposedException.ThrowIf(_disposed, this);

        result.EnsureValid();

        string line = JsonSerializer.Serialize(result, SwarmJson.Line);
        if (line.Contains('\n', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Serialized result for '{result.AttemptId}' contains a newline and would corrupt the JSONL log.");
        }

        _writer.Write(line);
        _writer.Write('\n');

        LineCount++;
        AdvanceChain(line);
    }

    private void AdvanceChain(string line)
    {
        byte[] lineBytes = Encoding.UTF8.GetBytes(line);
        byte[] combined = new byte[_chain.Length + lineBytes.Length];
        _chain.CopyTo(combined, 0);
        lineBytes.CopyTo(combined, _chain.Length);
        _chain = SHA256.HashData(combined);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _writer.Dispose();
        _disposed = true;
    }
}

/// <summary>Reads a JSONL attempt log back for reporting.</summary>
public static class AttemptLogReader
{
    /// <summary>
    /// Parses every line, failing loudly on a malformed one: a report built from a log
    /// with silently skipped rows would understate the engagement.
    /// </summary>
    public static IReadOnlyList<AttemptResult> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Attempt log not found at '{path}'.", path);
        }

        List<AttemptResult> results = [];
        int lineNumber = 0;

        foreach (string raw in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            AttemptResult result;
            try
            {
                result = JsonSerializer.Deserialize<AttemptResult>(raw, SwarmJson.Reading)
                         ?? throw new InvalidDataException($"{path}:{lineNumber} deserialized to null.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"{path}:{lineNumber} is not a valid attempt result: {ex.Message}", ex);
            }

            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Recomputes the logger's rolling digest over an existing file, so a manifest digest
    /// can be checked against the log it describes.
    /// </summary>
    public static string ComputeDigest(string path)
    {
        byte[] chain = new byte[32];

        foreach (string raw in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            byte[] lineBytes = Encoding.UTF8.GetBytes(raw);
            byte[] combined = new byte[chain.Length + lineBytes.Length];
            chain.CopyTo(combined, 0);
            lineBytes.CopyTo(combined, chain.Length);
            chain = SHA256.HashData(combined);
        }

        return Convert.ToHexString(chain).ToLowerInvariant();
    }
}
