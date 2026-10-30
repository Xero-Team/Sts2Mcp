using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Sts2Mcp;

namespace Sts2Mcp.Tests.Config;

/// <summary>
/// Pins the port-precedence chain without a game or a Godot runtime: command-line
/// argument, then STS2_PORT, then .env, then the conf port, then the default. Each
/// malformed value must fall through to the next source instead of stopping startup.
/// </summary>
internal static class Program
{
    private static readonly MethodInfo s_withResolvedPort =
        typeof(McpMod).GetMethod("WithResolvedPort", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("McpMod.WithResolvedPort not found");

    private static readonly MethodInfo s_parseDotEnvValue =
        typeof(McpMod).GetMethod("ParseDotEnvValue", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("McpMod.ParseDotEnvValue not found");

    private static int s_failures;
    private static string? s_originalDirectory;

    private static int Main()
    {
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        s_originalDirectory = Environment.CurrentDirectory;

        try
        {
            Run("defaults", confPort: McpMod.DefaultPort, expect: McpMod.DefaultPort);
            Run("conf port", confPort: 17000, expect: 17000);
            Run("conf port zero falls back", confPort: 0, expect: McpMod.DefaultPort);
            Run("conf port above 65535 falls back", confPort: 65536, expect: McpMod.DefaultPort);
            Run("conf port negative falls back", confPort: -1, expect: McpMod.DefaultPort);

            Run("STS2_PORT beats conf", confPort: 17000, env: "18000", expect: 18000);
            Run("STS2_PORT is trimmed", confPort: 17000, env: " 18001 ", expect: 18001);
            Run("malformed STS2_PORT falls through", confPort: 17000, env: "abc", expect: 17000);
            Run("out-of-range STS2_PORT falls through", confPort: 17000, env: "70000", expect: 17000);
            Run("blank STS2_PORT is absent", confPort: 17000, env: "   ", expect: 17000);
            Run("zero STS2_PORT falls through", confPort: 17000, env: "0", expect: 17000);

            Run("dotenv in cwd", confPort: 17000, dotEnv: "STS2_PORT=19000", expect: 19000);
            Run("dotenv comment", confPort: 17000, dotEnv: "STS2_PORT=19100 # local override", expect: 19100);
            Run("dotenv export and double quotes", confPort: 17000, dotEnv: "export STS2_PORT=\"19200\"", expect: 19200);
            Run("dotenv single quotes keep hash", confPort: 17000, dotEnv: "STS2_PORT='19300' # trailing", expect: 19300);
            Run("dotenv wrong case is ignored", confPort: 17000, dotEnv: "sts2_port=19400", expect: 17000);
            Run("dotenv malformed falls through", confPort: 17000, dotEnv: "STS2_PORT=notaport", expect: 17000);
            // The reader stops at the first STS2_PORT line, valid or not: it does not
            // scan on for a later value.
            Run("dotenv first STS2_PORT line wins", confPort: 17000,
                dotEnv: "STS2_PORT=19500\nSTS2_PORT=70000", expect: 19500);
            Run("dotenv invalid first line does not scan on", confPort: 17000,
                dotEnv: "STS2_PORT=70000\nSTS2_PORT=19500", expect: 17000);

            Run("environment beats dotenv", confPort: 17000, env: "18000", dotEnv: "STS2_PORT=19000", expect: 18000);
            Run("invalid environment reaches dotenv", confPort: 17000, env: "nope", dotEnv: "STS2_PORT=19000", expect: 19000);

            Run("argument beats everything", confPort: 17000, env: "18000",
                dotEnv: "STS2_PORT=19000", args: new[] { "--sts2-mcp-port=20000" }, expect: 20000);
            Run("argument prefix is case-insensitive", confPort: 17000,
                args: new[] { "--STS2-MCP-PORT=20001" }, expect: 20001);
            Run("first valid argument wins", confPort: 17000,
                args: new[] { "--sts2-mcp-port=20000", "--sts2-mcp-port=20002" }, expect: 20000);
            Run("malformed argument falls through", confPort: 17000, env: "18000",
                args: new[] { "--sts2-mcp-port=abc" }, expect: 18000);
            Run("out-of-range argument falls through", confPort: 17000,
                args: new[] { "--sts2-mcp-port=70000" }, expect: 17000);
            Run("unrelated argument is ignored", confPort: 17000,
                args: new[] { "--verbose", "--sts2-mcp-port=20003" }, expect: 20003);
            Run("argument without a value falls through", confPort: 17000,
                args: new[] { "--sts2-mcp-port=" }, expect: 17000);

            Equal(ParseDotEnv("plain"), "plain", "plain value");
            Equal(ParseDotEnv("  spaced  "), "spaced", "value is trimmed");
            Equal(ParseDotEnv("value # comment"), "value", "unquoted comment is stripped");
            Equal(ParseDotEnv("\"value # kept\""), "value # kept", "quoted hash is kept");
            Equal(ParseDotEnv("'single # kept'"), "single # kept", "single-quoted hash is kept");
            // No closing quote: the whole raw value (quote included) survives, and the
            // port parser then rejects it, so a typo cannot become a port.
            Equal(ParseDotEnv("\"unterminated"), "\"unterminated", "unterminated quote is not stripped");

            // The pure resolver, without the file/env layer.
            Equal(McpPortResolver.Resolve(new[] { "--sts2-mcp-port=30000" }, 17000), 30000, "resolver argument");
            Equal(McpPortResolver.Resolve(Array.Empty<string>(), 17000), 17000, "resolver configured");
            Equal(McpPortResolver.Resolve(Array.Empty<string>(), null), McpMod.DefaultPort, "resolver default");
            Equal(McpPortResolver.Resolve(new[] { "--sts2-mcp-port=0" }, 17000), 17000, "resolver rejects zero");
        }
        finally
        {
            Environment.SetEnvironmentVariable("STS2_PORT", null);
            Environment.CurrentDirectory = s_originalDirectory!;
        }

        if (s_failures > 0)
        {
            Console.WriteLine($"FAIL {s_failures} config check(s)");
            return 1;
        }

        Console.WriteLine("ALL OK (config port precedence)");
        return 0;
    }

    private static void Run(
        string name,
        int confPort,
        int expect,
        string? env = null,
        string? dotEnv = null,
        string[]? args = null)
    {
        string temp = Path.Combine(Path.GetTempPath(), "sts2mcp-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Environment.SetEnvironmentVariable("STS2_PORT", env);
            Godot.OS.Cmdline = args is null ? new List<string>() : new List<string>(args);
            Environment.CurrentDirectory = temp;
            if (dotEnv is not null)
            {
                File.WriteAllText(Path.Combine(temp, ".env"), dotEnv);
            }

            var config = new McpMod.ModConfig { Port = confPort };
            object? result = s_withResolvedPort.Invoke(null, new object?[] { config });
            int actual = ((McpMod.ModConfig)result!).Port;
            Equal(actual, expect, name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STS2_PORT", null);
            Godot.OS.Cmdline = new List<string>();
            Environment.CurrentDirectory = s_originalDirectory!;
            Directory.Delete(temp, recursive: true);
        }
    }

    private static string ParseDotEnv(string raw)
        => (string)s_parseDotEnvValue.Invoke(null, new object?[] { raw })!;

    private static void Equal<T>(T actual, T expected, string name)
    {
        if (EqualityComparer<T>.Default.Equals(actual, expected))
        {
            Console.WriteLine($"ok   {name}: {actual}");
            return;
        }

        s_failures++;
        Console.WriteLine($"FAIL {name}: expected {expected}, got {actual}");
    }
}
