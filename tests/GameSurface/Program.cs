using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sts2Mcp.Tests.GameSurface;

/// <summary>
/// One entry of tests/GameSurface/game-surface.json: a literal member name the mod
/// reaches through reflection or a string Harmony target.
/// </summary>
internal sealed record Binding(
    string File,
    int Line,
    string Api,
    string Member,
    string Kind,
    string Scope,
    string? Type,
    int Arity,
    bool Required,
    string? External)
{
    public string Describe() => Type is null ? Member : $"{Type}.{Member}";
}

/// <summary>
/// What a resolved type declares. Members are compared by name only: the mod's
/// reflection calls do not pin a signature, so a renamed member is the break this
/// check can actually catch.
/// </summary>
internal sealed class TypeMembers
{
    public required string FullName { get; init; }
    public string? BaseName { get; init; }
    public HashSet<string> Fields { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Properties { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Methods { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Events { get; } = new(StringComparer.Ordinal);

    /// <summary>Method name to declared arities, for Harmony constructor targets.</summary>
    public Dictionary<string, HashSet<int>> MethodArities { get; } = new(StringComparer.Ordinal);

    public bool Declares(string kind, string member, TypeMembers? model = null)
    {
        for (TypeMembers? type = model ?? this; type != null; type = type.Base)
        {
            if (kind switch
            {
                "field" => type.Fields.Contains(member),
                "property" => type.Properties.Contains(member),
                "method" => type.Methods.Contains(member),
                "event" => type.Events.Contains(member),
                "property-or-field" => type.Properties.Contains(member) || type.Fields.Contains(member),
                _ => false,
            })
            {
                return true;
            }
        }

        return false;
    }

    public TypeMembers? Base { get; set; }
}

internal static class Program
{
    private const string AssemblyName = "sts2.dll";

    private static int Main(string[] args)
    {
        string? dllPath = ResolveDllPath(args);
        if (dllPath is null)
        {
            Console.WriteLine($"SKIP GameSurface: {AssemblyName} not found (no game path configured); nothing to check.");
            return 0;
        }

        string manifestPath = Path.Combine(AppContext.BaseDirectory, "game-surface.json");
        if (!File.Exists(manifestPath))
        {
            Console.WriteLine($"FAIL GameSurface: manifest missing at {manifestPath}");
            return 1;
        }

        List<Binding> bindings = LoadManifest(manifestPath);
        Dictionary<string, TypeMembers> types = ReadTypes(dllPath);
        string sha256 = Sha256(dllPath);

        int failures = 0;
        int optionalAbsent = 0;
        foreach (Binding binding in bindings)
        {
            string location = $"{binding.File}:{binding.Line} {binding.Api} {binding.Describe()} ({binding.Kind})";
            if (binding.External is not null)
            {
                // A BCL type: real, but not part of the shipped game assembly this test grades.
                Console.WriteLine($"skip {location} - external {binding.External} member, not in {AssemblyName}");
                continue;
            }

            string? failure = Resolve(binding, types);
            if (failure is null)
            {
                Console.WriteLine($"ok   {location}");
                continue;
            }

            if (!binding.Required)
            {
                Console.WriteLine($"ok   {location} - absent, optional read with a fallback");
                optionalAbsent++;
                continue;
            }

            Console.WriteLine(
                $"FAIL {location} - {failure} in this build ({AssemblyName} sha256 {sha256[..12]})");
            failures++;
        }

        if (failures > 0)
        {
            Console.WriteLine($"{failures} of {bindings.Count} required game bindings missing from {dllPath}");
            return 1;
        }

        Console.WriteLine(
            $"ALL OK ({bindings.Count} game bindings checked against {AssemblyName} sha256 {sha256}; " +
            $"{optionalAbsent} optional absent)");
        return 0;
    }

    /// <summary>
    /// Argument, then STS2_GAME_DATA_DIR, then STS2_GAME_DIR + the platform folder.
    /// </summary>
    private static string? ResolveDllPath(string[] args)
    {
        var candidates = new List<string>();
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            candidates.Add(args[0]);
        }

        string? dataDir = Environment.GetEnvironmentVariable("STS2_GAME_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            candidates.Add(dataDir);
        }

        string? gameDir = Environment.GetEnvironmentVariable("STS2_GAME_DIR");
        if (!string.IsNullOrWhiteSpace(gameDir))
        {
            candidates.Add(Path.Combine(gameDir, PlatformDataDirectory()));
        }

        foreach (string candidate in candidates)
        {
            string path = Directory.Exists(candidate) ? Path.Combine(candidate, AssemblyName) : candidate;
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string PlatformDataDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "data_sts2_windows_x86_64";
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? Path.Combine("SlayTheSpire2.app", "Contents", "Resources", "data_sts2_macos_arm64")
            : "data_sts2_linuxbsd_x86_64";
    }

    private static List<Binding> LoadManifest(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        var bindings = new List<Binding>();
        foreach (JsonElement entry in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            bindings.Add(new Binding(
                entry.GetProperty("file").GetString()!,
                entry.GetProperty("line").GetInt32(),
                entry.GetProperty("api").GetString()!,
                entry.GetProperty("member").GetString()!,
                entry.GetProperty("kind").GetString()!,
                entry.GetProperty("scope").GetString()!,
                entry.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String
                    ? type.GetString()
                    : null,
                entry.TryGetProperty("arity", out JsonElement arity) ? arity.GetInt32() : 0,
                !entry.TryGetProperty("required", out JsonElement required) || required.GetBoolean(),
                entry.TryGetProperty("external", out JsonElement external) ? external.GetString() : null));
        }

        return bindings;
    }

    /// <summary>Read sts2.dll's metadata into name sets. Nothing is executed.</summary>
    private static Dictionary<string, TypeMembers> ReadTypes(string dllPath)
    {
        using FileStream stream = File.OpenRead(dllPath);
        using var peReader = new PEReader(stream);
        MetadataReader reader = peReader.GetMetadataReader();
        var byName = new Dictionary<string, TypeMembers>(StringComparer.Ordinal);
        var namesByHandle = new Dictionary<TypeDefinitionHandle, string>();

        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition definition = reader.GetTypeDefinition(handle);
            string name = FullName(reader, handle);
            var members = new TypeMembers { FullName = name };
            AddMembers(reader, definition, members);
            byName[name] = members;
            namesByHandle[handle] = name;
        }

        // Link base types by name, so a member declared on a base class still resolves.
        foreach ((TypeDefinitionHandle handle, string name) in namesByHandle)
        {
            EntityHandle baseHandle = reader.GetTypeDefinition(handle).BaseType;
            if (baseHandle.IsNil)
            {
                continue;
            }

            string? baseName = baseHandle.Kind switch
            {
                HandleKind.TypeDefinition => namesByHandle.GetValueOrDefault((TypeDefinitionHandle)baseHandle),
                HandleKind.TypeReference => TypeRefName(reader, (TypeReferenceHandle)baseHandle),
                _ => null,
            };
            if (baseName != null && byName.TryGetValue(baseName, out TypeMembers? baseType))
            {
                byName[name].Base = baseType;
            }
        }

        return byName;
    }

    private static void AddMembers(MetadataReader reader, TypeDefinition definition, TypeMembers members)
    {
        foreach (FieldDefinitionHandle field in definition.GetFields())
        {
            members.Fields.Add(reader.GetString(reader.GetFieldDefinition(field).Name));
        }

        foreach (PropertyDefinitionHandle property in definition.GetProperties())
        {
            members.Properties.Add(reader.GetString(reader.GetPropertyDefinition(property).Name));
        }

        foreach (EventDefinitionHandle @event in definition.GetEvents())
        {
            members.Events.Add(reader.GetString(reader.GetEventDefinition(@event).Name));
        }

        foreach (MethodDefinitionHandle method in definition.GetMethods())
        {
            MethodDefinition definitionMethod = reader.GetMethodDefinition(method);
            string name = reader.GetString(definitionMethod.Name);
            members.Methods.Add(name);
            if (!members.MethodArities.TryGetValue(name, out HashSet<int>? arities))
            {
                arities = new HashSet<int>();
                members.MethodArities[name] = arities;
            }

            arities.Add(definitionMethod.GetParameters().Count);
        }
    }

    private static string FullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition definition = reader.GetTypeDefinition(handle);
        string name = reader.GetString(definition.Name);
        if (definition.GetDeclaringType() is { IsNil: false } declaring)
        {
            return $"{FullName(reader, declaring)}+{name}";
        }

        string ns = reader.GetString(definition.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    private static string TypeRefName(MetadataReader reader, TypeReferenceHandle handle)
    {
        TypeReference reference = reader.GetTypeReference(handle);
        string name = reader.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{TypeRefName(reader, (TypeReferenceHandle)reference.ResolutionScope)}+{name}";
        }

        string ns = reader.GetString(reference.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    private static string? Resolve(Binding binding, Dictionary<string, TypeMembers> types)
    {
        if (binding.Kind == "type")
        {
            return FindType(binding.Member, types) is null
                ? "type missing"
                : null;
        }

        if (binding.Scope == "type" && binding.Type is not null)
        {
            TypeMembers? owner = FindType(binding.Type, types);
            if (owner is null)
            {
                return $"owning type {binding.Type} missing";
            }

            return Declares(owner, binding) ? null : "member missing";
        }

        // The owning type is only known at run time. The member must still be declared by
        // some type in the assembly, which is what a rename or removal breaks.
        return types.Values.Any(type => Declares(type, binding)) ? null : "no type declares it";
    }

    private static bool Declares(TypeMembers type, Binding binding)
    {
        if (binding.Kind == "method-prefix")
        {
            return type.Methods.Any(name => name.StartsWith(binding.Member, StringComparison.Ordinal));
        }

        if (binding.Kind == "constructor")
        {
            return type.MethodArities.TryGetValue(".ctor", out HashSet<int>? arities)
                && (binding.Arity == 0 || arities.Contains(binding.Arity));
        }

        return type.Declares(binding.Kind, binding.Member);
    }

    private static TypeMembers? FindType(string name, Dictionary<string, TypeMembers> types)
    {
        string normalized = name.Replace('+', '.').Replace('/', '+');
        foreach ((string fullName, TypeMembers members) in types)
        {
            if (string.Equals(fullName.Replace('+', '.'), normalized, StringComparison.Ordinal))
            {
                return members;
            }
        }

        // The source states simple names (`typeof(RelicGrabBag)`); resolve them only when
        // unambiguous, so a game type split in two is reported rather than guessed.
        List<TypeMembers> matches = types
            .Where(pair => pair.Key.EndsWith("." + normalized, StringComparison.Ordinal)
                           || pair.Key.Replace('+', '.').EndsWith("." + normalized, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .Distinct()
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
