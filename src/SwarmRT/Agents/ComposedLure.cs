namespace SwarmRT.Agents;

/// <summary>
/// One agent's single composed lure.
/// <para>
/// The simulated message is the only artefact in the system that resembles attack
/// content, so it is treated as radioactive: it lives in a buffer this object owns,
/// it is never handed to the logger, and <see cref="Dispose"/> zeroes it. Only
/// <see cref="AttemptSummary"/> — a pretext-level description — is allowed to reach
/// disk (design §8.3).
/// </para>
/// </summary>
public sealed class ComposedLure : IDisposable
{
    private char[] _message;
    private bool _disposed;

    public ComposedLure(string simulatedMessage, string attemptSummary, string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(simulatedMessage);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptSummary);

        _message = simulatedMessage.ToCharArray();
        MessageLength = _message.Length;
        AttemptSummary = attemptSummary.Trim();
        Channel = channel;
    }

    /// <summary>Pretext-level description of the approach. Safe to log.</summary>
    public string AttemptSummary { get; }

    /// <summary>Simulated delivery channel, from the pretext definition.</summary>
    public string Channel { get; }

    public int MessageLength { get; }

    public bool IsScrubbed => _disposed;

    /// <summary>
    /// Materialises the message for the safety gate and the synthetic responder.
    /// Throws once scrubbed, so a use-after-wipe is a crash rather than silent
    /// leakage of a stale buffer.
    /// </summary>
    public string Reveal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new string(_message);
    }

    /// <summary>
    /// Zeroes the buffer this object owns. Transient copies handed to callers are
    /// left to the garbage collector; what is guaranteed is that the agent retains
    /// nothing after disposal.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Array.Clear(_message);
        _message = [];
        _disposed = true;
    }
}
