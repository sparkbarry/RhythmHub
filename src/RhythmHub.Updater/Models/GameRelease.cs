namespace RhythmHub.Updater.Models;

/// <summary>A verified release the launcher can install.</summary>
/// <param name="ManifestBytes">The signed manifest exactly as served; kept next to the install for later diffs.</param>
/// <param name="DownloadBytes">Estimated bytes to fetch, after reusing files from installed versions.</param>
public sealed record GameRelease(
    string GameId,
    string Version,
    string Platform,
    string ManifestSha256,
    ReleaseManifest Manifest,
    byte[] ManifestBytes,
    long DownloadBytes)
{
    public string Entrypoint => Manifest.Entrypoint;
    public long InstalledSizeBytes => Manifest.InstalledSizeBytes;
    public Uri? ReleaseNotesUrl => Manifest.ReleaseNotesUrl is { } u ? new Uri(u) : null;
}

public enum DownloadStage
{
    /// <summary>Copying and re-hashing unchanged files from an installed version.</summary>
    ReusingLocalFiles,
    Downloading,
}

public sealed record DownloadProgress(long CompletedBytes, long TotalBytes, DownloadStage Stage = DownloadStage.Downloading);

/// <summary>The version current.json names; it runs from <paramref name="GameDirectory"/>, the game folder itself.</summary>
public sealed record InstalledGame(string Version, string GameDirectory, string Entrypoint);
