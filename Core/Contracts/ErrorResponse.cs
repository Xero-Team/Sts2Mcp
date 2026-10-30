namespace STS2_MCP.Core.Contracts;

/// <summary>
/// The documented failure body: <c>{"status": "error", "error": "&lt;human message&gt;"}</c>, plus a
/// stable code when the call site has one.
/// </summary>
/// <remarks>
/// The transport boundary (<c>SendError</c>) and the action dispatch (<c>Error()</c>) emit this
/// same shape, so a client never has to branch on the HTTP status to understand a failure. The
/// property order - status, error, error_code - is part of the pinned wire format documented under
/// "Error Response" in <c>docs/raw-full.md</c>; see <c>tests/ContractShape</c>. An absent code is omitted rather than written as null, which is why
/// a blank code must be normalised to null by the caller.
/// </remarks>
internal sealed record ErrorResponse
{
    /// <summary>Always <c>"error"</c>.</summary>
    public string Status { get; } = "error";

    /// <summary>The human-readable reason, unchanged from the failing call site.</summary>
    public required string Error { get; init; }

    /// <summary>A code a client can branch on; omitted when the call site has none.</summary>
    public string? ErrorCode { get; init; }
}
