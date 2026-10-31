using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sts2Mcp;

internal static class McpPortResolver
{
    private const string PortArgumentPrefix = "--sts2-mcp-port=";

    internal static int Resolve(IReadOnlyList<string> arguments, int? configuredPort)
    {
        foreach (string argument in arguments)
        {
            if (!argument.StartsWith(PortArgumentPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string value = argument[PortArgumentPrefix.Length..];
            if (int.TryParse(value, CultureInfo.InvariantCulture, out int overridePort) && IsValid(overridePort))
                return overridePort;

            break;
        }

        return configuredPort is int port && IsValid(port) ? port : McpMod.DefaultPort;
    }

    private static bool IsValid(int port) => port is > 0 and <= 65535;
}
