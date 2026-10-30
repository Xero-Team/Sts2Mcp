namespace STS2_MCP.Core.Contracts;

/// <summary>Channel volumes of <see cref="SettingsResponse"/>.</summary>
/// <remarks>
/// These stay <see cref="float"/> because that is how the game stores them: widening to
/// <see cref="double"/> would change the emitted JSON (<c>0.8f</c> becomes
/// <c>0.800000011920929</c>).
/// </remarks>
internal sealed record SettingsAudio
{
    /// <summary>Master volume.</summary>
    public required float Master { get; init; }

    /// <summary>Music volume.</summary>
    public required float Bgm { get; init; }

    /// <summary>Sound-effect volume.</summary>
    public required float Sfx { get; init; }

    /// <summary>Ambience volume.</summary>
    public required float Ambience { get; init; }
}
