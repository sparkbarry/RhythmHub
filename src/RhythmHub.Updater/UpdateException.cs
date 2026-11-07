namespace RhythmHub.Updater;

public enum UpdateErrorCode
{
    Network,
    Timeout,
    RateLimited,
    PackageNotFound,
    SignatureInvalid,
    ManifestNotFound,
    ManifestInvalid,
    LauncherTooOld,
    InsufficientDiskSpace,
    DownloadIncomplete,
    HashMismatch,
    UnsafeArchive,
    InvalidPackage,
    GameRunning,
    NotInstalled,
    NoRollbackTarget,

    /// <summary>A game folder can only be moved into a missing or empty folder.</summary>
    DestinationNotEmpty,

    /// <summary>The picked folder is a volume root or overlaps another game's folder.</summary>
    InvalidDestination,
}

public class UpdateException(UpdateErrorCode code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public UpdateErrorCode Code { get; } = code;
}
