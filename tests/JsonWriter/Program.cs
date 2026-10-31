using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sts2Mcp;

namespace Sts2Mcp.Tests;

// Compares StateJsonWriter against JsonSerializer over the shapes the mod's state builders
// produce. Any difference in bytes is a behavioral change for every client, so the harness
// treats the first mismatch as a failure.

internal static class Program
{
    private static int _failures;

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static int Main()
    {
        foreach (var (name, value) in Cases())
        {
            Compare(name, value, Compact);
            Compare(name, value, Pretty);
        }

        CompareDigest();

        Console.WriteLine(_failures == 0
            ? "all cases byte-identical"
            : $"{_failures} case(s) differ");
        return _failures == 0 ? 0 : 1;
    }

    private static void Compare(string name, object? value, JsonSerializerOptions options)
    {
        string expected = JsonSerializer.Serialize(value, options);

        using var buffer = new PooledJsonBuffer();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = options.WriteIndented,
                   Encoder = options.Encoder
               }))
        {
            StateJsonWriter.Write(writer, value, options);
        }

        string actual = Encoding.UTF8.GetString(buffer.WrittenSpan);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            _failures++;
            Console.WriteLine($"MISMATCH {name}{(options.WriteIndented ? " (pretty)" : "")}");
            Console.WriteLine($"  expected: {expected}");
            Console.WriteLine($"  actual  : {actual}");
        }

        // The writer must also agree with itself on the digest (same tree, same digest) and
        // disagree when a single leaf changes - that is what the poll cache keys on.
        if (options.WriteIndented)
            return;

        ulong first = StateJsonWriter.Digest(value);
        ulong second = StateJsonWriter.Digest(value);
        if (first != second)
        {
            _failures++;
            Console.WriteLine($"UNSTABLE DIGEST {name}");
        }
    }

    private static readonly string[] StrikeTargets = { "ENEMY_0", "ENEMY_1" };
    private static readonly string[] TwoStrings = { "a", "b", "" };
    private static readonly bool[] Bools = { true, false };

    private static IEnumerable<(string Name, object? Value)> Cases()
    {
        foreach (var item in TreeCases())
            yield return item;
        foreach (var item in ScalarCases())
            yield return item;
    }

    private static IEnumerable<(string Name, object? Value)> TreeCases()
    {
        yield return ("empty dict", new Dictionary<string, object?>(StringComparer.Ordinal));

        yield return ("state tree", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["state_type"] = "monster",
            ["schema_version"] = 3,
            ["null_value"] = null,
            ["battle"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["round"] = 4,
                ["turn"] = "player",
                ["is_play_phase"] = true,
                ["enemies"] = new List<Dictionary<string, object?>>
                {
                    new(StringComparer.Ordinal)
                    {
                        ["entity_id"] = "KIN_PRIEST_0",
                        ["hp"] = 41,
                        ["block"] = 0,
                        ["powers"] = new List<Dictionary<string, object?>>()
                    }
                }
            },
            ["hand"] = new List<object?>
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = "STRIKE",
                    ["damage"] = 6,
                    ["targets"] = StrikeTargets
                },
                null
            },
            ["warnings"] = new[] { "text in CardDescription: NullReferenceException" },
            ["empty_list"] = new List<object?>(),
            ["empty_array"] = Array.Empty<string>()
        });

        yield return ("list of lists", new List<object?>
        {
            new List<object?> { 1, "two", null },
            Bools
        });

        yield return ("string array", TwoStrings);
    }

    private static IEnumerable<(string Name, object? Value)> ScalarCases()
    {

        yield return ("numbers", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["int"] = 42,
            ["int_min"] = int.MinValue,
            ["long"] = 9007199254740993L,
            ["long_min"] = long.MinValue,
            ["uint"] = uint.MaxValue,
            ["ulong"] = ulong.MaxValue,
            ["short"] = (short)-7,
            ["ushort"] = (ushort)7,
            ["byte"] = (byte)200,
            ["sbyte"] = (sbyte)-100,
            ["double_whole"] = 6d,
            ["double_fraction"] = 0.1d,
            ["double_roundtrip"] = 1d / 3d,
            ["double_exp"] = 1e-9d,
            ["float"] = 1.5f,
            ["decimal"] = 12.34m,
            ["decimal_trailing_zero"] = 1.10m
        });

        yield return ("strings", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["plain"] = "Deal 6 damage.",
            ["quotes"] = "a \"b\" c",
            ["backslash"] = "a\\b",
            ["newline"] = "line1\nline2\u0001",
            ["unicode"] = "腐化之刃 — ✨",
            ["html"] = "<tag> & 'quote'",
            ["empty"] = "",
            ["null"] = null
        });

        yield return ("scalars and typed values", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["bool"] = true,
            ["char"] = 'x',
            ["guid"] = Guid.Parse("d3c1a5f0-1111-4f2e-9d3b-1234567890ab"),
            ["date"] = new DateTime(2026, 8, 13, 17, 39, 18, DateTimeKind.Utc),
            ["date_offset"] = new DateTimeOffset(2026, 8, 13, 17, 39, 18, TimeSpan.FromHours(-7)),
            ["bytes"] = new byte[] { 1, 2, 250 },
            ["element"] = JsonDocument.Parse("""{"a":[1,2,{"b":null}],"c":"d"}""").RootElement,
            ["null_element"] = JsonDocument.Parse("null").RootElement
        });
    }

    private static void CompareDigest()
    {
        var baseline = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["hp"] = 41,
            ["hand"] = new List<object?> { "STRIKE", "DEFEND" }
        };

        var same = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["hp"] = 41,
            ["hand"] = new List<object?> { "STRIKE", "DEFEND" }
        };

        var differentNumber = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["hp"] = 42,
            ["hand"] = new List<object?> { "STRIKE", "DEFEND" }
        };

        var differentText = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["hp"] = 41,
            ["hand"] = new List<object?> { "STRIKE", "DEFENDS" }
        };

        Expect(StateJsonWriter.Digest(baseline) == StateJsonWriter.Digest(same), "digest is stable for equal trees");
        Expect(StateJsonWriter.Digest(baseline) != StateJsonWriter.Digest(differentNumber), "digest reacts to a changed number");
        Expect(StateJsonWriter.Digest(baseline) != StateJsonWriter.Digest(differentText), "digest reacts to a changed string");
        Expect(StateJsonWriter.Digest(baseline) != StateJsonWriter.Digest(null), "digest reacts to null");
    }

    private static void Expect(bool condition, string what)
    {
        if (condition)
            return;

        _failures++;
        Console.WriteLine($"FAILED {what}");
    }
}
