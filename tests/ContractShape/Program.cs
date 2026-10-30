using System.Text.Json;
using STS2_MCP.Core.Contracts;

// Pins the JSON wire format the transport emits, using the shipped contract types and the shipped
// serializer options. No game, no Godot: this runs anywhere `dotnet run` does.
int checks = 0;

void Check(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException("FAILED: " + name);
    checks++;
}

void CheckWire(string expected, object value, string name, bool pretty = false)
{
    string actual = JsonSerializer.Serialize(value, pretty ? ContractJson.PrettyOptions : ContractJson.Options);
    Check(string.Equals(expected, actual, StringComparison.Ordinal), $"{name}: expected {expected}, got {actual}");
}

// The documented failure body. The code is omitted, not written as null, when there is none.
CheckWire(
    """{"status":"error","error":"Boom"}""",
    new ErrorResponse { Error = "Boom" },
    "error body without a code");
CheckWire(
    """{"status":"error","error":"Boom","error_code":"bad_request"}""",
    new ErrorResponse { Error = "Boom", ErrorCode = "bad_request" },
    "error body with a code");

// Action-level failures are built as a dictionary today; the contract type must stay byte-identical
// to that shape, so a client cannot tell the two paths apart.
var actionError = new Dictionary<string, object?>(StringComparer.Ordinal)
{
    ["status"] = "error",
    ["error"] = "Boom",
    ["error_code"] = "action_error"
};
Check(
    string.Equals(
        JsonSerializer.Serialize(actionError, ContractJson.Options),
        JsonSerializer.Serialize(new ErrorResponse { Error = "Boom", ErrorCode = "action_error" }, ContractJson.Options),
        StringComparison.Ordinal),
    "action-level and transport-level failures serialize identically");

// The 503 body has its own key order (error first) and always says the request may be retried.
CheckWire(
    """{"error":"busy","status":"error","retry":true}""",
    new UnavailableResponse { Error = "busy" },
    "unavailable body");

// ?pretty=1 changes whitespace only: same keys, same order. System.Text.Json always wraps with
// "\n", so this expectation is portable across platforms.
CheckWire(
    "{\n  \"status\": \"error\",\n  \"error\": \"Boom\"\n}",
    new ErrorResponse { Error = "Boom" },
    "pretty error body",
    pretty: true);

// C# members stay PascalCase and the naming policy does the snake_case conversion.
CheckWire("""{"max_select":2}""", new { MaxSelect = 2 }, "snake_case property names");

// UnsafeRelaxedJsonEscaping: the payload is read by an MCP client, so <, > and & stay literal.
CheckWire("""{"note":"a<b>&c"}""", new { Note = "a<b>&c" }, "html characters are not escaped");

// A null *property* is dropped, while a null *dictionary value* is kept. The root endpoint depends
// on the second half: "commit" must stay present (as null) for clients that read it blindly.
CheckWire("""{"status":"ok"}""", new { Status = "ok", Commit = (string?)null }, "null properties are omitted");
var bag = new Dictionary<string, object?>(StringComparer.Ordinal)
{
    ["commit"] = null,
    ["status"] = "ok"
};
CheckWire("""{"commit":null,"status":"ok"}""", bag, "null dictionary values are kept");

Console.WriteLine($"PASS: {checks} contract wire checks (no game dependencies)");
