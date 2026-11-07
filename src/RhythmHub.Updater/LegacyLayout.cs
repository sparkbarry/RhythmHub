using Microsoft.Extensions.Logging;
using RhythmHub.Updater.Models;

namespace RhythmHub.Updater;

/// <summary>
/// Launchers up to v1.0.3 kept <c>versions/</c>, <c>downloads/</c> and the state files directly in the game folder and
/// ran each version from inside <c>versions/{v}</c>, so whatever the game saved there stayed behind on every update.
/// This moves that state into <c>.rhythmhub/</c> and carries the active version's saved files into the game folder.
/// </summary>
internal static class LegacyLayout
{
    private static readonly string[] Directories = ["versions", "downloads", "staging", "health"];

    public static bool IsPresent(GameInstallLayout layout) =>
        File.Exists(Path.Combine(layout.GameDirectory, "current.json")) && !File.Exists(layout.CurrentStatePath);

    /// <summary>The entrypoint of the version the old layout runs, to check the game is closed before moving it.</summary>
    public static async Task<string?> ActiveEntrypointAsync(GameInstallLayout layout, CancellationToken ct)
    {
        var state = await AtomicFile.ReadJsonAsync<LocalGameState>(Path.Combine(layout.GameDirectory, "current.json"), ct);
        if (state?.ActiveVersion is not { } active) return null;
        foreach (var root in new[] { layout.GameDirectory, layout.DataDirectory })
        {
            var info = Path.Combine(root, "versions", GameInstallLayout.SafeName(active), ManifestPaths.ReservedDirectory, "release.json");
            if (await AtomicFile.ReadJsonAsync<InstalledReleaseInfo>(info, ct) is { } release) return release.Entrypoint;
        }
        return null;
    }

    /// <summary>Safe to run again after an interruption: current.json moves last and marks the migration done.</summary>
    public static async Task MigrateAsync(GameInstallLayout layout, ILogger logger, CancellationToken ct)
    {
        logger.LogInformation("Moving {Game} to the .rhythmhub layout in {Dir}", layout.GameId, layout.GameDirectory);
        Directory.CreateDirectory(layout.DataDirectory);
        foreach (var name in Directories)
        {
            var from = Path.Combine(layout.GameDirectory, name);
            var to = Path.Combine(layout.DataDirectory, name);
            if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to);
        }
        var pending = Path.Combine(layout.GameDirectory, "pending.json");
        if (File.Exists(pending)) File.Move(pending, layout.PendingStatePath, overwrite: true);

        var legacyState = Path.Combine(layout.GameDirectory, "current.json");
        var state = await AtomicFile.ReadJsonAsync<LocalGameState>(legacyState, ct);
        if (state?.ActiveVersion is { } active &&
            await new VersionRepository(layout).LoadManifestAsync(active, ct) is { } manifest)
        {
            var moved = MoveUnmanaged(layout.VersionDirectory(active), "", layout.GameDirectory,
                GameDeployment.ManagedPaths(manifest), GameDeployment.ManagedDirectories(manifest), logger);
            logger.LogInformation("Kept {Count} files and folders the game had saved in {Version}", moved, active);
        }
        File.Move(legacyState, layout.CurrentStatePath, overwrite: true);
    }

    /// <summary>Moves everything the manifest does not name from a version directory to the same place in the game folder.</summary>
    private static int MoveUnmanaged(string dir, string relative, string gameDirectory, HashSet<string> managed,
        HashSet<string> managedDirectories, ILogger logger)
    {
        var moved = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir).ToList())
        {
            var name = Path.GetFileName(entry);
            var path = relative.Length == 0 ? name : relative + "/" + name;
            if (relative.Length == 0 && name == ManifestPaths.ReservedDirectory) continue;

            var kind = FileSystemEntry.Kind(entry);
            if (kind == EntryKind.Directory && managedDirectories.Contains(path))
            {
                moved += MoveUnmanaged(entry, path, gameDirectory, managed, managedDirectories, logger);
                continue;
            }
            if (managed.Contains(path)) continue;

            var target = Path.Combine(gameDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            if (FileSystemEntry.Kind(target) != EntryKind.None)
            {
                logger.LogWarning("Not moving {Path}: the game folder already has one", path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (kind == EntryKind.Directory) Directory.Move(entry, target);
            else File.Move(entry, target);
            moved++;
        }
        return moved;
    }
}
