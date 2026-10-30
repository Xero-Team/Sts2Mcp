namespace STS2_MCP.Core.Contracts;

/// <summary>Gameplay preferences of <see cref="SettingsResponse"/>: speed, shake, and HUD toggles.</summary>
internal sealed record SettingsGameplay
{
    /// <summary>The game's fast-mode preference name (<c>Normal</c>, <c>Fast</c>, <c>Instant</c>).</summary>
    public required string FastMode { get; init; }

    /// <summary>The screen-shake option index, as the settings screen stores it.</summary>
    public required int ScreenShake { get; init; }

    /// <summary>Whether the run timer is shown.</summary>
    public required bool ShowRunTimer { get; init; }

    /// <summary>Whether cards are drawn with their hand index.</summary>
    public required bool ShowCardIndices { get; init; }

    /// <summary>Whether text animation effects are enabled.</summary>
    public required bool TextEffects { get; init; }

    /// <summary>Whether long-press input is enabled.</summary>
    public required bool LongPress { get; init; }
}
