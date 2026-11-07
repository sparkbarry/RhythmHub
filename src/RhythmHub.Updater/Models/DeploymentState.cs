namespace RhythmHub.Updater.Models;

/// <summary>
/// Contents of <c>.rhythmhub/deployed.json</c>: which version is laid out in the game folder, and every path the launcher
/// may have put there. Anything else in the folder belongs to the player or the game and is never touched.
/// </summary>
public sealed record DeploymentState
{
    public int SchemaVersion { get; init; } = 1;
    public string? Version { get; init; }

    /// <summary>False while files are being replaced; the folder may then hold a mix of two versions.</summary>
    public bool Complete { get; init; }

    /// <summary>Forward-slash relative paths of files, links and directories, as in the manifest.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];
}
