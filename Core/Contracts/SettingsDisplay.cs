namespace STS2_MCP.Core.Contracts;

/// <summary>Display preferences of <see cref="SettingsResponse"/>: window, frame rate, monitor.</summary>
internal sealed record SettingsDisplay
{
    /// <summary>Fullscreen window mode.</summary>
    public required bool Fullscreen { get; init; }

    /// <summary>The saved window size, formatted <c>WIDTHxHEIGHT</c>.</summary>
    public required string Resolution { get; init; }

    /// <summary>The frame-rate cap.</summary>
    public required int FpsLimit { get; init; }

    /// <summary>The game's vertical-sync mode name.</summary>
    public required string Vsync { get; init; }

    /// <summary>The MSAA sample count.</summary>
    public required int Msaa { get; init; }

    /// <summary>The game's aspect-ratio setting name.</summary>
    public required string AspectRatio { get; init; }

    /// <summary>The monitor the game renders to.</summary>
    public required int TargetDisplay { get; init; }

    /// <summary>Whether the frame rate is also capped while the window is unfocused.</summary>
    public required bool LimitFpsBackground { get; init; }
}
