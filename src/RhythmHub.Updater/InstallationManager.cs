using Microsoft.Extensions.Logging;
using RhythmHub.Updater.Models;

namespace RhythmHub.Updater;

/// <summary>
/// Assembles <c>.rhythmhub/versions/{v}</c> from the verified objects in <c>downloads/{v}/</c>, commits it via
/// current.json and then lays it out in the game folder. The cached copy of the previous version is never touched
/// before the commit.
/// </summary>
public sealed class InstallationManager(GameInstallLayout layout, VersionRepository versions, GameDeployment deployment,
    ILogger<InstallationManager> logger)
{
    /// <param name="verifyPackage">Platform check run on the staged directory before it is moved into place.</param>
    public async Task InstallAsync(GameRelease release, Func<string, string, CancellationToken, Task> verifyPackage,
        CancellationToken ct)
    {
        layout.EnsureCreated();
        var staging = Path.Combine(layout.StagingDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            logger.LogInformation("Assembling {Game} {Version} in {Staging}", layout.GameId, release.Version, staging);
            Assemble(release.Manifest, layout.ObjectsDirectory(release.Version), staging, ct);
            await verifyPackage(staging, release.Entrypoint, ct);

            var meta = Path.Combine(staging, ManifestPaths.ReservedDirectory);
            Directory.CreateDirectory(meta);
            await File.WriteAllBytesAsync(Path.Combine(meta, "manifest.json"), release.ManifestBytes, ct);
            await AtomicFile.WriteJsonAsync(Path.Combine(meta, "release.json"), new InstalledReleaseInfo
            {
                Version = release.Version,
                Entrypoint = release.Entrypoint,
                ManifestSha256 = release.ManifestSha256,
                InstalledAt = DateTimeOffset.UtcNow,
            }, ct);

            var state = await versions.LoadAsync(ct);
            var target = layout.VersionDirectory(release.Version);
            if (Directory.Exists(target))
            {
                if (state.ActiveVersion == release.Version)
                    throw new UpdateException(UpdateErrorCode.InvalidPackage, $"Version {release.Version} is already active.");
                // A leftover from an earlier, uncommitted attempt: safe to replace.
                logger.LogWarning("Replacing stale version directory {Dir}", target);
                Directory.Delete(target, recursive: true);
            }
            Directory.Move(staging, target);

            // Commit point. Only a version that has launched successfully becomes the rollback target;
            // an unverified one is dropped in favour of the last known good.
            var previous = state.ActiveVersion;
            var rollbackTarget = previous is not null && (state.LastLaunchSuccessful || state.PreviousGoodVersion is null)
                ? previous
                : state.PreviousGoodVersion;
            await versions.SaveAsync(state with
            {
                ActiveVersion = release.Version,
                PreviousGoodVersion = rollbackTarget,
                LastLaunchSuccessful = false,
            }, ct);
            logger.LogInformation("Activated {Game} {Version} (previous {Previous})", layout.GameId, release.Version, previous);

            // Committed: from here an interruption is finished by recovery, so don't stop halfway on cancel.
            await deployment.DeployAsync(release.Version, release.Manifest, CancellationToken.None);
            TryDeleteDirectory(layout.ObjectsDirectory(release.Version));
            await PruneAsync(ct);
        }
        finally
        {
            if (Directory.Exists(staging)) TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Lays out every manifest entry under <paramref name="staging"/>. Objects used by a single path are moved (cheap on
    /// one volume); shared ones are copied. Symlinks are created last so nothing is ever written through one.
    /// </summary>
    internal static void Assemble(ReleaseManifest manifest, string objects, string staging, CancellationToken ct)
    {
        Directory.CreateDirectory(staging);
        foreach (var dir in manifest.Directories)
            Directory.CreateDirectory(ManifestPaths.Under(staging, dir));

        var remainingUses = manifest.Files.Where(f => f.Size > 0).GroupBy(f => f.Sha256).ToDictionary(g => g.Key, g => g.Count());
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var target = ManifestPaths.Under(staging, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (file.Size == 0)
            {
                File.WriteAllBytes(target, []);
            }
            else
            {
                var source = Path.Combine(objects, file.Sha256);
                if (!File.Exists(source) || new FileInfo(source).Length != file.Size)
                    throw new UpdateException(UpdateErrorCode.DownloadIncomplete, $"Missing verified data for '{file.Path}'.");
                if (--remainingUses[file.Sha256] == 0) File.Move(source, target);
                else File.Copy(source, target);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, (UnixFileMode)(file.Mode ?? 0b110_100_100));
        }

        foreach (var link in manifest.Symlinks)
        {
            var path = ManifestPaths.Under(staging, link.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (OperatingSystem.IsWindows())
            {
                // Windows builds have no symlinks; creating one would need developer mode, so refuse clearly.
                throw new UpdateException(UpdateErrorCode.InvalidPackage, $"Symlink '{link.Path}' is not supported on Windows.");
            }
            File.CreateSymbolicLink(path, link.Target);
        }

        var entry = Path.Combine(staging, manifest.Entrypoint);
        if (!File.Exists(entry) && !Directory.Exists(entry))
            throw new UpdateException(UpdateErrorCode.InvalidPackage, $"Entrypoint '{manifest.Entrypoint}' missing after assembly.");
    }

    public async Task RollbackAsync(CancellationToken ct)
    {
        var state = await versions.LoadAsync(ct);
        if (state.PreviousGoodVersion is not { } previous || previous == state.ActiveVersion ||
            await versions.LoadReleaseInfoAsync(previous, ct) is null ||
            await versions.LoadManifestAsync(previous, ct) is not { } manifest)
            throw new UpdateException(UpdateErrorCode.NoRollbackTarget, "No previous version is available.");

        // The version being left is the one that misbehaved; don't offer it as a rollback target.
        // previous launched fine before, so it keeps its known-good status.
        await versions.SaveAsync(state with
        {
            ActiveVersion = previous,
            PreviousGoodVersion = null,
            LastLaunchSuccessful = true,
        }, ct);
        logger.LogInformation("Rolled back {Game} from {From} to {To}", layout.GameId, state.ActiveVersion, previous);
        await deployment.DeployAsync(previous, manifest, CancellationToken.None);
    }

    /// <summary>Keep the active and previous good versions; drop everything else, plus stale staging leftovers.</summary>
    public async Task PruneAsync(CancellationToken ct)
    {
        var state = await versions.LoadAsync(ct);
        var keep = new HashSet<string?>([state.ActiveVersion, state.PreviousGoodVersion]);
        if (Directory.Exists(layout.VersionsDirectory))
        {
            foreach (var dir in Directory.GetDirectories(layout.VersionsDirectory))
                if (!keep.Contains(Path.GetFileName(dir))) TryDeleteDirectory(dir);
        }
        if (Directory.Exists(layout.StagingDirectory))
        {
            foreach (var dir in Directory.GetDirectories(layout.StagingDirectory)) TryDeleteDirectory(dir);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }
}
