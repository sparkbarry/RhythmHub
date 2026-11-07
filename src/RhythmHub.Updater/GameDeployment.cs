using System.Text.Json;
using Microsoft.Extensions.Logging;
using RhythmHub.Updater.Models;

namespace RhythmHub.Updater;

/// <summary>
/// Lays out one cached version (<c>.rhythmhub/versions/{v}</c>) in the game folder itself. Only paths a manifest names are
/// touched, so what the game writes beside them (settings, charts, scores) survives updates and rollbacks.
/// deployed.json lists every path the launcher may have placed: it is widened before the folder changes and narrowed
/// after, so an interrupted deployment is simply run again.
/// </summary>
public sealed class GameDeployment(GameInstallLayout layout, ILogger logger)
{
    public async Task<DeploymentState?> LoadAsync(CancellationToken ct)
    {
        try
        {
            return await AtomicFile.ReadJsonAsync<DeploymentState>(layout.DeploymentStatePath, ct);
        }
        catch (JsonException)
        {
            return null; // Unreadable: treated as nothing deployed, so the next deployment rewrites everything.
        }
    }

    /// <summary>True when <paramref name="version"/> is fully laid out in the game folder.</summary>
    public async Task<bool> IsCompleteAsync(string version, CancellationToken ct) =>
        await LoadAsync(ct) is { Complete: true } state && state.Version == version;

    public async Task DeployAsync(string version, ReleaseManifest manifest, CancellationToken ct)
    {
        var root = layout.GameDirectory;
        var source = layout.VersionDirectory(version);
        var wanted = ManagedPaths(manifest);
        var previous = await LoadAsync(ct);
        var placed = new HashSet<string>(previous?.Paths ?? [], StringComparer.Ordinal);

        await SaveAsync(new DeploymentState { Version = version, Complete = false, Paths = Sorted(placed.Union(wanted)) }, ct);

        // Old files first: on a case-insensitive volume a renamed file must go before its new spelling is written.
        var stale = placed.Except(wanted).OrderByDescending(Depth).ToList();
        foreach (var path in stale) RemoveStale(root, path);

        foreach (var dir in ManagedDirectories(manifest).OrderBy(Depth))
            EnsureDirectory(ManifestPaths.Under(root, dir));

        var copied = 0;
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (PlaceFile(ManifestPaths.Under(source, file.Path), ManifestPaths.Under(root, file.Path), file)) copied++;
        }

        foreach (var link in manifest.Symlinks)
            PlaceLink(ManifestPaths.Under(root, link.Path), link);

        await SaveAsync(new DeploymentState { Version = version, Complete = true, Paths = Sorted(wanted) }, ct);
        logger.LogInformation("Laid out {Game} {Version} in {Dir}: {Copied} files copied, {Stale} old paths removed",
            layout.GameId, version, root, copied, stale.Count);
    }

    /// <summary>Every file, link and directory the manifest puts in the game folder, parents included.</summary>
    internal static HashSet<string> ManagedPaths(ReleaseManifest manifest)
    {
        var paths = ManagedDirectories(manifest);
        paths.UnionWith(manifest.Files.Select(f => f.Path));
        paths.UnionWith(manifest.Symlinks.Select(l => l.Path));
        return paths;
    }

    internal static HashSet<string> ManagedDirectories(ReleaseManifest manifest)
    {
        var dirs = new HashSet<string>(manifest.Directories, StringComparer.Ordinal);
        foreach (var path in manifest.Files.Select(f => f.Path).Concat(manifest.Symlinks.Select(l => l.Path)).Concat(manifest.Directories))
        {
            for (var cut = path.LastIndexOf('/'); cut > 0; cut = path.LastIndexOf('/', cut - 1))
                dirs.Add(path[..cut]);
        }
        return dirs;
    }

    private void RemoveStale(string root, string path)
    {
        var full = ManifestPaths.Under(root, path);
        // A link the player put in place of one of our directories: what is behind it is not ours.
        if (FileSystemEntry.HasLinkedParent(root, full)) return;
        switch (FileSystemEntry.Kind(full))
        {
            case EntryKind.Link or EntryKind.File:
                FileSystemEntry.Delete(full);
                break;
            case EntryKind.Directory when FileSystemEntry.IsEmptyDirectory(full):
                FileSystemEntry.DeleteEmptyDirectory(full);
                break;
            case EntryKind.Directory:
                logger.LogDebug("Keeping {Path}: it holds files the launcher did not put there", path);
                break;
        }
    }

    private static void EnsureDirectory(string path)
    {
        if (FileSystemEntry.Kind(path) is EntryKind.Link or EntryKind.File) FileSystemEntry.Delete(path);
        Directory.CreateDirectory(path);
    }

    /// <returns>False when an untouched copy was already in place.</returns>
    private static bool PlaceFile(string source, string target, ManifestFile file)
    {
        var cached = new FileInfo(source);
        if (!cached.Exists || cached.Length != file.Size)
            throw new UpdateException(UpdateErrorCode.InvalidPackage, $"The cached copy of '{file.Path}' is missing or damaged.");

        var copied = true;
        switch (FileSystemEntry.Kind(target))
        {
            case EntryKind.File when new FileInfo(target) is var existing &&
                                     existing.Length == cached.Length && existing.LastWriteTimeUtc == cached.LastWriteTimeUtc:
                copied = false;
                break;
            case EntryKind.None:
                break;
            default:
                FileSystemEntry.Delete(target);
                break;
        }
        if (copied) FileSystemEntry.CopyFile(source, target);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(target, (UnixFileMode)(file.Mode ?? 0b110_100_100));
        return copied;
    }

    private static void PlaceLink(string path, ManifestSymlink link)
    {
        if (FileSystemEntry.Kind(path) == EntryKind.Link && new FileInfo(path).LinkTarget == link.Target) return;
        if (OperatingSystem.IsWindows())
            throw new UpdateException(UpdateErrorCode.InvalidPackage, $"Symlink '{link.Path}' is not supported on Windows.");
        FileSystemEntry.Delete(path);
        File.CreateSymbolicLink(path, link.Target);
    }

    private Task SaveAsync(DeploymentState state, CancellationToken ct) =>
        AtomicFile.WriteJsonAsync(layout.DeploymentStatePath, state, ct);

    private static List<string> Sorted(IEnumerable<string> paths) => [.. paths.Order(StringComparer.Ordinal)];

    private static int Depth(string path) => path.Count(c => c == '/');
}
