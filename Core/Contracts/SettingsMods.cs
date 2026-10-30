namespace STS2_MCP.Core.Contracts;

/// <summary>Mod-loading consent of <see cref="SettingsResponse"/>.</summary>
internal sealed record SettingsMods
{
    /// <summary>Whether the player agreed to mod loading.</summary>
    public required bool Enabled { get; init; }
}
