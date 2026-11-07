namespace RhythmHub.Updater;

public sealed record UpdaterOptions
{
    /// <summary>
    /// Root for launcher data (settings, logs) and the default home of game folders,
    /// e.g. <c>~/Library/Application Support/RhythmHub</c>.
    /// </summary>
    public required string InstallRoot { get; init; }

    /// <summary>Runtime identifier style platform id: Feed platform directory: "Windows" or "MacOS".</summary>
    public required string PlatformId { get; init; }

    /// <summary>Base URL of the static feed; see <see cref="FeedLayout"/> for the object keys below it.</summary>
    public required Uri FeedBaseUri { get; init; }

    public string Channel { get; init; } = "stable";

    public required Version LauncherVersion { get; init; }

    /// <summary>keyId → base64 Ed25519 public key. Manifests not signed by one of these are rejected.</summary>
    public required IReadOnlyDictionary<string, string> TrustedKeys { get; init; }

    /// <summary>Extra free space required on top of package + installed size.</summary>
    public long DiskSafetyMarginBytes { get; init; } = 256L * 1024 * 1024;

    public int MaxParallelDownloads { get; init; } = 6;
}
