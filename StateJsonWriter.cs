using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Sts2Mcp;

/// <summary>
/// Writes the mod's response trees (nested <c>Dictionary&lt;string, object?&gt;</c>, lists, and
/// scalars) straight to a <see cref="Utf8JsonWriter"/>.
///
/// The state builders hand back an untyped tree, so serializing it went through
/// <c>JsonSerializer</c>'s reflection path — the slowest shape it supports — and produced a
/// string that was then copied into a second byte array. Walking the same tree here emits the
/// exact bytes <c>JsonSerializer</c> would (same options: snake_case for typed members,
/// <c>WhenWritingNull</c> for dictionary entries, relaxed escaping) without either copy.
/// Anything that is not a concrete shape below is delegated to <c>JsonSerializer</c>, so a new
/// value type in the state keeps working and only loses the fast path.
/// </summary>
internal static class StateJsonWriter
{
    internal static void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (TryWriteLeaf(writer, value))
            return;

        switch (value)
        {
            case JsonElement element:
                element.WriteTo(writer);
                return;

            case IReadOnlyDictionary<string, object?> map:
                WriteObject(writer, map, options);
                return;

            case IEnumerable sequence:
                writer.WriteStartArray();
                foreach (object? item in sequence)
                {
                    Write(writer, item, options);
                }
                writer.WriteEndArray();
                return;

            default:
                // Typed values (records, enums, DTOs) keep the serializer's own handling.
                JsonSerializer.Serialize(writer, value, value.GetType(), options);
                return;
        }
    }

    /// <summary>Writes a scalar/leaf and reports whether it handled the value.</summary>
    private static bool TryWriteLeaf(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case string text: writer.WriteStringValue(text); return true;
            case bool flag: writer.WriteBooleanValue(flag); return true;
            case int number: writer.WriteNumberValue(number); return true;
            case long number: writer.WriteNumberValue(number); return true;
            case double number: writer.WriteNumberValue(number); return true;
            case float number: writer.WriteNumberValue(number); return true;
            case decimal number: writer.WriteNumberValue(number); return true;
            case uint number: writer.WriteNumberValue(number); return true;
            case ulong number: writer.WriteNumberValue(number); return true;
            case short number: writer.WriteNumberValue(number); return true;
            case ushort number: writer.WriteNumberValue(number); return true;
            case byte number: writer.WriteNumberValue(number); return true;
            case sbyte number: writer.WriteNumberValue(number); return true;
            case char character: WriteChar(writer, character); return true;
            case DateTime timestamp: writer.WriteStringValue(timestamp); return true;
            case DateTimeOffset timestamp: writer.WriteStringValue(timestamp); return true;
            case Guid guid: writer.WriteStringValue(guid); return true;
            case byte[] bytes: writer.WriteBase64StringValue(bytes); return true;
            default: return false;
        }
    }

    private static void WriteChar(Utf8JsonWriter writer, char character)
    {
        Span<char> one = stackalloc char[1];
        one[0] = character;
        writer.WriteStringValue(one);
    }

    private static void WriteObject(
        Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> entries, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (KeyValuePair<string, object?> entry in entries)
        {
            // A null dictionary value is written as JSON null. DefaultIgnoreCondition =
            // WhenWritingNull governs typed properties, not dictionary entries, and the harness
            // in tests/JsonWriter pins that: omitting the key here would change the contract
            // every client already reads.
            writer.WritePropertyName(entry.Key);
            Write(writer, entry.Value, options);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// A 64-bit structural digest (FNV-1a) of a response tree.
    ///
    /// Used to answer "did anything actually change?" without serializing: two trees produce the
    /// same digest only when the same keys, in the same order, hold the same values. Cheap enough
    /// to run on every poll and exact enough that a changed value cannot hide behind it.
    /// </summary>
    internal static ulong Digest(object? value)
    {
        ulong hash = 14695981039346656037UL;
        AppendDigest(ref hash, value);
        return hash;
    }

    private static void AppendDigest(ref ulong hash, object? value)
    {
        switch (value)
        {
            case null:
                Mix(ref hash, "n");
                return;

            case string text:
                Mix(ref hash, "s");
                Mix(ref hash, text);
                return;

            case bool flag:
                Mix(ref hash, flag ? "b1" : "b0");
                return;

            case IReadOnlyDictionary<string, object?> map:
                Mix(ref hash, "d");
                foreach (KeyValuePair<string, object?> entry in map)
                {
                    Mix(ref hash, "k");
                    Mix(ref hash, entry.Key);
                    AppendDigest(ref hash, entry.Value);
                }

                return;

            case IEnumerable sequence:
                Mix(ref hash, "a");
                foreach (object? item in sequence)
                {
                    AppendDigest(ref hash, item);
                }
                return;

            case JsonElement element:
                Mix(ref hash, "j");
                Mix(ref hash, element.GetRawText());
                return;

            default:
                Mix(ref hash, "v");
                Mix(ref hash, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                return;
        }
    }

    private static void Mix(ref ulong hash, string text)
    {
        foreach (char character in text)
        {
            hash ^= (byte)character;
            hash *= 1099511628211UL;
            hash ^= (byte)(character >> 8);
            hash *= 1099511628211UL;
        }

        // Separator so ("ab","c") and ("a","bc") cannot collide.
        hash ^= 0xFF;
        hash *= 1099511628211UL;
    }
}
