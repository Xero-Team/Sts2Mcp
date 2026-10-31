using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Mcp.Core.Contracts;

/// <summary>
/// The single definition of the JSON wire format: snake_case names, absent optional fields, and no
/// HTML-safety escaping (a localhost MCP client reads this, not a browser).
/// </summary>
/// <remarks>
/// Two immutable instances, because <see cref="JsonSerializerOptions"/> must not be mutated after
/// first use and requests are served concurrently on ThreadPool threads. They live next to the
/// contract types rather than in the transport so a test can pin the format without the game:
/// <c>tests/ContractShape</c> serializes every contract with exactly these options.
///
/// Compact is the default because indentation was ~32% of a typical in-combat state response
/// (12,069 -> 8,178 bytes) and the main consumer is an LLM that pays per token; <c>?pretty=1</c>
/// opts back in through <see cref="PrettyOptions"/>.
/// </remarks>
internal static class ContractJson
{
    /// <summary>Compact output, the default for every response.</summary>
    internal static JsonSerializerOptions Options { get; } = Create(indented: false);

    /// <summary>The <c>?pretty=1</c> variant: same keys, same order, indented.</summary>
    internal static JsonSerializerOptions PrettyOptions { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
