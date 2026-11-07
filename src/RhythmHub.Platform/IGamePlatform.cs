using RhythmHub.Updater;

namespace RhythmHub.Platform;

public interface IGamePlatform : IPackageVerifier
{
    /// <summary>Feed directory name: "Windows" (x64) or "MacOS" (Apple Silicon; Intel Macs are not supported).</summary>
    string PlatformId { get; }

    /// <summary>Per-user, writable root for settings, logs and the default game folders. No admin rights needed.</summary>
    string DefaultInstallRoot { get; }

    /// <param name="gameDirectory">The game folder; <paramref name="entrypoint"/> is relative to it.</param>
    /// <param name="environment">Extra variables for the game process (the health handshake uses these).</param>
    Task LaunchGameAsync(string gameDirectory, string entrypoint, IReadOnlyDictionary<string, string> environment,
        CancellationToken ct);

    /// <summary>Opens a folder in Explorer / Finder.</summary>
    void RevealDirectory(string path);
}

public static class GamePlatform
{
    public const string AppFolderName = "RhythmHub";

    public static IGamePlatform Current()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPlatform();
        if (OperatingSystem.IsMacOS()) return new MacOSPlatform();
        throw new PlatformNotSupportedException("RhythmHub supports Windows and macOS.");
    }
}
