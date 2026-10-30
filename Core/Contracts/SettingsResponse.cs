namespace Sts2Mcp.Core.Contracts;

/// <summary>
/// The documented <c>GET /api/v1/settings</c> body: the game's saved display, audio, gameplay and
/// mod-loading preferences.
/// </summary>
/// <remarks>
/// Property order is the pinned wire order, documented under "GET /api/v1/settings" in
/// <c>docs/raw-full.md</c> and asserted by <c>tests/ContractShape</c>. Every value is a primitive on
/// purpose: <c>Core</c> must not reference <c>MegaCrit</c>, so the enum-typed game members
/// (<c>VSync</c>, <c>AspectRatioSetting</c>, <c>FastMode</c>) are stringified by the code that reads
/// them. C# names are chosen so the shared snake_case policy produces the wire name; no property
/// carries a hand-written JSON name.
/// </remarks>
internal sealed record SettingsResponse
{
    /// <summary>Always <c>"ok"</c>.</summary>
    public string Status { get; } = "ok";

    /// <summary>Always <c>"settings"</c>.</summary>
    public string Kind { get; } = "settings";

    /// <summary>Window, frame-rate, and display-target preferences.</summary>
    public required SettingsDisplay Display { get; init; }

    /// <summary>Channel volumes, as the game stores them.</summary>
    public required SettingsAudio Audio { get; init; }

    /// <summary>Fast-mode, shake, and HUD preferences.</summary>
    public required SettingsGameplay Gameplay { get; init; }

    /// <summary>Mod-loading consent.</summary>
    public required SettingsMods Mods { get; init; }

    /// <summary>The selected language; omitted when the save has none, like every other absent field.</summary>
    public string? Language { get; init; }

    /// <summary>Whether the intro logo is skipped.</summary>
    public required bool SkipIntro { get; init; }
}
