namespace Sts2Mcp.Core.Contracts;

/// <summary>
/// The 503 for "the game's main thread never picked this up". <c>retry: true</c> is the contract:
/// the request was not executed, so a client may safely send it again - unlike a 500, where the
/// action ran and failed.
/// </summary>
/// <remarks>
/// The key order (error, status, retry) is the order this body has always been written in, and is
/// pinned by <c>tests/ContractShape</c>; it is not the same order as <see cref="ErrorResponse"/>.
/// The contract itself is documented under "Unavailable Response (HTTP 503)" in the API reference.
/// </remarks>
internal sealed record UnavailableResponse
{
    /// <summary>The reason the request was not executed.</summary>
    public required string Error { get; init; }

    /// <summary>Always <c>"error"</c>.</summary>
    public string Status { get; } = "error";

    /// <summary>Always <c>true</c>: nothing ran, so retrying is safe.</summary>
    public bool Retry { get; } = true;
}
