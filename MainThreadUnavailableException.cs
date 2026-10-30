using System;

namespace STS2_MCP;

/// <summary>
/// The game's main thread did not run the requested work: the queue was already backed up, or
/// the wait timed out. Distinct from an action failure so handlers can answer 503 + retry
/// instead of 500 - nothing was executed, and the same request is safe to send again.
/// </summary>
public sealed class MainThreadUnavailableException : Exception
{
    public MainThreadUnavailableException() { }

    public MainThreadUnavailableException(string message) : base(message) { }

    public MainThreadUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
