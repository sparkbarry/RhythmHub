namespace RhythmHub.Updater.Models;

public enum PendingStage
{
    /// <summary><c>downloads/{v}/</c> holds some verified objects and maybe partial ones.</summary>
    Downloading,

    /// <summary>Every object the release needs is verified under <c>downloads/{v}/</c>.</summary>
    Downloaded,

    /// <summary>Assembling into staging; objects are being moved out, current.json may or may not be committed.</summary>
    Installing,
}

/// <summary>
/// Contents of <c>{game}/.rhythmhub/pending.json</c>: what an interrupted update was doing.
/// Never evidence that a version is installed; only current.json is.
/// </summary>
public sealed record PendingOperation
{
    public int SchemaVersion { get; init; } = 2;
    public required string Version { get; init; }
    public required string ManifestSha256 { get; init; }
    public PendingStage Stage { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public bool IsFor(GameRelease release) => Version == release.Version && ManifestSha256 == release.ManifestSha256;
}
