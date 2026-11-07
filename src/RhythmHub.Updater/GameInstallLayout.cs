namespace RhythmHub.Updater;

/// <summary>
/// One game's folder. The folder itself holds the active version laid out as the game expects, next to whatever the game
/// writes there (settings, charts); everything the launcher keeps lives in its <c>.rhythmhub/</c> subfolder.
/// Both share one volume, so renames between them stay atomic.
/// </summary>
public sealed class GameInstallLayout(string gameDirectory, string gameId)
{
    public string GameId { get; } = gameId;

    /// <summary>The folder the game runs from; the player can pick it.</summary>
    public string GameDirectory { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));

    /// <summary><c>{game}/.rhythmhub</c>: cached versions, downloads and state, never touched by the game.</summary>
    public string DataDirectory => Path.Combine(GameDirectory, ManifestPaths.ReservedDirectory);
    public string VersionsDirectory => Path.Combine(DataDirectory, "versions");
    public string DownloadsDirectory => Path.Combine(DataDirectory, "downloads");
    public string StagingDirectory => Path.Combine(DataDirectory, "staging");
    public string CurrentStatePath => Path.Combine(DataDirectory, "current.json");
    public string PendingStatePath => Path.Combine(DataDirectory, "pending.json");
    public string DeploymentStatePath => Path.Combine(DataDirectory, "deployed.json");
    public string HealthFilePath => Path.Combine(DataDirectory, "health", "health.json");

    /// <summary>Where a game goes when the player has not picked a folder: <c>{installRoot}/games/{gameId}</c>.</summary>
    public static string DefaultGameDirectory(string installRoot, string gameId) =>
        Path.Combine(installRoot, "games", SafeName(gameId));

    /// <summary>The pristine copy of one version; the game folder is laid out from it.</summary>
    public string VersionDirectory(string version) => Path.Combine(VersionsDirectory, SafeName(version));
    public string ReleaseInfoPath(string version) => Path.Combine(VersionDirectory(version), ManifestPaths.ReservedDirectory, "release.json");
    public string ManifestCopyPath(string version) => Path.Combine(VersionDirectory(version), ManifestPaths.ReservedDirectory, "manifest.json");

    /// <summary>Content-addressed store for one version's download: <c>downloads/{v}/{sha256}</c>.</summary>
    public string ObjectsDirectory(string version) => Path.Combine(DownloadsDirectory, SafeName(version));
    public string ObjectPath(string version, string sha256) => Path.Combine(ObjectsDirectory(version), sha256);
    public string PartialObjectPath(string version, string sha256) => ObjectPath(version, sha256) + ".partial";

    public void EnsureCreated()
    {
        Directory.CreateDirectory(VersionsDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(StagingDirectory);
        if (OperatingSystem.IsWindows())
        {
            // A dot prefix hides it on macOS; Explorer needs the attribute.
            var info = new DirectoryInfo(DataDirectory);
            if (!info.Attributes.HasFlag(FileAttributes.Hidden)) info.Attributes |= FileAttributes.Hidden;
        }
    }

    /// <summary>Versions and ids come from the network; never let them become path separators.</summary>
    internal static string SafeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+')))
        {
            throw new UpdateException(UpdateErrorCode.ManifestInvalid, $"Illegal identifier '{value}'.");
        }
        return value;
    }
}
