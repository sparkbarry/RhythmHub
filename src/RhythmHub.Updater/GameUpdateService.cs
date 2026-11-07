using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RhythmHub.Updater.Models;

namespace RhythmHub.Updater;

/// <summary>Platform-specific hooks the updater needs but must not implement itself.</summary>
public interface IPackageVerifier
{
    Task VerifyPlatformPackageAsync(string unpackedDirectory, string entrypoint, CancellationToken ct);

    /// <summary>True if <paramref name="entrypoint"/>'s process runs from anywhere inside <paramref name="gameDirectory"/>.</summary>
    Task<bool> IsGameRunningAsync(string gameDirectory, string entrypoint, CancellationToken ct);
}

public sealed class GameUpdateService : IGameUpdateService
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private UpdaterOptions _options;
    private readonly GameInstallLayout _layout;
    private readonly VersionRepository _versions;
    private readonly InstallationManager _installer;
    private readonly GameDeployment _deployment;
    private readonly ManifestClient _manifests;
    private readonly DownloadService _downloads;
    private readonly IPackageVerifier _platform;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="gameDirectory">The folder the game lives in; null for <see cref="GameInstallLayout.DefaultGameDirectory"/>.</param>
    public GameUpdateService(string gameId, UpdaterOptions options, HttpClient http, IPackageVerifier platform,
        ILoggerFactory loggers, DownloadService? downloads = null, string? gameDirectory = null)
    {
        GameId = GameInstallLayout.SafeName(gameId);
        _options = options;
        _layout = new GameInstallLayout(gameDirectory ?? GameInstallLayout.DefaultGameDirectory(options.InstallRoot, gameId), gameId);
        _versions = new VersionRepository(_layout);
        _deployment = new GameDeployment(_layout, loggers.CreateLogger<GameDeployment>());
        _installer = new InstallationManager(_layout, _versions, _deployment, loggers.CreateLogger<InstallationManager>());
        _manifests = new ManifestClient(http, loggers.CreateLogger<ManifestClient>());
        _downloads = downloads ?? new DownloadService(http, loggers.CreateLogger<DownloadService>());
        _platform = platform;
        _logger = loggers.CreateLogger<GameUpdateService>();
        Health = new LaunchHealth(_layout);
    }

    public string GameId { get; }

    /// <summary>
    /// Channel this game follows (<c>stable</c>, <c>nightly</c>); takes effect on the next <see cref="CheckAsync"/>.
    /// Switching installs whatever the new channel points at, older or newer.
    /// </summary>
    public string Channel
    {
        get => _options.Channel;
        set => _options = _options with { Channel = value };
    }

    public GameInstallLayout Layout => _layout;
    public LaunchHealth Health { get; }

    public Task<InstalledGame?> GetInstalledAsync(CancellationToken ct) => _versions.GetActiveAsync(ct);

    public Task<LocalGameState> GetStateAsync(CancellationToken ct) => _versions.LoadAsync(ct);

    public Task<PendingOperation?> GetPendingAsync(CancellationToken ct) =>
        AtomicFile.ReadJsonAsync<PendingOperation>(_layout.PendingStatePath, ct);

    /// <summary>
    /// Brings the game folder back to a consistent state after a crash or forced quit, and moves a folder from the
    /// pre-.rhythmhub layout over. Must run before any other operation, while no other launcher instance is active.
    /// Throws <see cref="UpdateErrorCode.GameRunning"/> if that needs files the running game is using.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (LegacyLayout.IsPresent(_layout))
            {
                if (await LegacyLayout.ActiveEntrypointAsync(_layout, ct) is { } entrypoint &&
                    await _platform.IsGameRunningAsync(_layout.GameDirectory, entrypoint, ct))
                    throw new UpdateException(UpdateErrorCode.GameRunning, "Close the game so its folder can be reorganized.");
                await LegacyLayout.MigrateAsync(_layout, _logger, ct);
            }

            var pending = await GetPendingAsync(ct);
            var state = await _versions.LoadAsync(ct);
            if (pending is not null)
            {
                var objects = _layout.ObjectsDirectory(pending.Version);
                if (pending.Stage == PendingStage.Installing && state.ActiveVersion == pending.Version)
                {
                    _logger.LogInformation("Recovery: {Version} was committed before the interruption", pending.Version);
                    await ClearPendingAsync();
                }
                else if (pending.Stage == PendingStage.Installing)
                {
                    // Assembly moves objects out of the store, so some may be gone; the next download refills them.
                    _logger.LogInformation("Recovery: {Version} install was interrupted before commit", pending.Version);
                    await SavePendingAsync(pending with { Stage = PendingStage.Downloading }, ct);
                }
                else if (!Directory.Exists(objects))
                {
                    _logger.LogInformation("Recovery: nothing left of pending {Version}", pending.Version);
                    await ClearPendingAsync();
                }
                else
                {
                    _logger.LogInformation("Recovery: {Version} download can continue", pending.Version);
                }
            }
            // Drops half-built staging dirs and any version directory current.json does not reference.
            await _installer.PruneAsync(ct);
            CleanStrayDownloads(await GetPendingAsync(ct));
            await EnsureDeployedCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GameRelease?> CheckAsync(CancellationToken ct)
    {
        var pointer = await _manifests.FetchPointerAsync(_options, GameId, ct);
        var installed = await GetInstalledAsync(ct);
        var installedSha = installed is null ? null : (await _versions.LoadReleaseInfoAsync(installed.Version, ct))?.ManifestSha256;
        _logger.LogInformation("{Game}: installed {Installed}, channel {Channel} has {Remote}",
            GameId, installed?.Version ?? "none", _options.Channel, pointer.Version);
        // Any difference counts: the channel is authoritative, so pointing it at an older version is a deliberate rollback.
        if (installed?.Version == pointer.Version && installedSha == pointer.ManifestSha256) return null;

        var (manifest, bytes) = await _manifests.FetchReleaseAsync(_options, pointer, ct);
        var reusable = await ReusableShasAsync(ct);
        var downloadBytes = manifest.Files.Where(f => f.Size > 0 && !reusable.Contains(f.Sha256))
            .DistinctBy(f => f.Sha256).Sum(f => f.Size);
        return new GameRelease(GameId, manifest.Version, manifest.Platform, pointer.ManifestSha256, manifest, bytes, downloadBytes);
    }

    /// <summary>Bytes already fetched for <paramref name="release"/> by an earlier, interrupted attempt.</summary>
    public async Task<long> GetResumableBytesAsync(GameRelease release, CancellationToken ct)
    {
        if (await GetPendingAsync(ct) is not { } pending || !pending.IsFor(release)) return 0;
        var dir = _layout.ObjectsDirectory(release.Version);
        return Directory.Exists(dir) ? Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length) : 0;
    }

    /// <summary>
    /// Fills <c>downloads/{v}/</c> with one verified object per distinct file hash: copied from an installed version
    /// when possible, otherwise downloaded. Safe to call again after any interruption.
    /// </summary>
    public async Task DownloadAsync(GameRelease release, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _layout.EnsureCreated();
            var pending = await GetPendingAsync(ct);
            if (pending is not null && !pending.IsFor(release))
            {
                _logger.LogInformation("Abandoning pending {Old} in favour of {New}", pending.Version, release.Version);
                DeleteDirectory(_layout.ObjectsDirectory(pending.Version));
                pending = null;
            }
            pending ??= new PendingOperation { Version = release.Version, ManifestSha256 = release.ManifestSha256 };
            await SavePendingAsync(pending with { Stage = PendingStage.Downloading }, ct);

            var objects = _layout.ObjectsDirectory(release.Version);
            Directory.CreateDirectory(objects);
            var needed = release.Manifest.Files.Where(f => f.Size > 0).DistinctBy(f => f.Sha256)
                .Where(f => !IsVerifiedObject(release.Version, f)).ToList();

            EnsureDiskSpace(needed.Sum(f => f.Size) + await DeploymentGrowthAsync(release, ct));
            var toFetch = await ReuseLocalFilesAsync(release.Version, needed, progress, ct);
            await FetchAsync(release, toFetch, progress, ct);

            await SavePendingAsync(pending with { Stage = PendingStage.Downloaded }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Finishes laying out the active version in the game folder if that was interrupted; call before launching.
    /// </summary>
    public async Task EnsureDeployedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureDeployedCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureDeployedCoreAsync(CancellationToken ct)
    {
        var state = await _versions.LoadAsync(ct);
        if (state.ActiveVersion is not { } active || await _deployment.IsCompleteAsync(active, ct)) return;
        var manifest = await _versions.LoadManifestAsync(active, ct)
            ?? throw new UpdateException(UpdateErrorCode.NotInstalled, $"The cached copy of {active} is missing; reinstall it.");
        await EnsureNotRunningAsync(ct);
        _logger.LogInformation("Finishing the layout of {Game} {Version}", GameId, active);
        await _deployment.DeployAsync(active, manifest, CancellationToken.None);
    }

    /// <summary>
    /// Moves the whole game folder (game, saved settings and charts, .rhythmhub) to <paramref name="target"/>, which must
    /// be missing or empty. This service keeps pointing at the old path; create a new one for the target.
    /// </summary>
    public async Task MoveToAsync(string target, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureNotRunningAsync(ct);
            _logger.LogInformation("Moving {Game} from {From} to {To}", GameId, _layout.GameDirectory, target);
            if (!GameFolder.Move(_layout.GameDirectory, target, ct))
                _logger.LogWarning("Copied {Game} to {To}, but parts of {From} could not be removed", GameId, target, _layout.GameDirectory);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InstallAsync(GameRelease release, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (await GetPendingAsync(ct) is not { Stage: PendingStage.Downloaded } pending || !pending.IsFor(release))
                throw new UpdateException(UpdateErrorCode.DownloadIncomplete, "Release has not been downloaded.");
            await EnsureNotRunningAsync(ct);

            await SavePendingAsync(pending with { Stage = PendingStage.Installing }, ct);
            try
            {
                await _installer.InstallAsync(release, _platform.VerifyPlatformPackageAsync, ct);
            }
            catch
            {
                // Not committed: assembly may have consumed objects, so the next attempt re-runs the download step,
                // which only refills what is missing. Committed: only laying out the game folder failed, and
                // EnsureDeployedAsync finishes that.
                if ((await _versions.LoadAsync(CancellationToken.None)).ActiveVersion != release.Version)
                    await SavePendingAsync(pending with { Stage = PendingStage.Downloading }, CancellationToken.None);
                else
                    await ClearPendingAsync();
                throw;
            }
            await ClearPendingAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task VerifyInstalledAsync(CancellationToken ct)
    {
        var installed = await GetInstalledAsync(ct)
            ?? throw new UpdateException(UpdateErrorCode.NotInstalled, $"{GameId} is not installed.");
        await _platform.VerifyPlatformPackageAsync(installed.GameDirectory, installed.Entrypoint, ct);
    }

    public async Task RollbackAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureNotRunningAsync(ct);
            await _installer.RollbackAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records the result of a launch handshake for the active version.</summary>
    public async Task RecordLaunchAsync(string version, LaunchOutcome outcome, CancellationToken ct)
    {
        var state = await _versions.LoadAsync(ct);
        if (state.ActiveVersion != version) return;
        // A version that launched well before stays known-good after a later timeout; a crash revokes it.
        var next = outcome switch
        {
            LaunchOutcome.Healthy => true,
            LaunchOutcome.TimedOut => state.LastLaunchSuccessful,
            _ => false,
        };
        if (next != state.LastLaunchSuccessful)
            await _versions.SaveAsync(state with { LastLaunchSuccessful = next }, ct);
        _logger.LogInformation("{Game} {Version} launch outcome {Outcome}", GameId, version, outcome);
    }

    /// <summary>Hash → existing file in an installed version (active first, then the rollback target).</summary>
    private async Task<Dictionary<string, string>> LocalSourcesAsync(CancellationToken ct)
    {
        var state = await _versions.LoadAsync(ct);
        var sources = new Dictionary<string, string>();
        foreach (var version in new[] { state.ActiveVersion, state.PreviousGoodVersion })
        {
            if (version is null || await _versions.LoadManifestAsync(version, ct) is not { } manifest) continue;
            var root = _layout.VersionDirectory(version);
            foreach (var f in manifest.Files.Where(f => f.Size > 0))
            {
                if (sources.ContainsKey(f.Sha256)) continue;
                var path = Path.Combine(root, f.Path);
                if (File.Exists(path) && new FileInfo(path).Length == f.Size) sources[f.Sha256] = path;
            }
        }
        return sources;
    }

    private async Task<HashSet<string>> ReusableShasAsync(CancellationToken ct) => [.. (await LocalSourcesAsync(ct)).Keys];

    /// <summary>Copies unchanged files from installed versions into the object store; returns what still has to be fetched.</summary>
    private async Task<List<ManifestFile>> ReuseLocalFilesAsync(string version, List<ManifestFile> needed,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var sources = await LocalSourcesAsync(ct);
        var candidates = needed.Where(f => sources.ContainsKey(f.Sha256)).ToList();
        var total = candidates.Sum(f => f.Size);
        long done = 0;
        var remaining = needed.Where(f => !sources.ContainsKey(f.Sha256)).ToList();
        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var tmp = _layout.ObjectPath(version, file.Sha256) + ".copy";
            File.Copy(sources[file.Sha256], tmp, overwrite: true);
            // The installed copy could have been modified or damaged; only trust what hashes correctly.
            if (await FileHasher.ComputeSha256Async(tmp, ct) == file.Sha256)
            {
                File.Move(tmp, _layout.ObjectPath(version, file.Sha256), overwrite: true);
            }
            else
            {
                _logger.LogWarning("Local copy of {Path} is damaged; downloading it instead", file.Path);
                File.Delete(tmp);
                remaining.Add(file);
            }
            done += file.Size;
            progress?.Report(new DownloadProgress(done, total, DownloadStage.ReusingLocalFiles));
        }
        if (candidates.Count > 0)
            _logger.LogInformation("Reused {Count} unchanged files ({Bytes} bytes) from installed versions", candidates.Count, total);
        return remaining;
    }

    private async Task FetchAsync(GameRelease release, List<ManifestFile> files, IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var total = files.Sum(f => f.Size);
        long done = files.Sum(f => PartialLength(release.Version, f.Sha256));
        long lastReportTicks = 0;
        var clock = Stopwatch.StartNew();
        void OnBytes(long delta)
        {
            var now = Interlocked.Add(ref done, delta);
            var last = Interlocked.Read(ref lastReportTicks);
            if (clock.Elapsed.Ticks - last >= ProgressInterval.Ticks &&
                Interlocked.CompareExchange(ref lastReportTicks, clock.Elapsed.Ticks, last) == last)
                progress?.Report(new DownloadProgress(now, total));
        }

        _logger.LogInformation("Downloading {Count} files ({Bytes} bytes) for {Game} {Version}",
            files.Count, total, GameId, release.Version);
        progress?.Report(new DownloadProgress(done, total));

        // Largest first, so a slow big file starts early instead of trailing at the end.
        await Parallel.ForEachAsync(files.OrderByDescending(f => f.Size),
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallelDownloads, CancellationToken = ct },
            async (file, token) => await FetchOneAsync(release, file, OnBytes, token));

        progress?.Report(new DownloadProgress(Interlocked.Read(ref done), total));
    }

    private async Task FetchOneAsync(GameRelease release, ManifestFile file, Action<long> onBytes, CancellationToken ct)
    {
        var uri = FeedLayout.FileUri(_options.FeedBaseUri, GameId, release.Platform, release.Version, file.Path);
        var partial = _layout.PartialObjectPath(release.Version, file.Sha256);
        for (var attempt = 1; ; attempt++)
        {
            await _downloads.DownloadAsync(uri, partial, file.Size, onBytes, ct);
            var actual = await FileHasher.ComputeSha256Async(partial, ct);
            if (actual == file.Sha256)
            {
                File.Move(partial, _layout.ObjectPath(release.Version, file.Sha256), overwrite: true);
                return;
            }
            onBytes(-file.Size);
            File.Delete(partial);
            // One clean retry covers a corrupt resumed prefix; a second mismatch means the server copy is wrong.
            if (attempt == 2)
                throw new UpdateException(UpdateErrorCode.HashMismatch,
                    $"'{file.Path}' does not match the manifest (expected {file.Sha256}, got {actual}).");
            _logger.LogWarning("Hash mismatch for {Path}; downloading it again from scratch", file.Path);
        }
    }

    private bool IsVerifiedObject(string version, ManifestFile f)
    {
        // Objects are only renamed into place after their hash matched.
        var path = _layout.ObjectPath(version, f.Sha256);
        return File.Exists(path) && new FileInfo(path).Length == f.Size;
    }

    private long PartialLength(string version, string sha)
    {
        var path = _layout.PartialObjectPath(version, sha);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    private async Task EnsureNotRunningAsync(CancellationToken ct)
    {
        if (await GetInstalledAsync(ct) is { } installed &&
            await _platform.IsGameRunningAsync(installed.GameDirectory, installed.Entrypoint, ct))
            throw new UpdateException(UpdateErrorCode.GameRunning, "Close the game before updating.");
    }

    /// <summary>
    /// The game folder holds a copy of the active version beside its cached one; it grows by whatever the new version
    /// adds. (On APFS the copy is a clone and takes no space, but other volumes need it.)
    /// </summary>
    private async Task<long> DeploymentGrowthAsync(GameRelease release, CancellationToken ct)
    {
        var deployed = (await _deployment.LoadAsync(ct))?.Version;
        var current = deployed is null ? null : await _versions.LoadManifestAsync(deployed, ct);
        return Math.Max(0, release.InstalledSizeBytes - (current?.InstalledSizeBytes ?? 0));
    }

    /// <summary>Objects end up moved into the new version, so peak use is roughly the objects still to be stored.</summary>
    private void EnsureDiskSpace(long bytesToStore)
    {
        var required = bytesToStore + _options.DiskSafetyMarginBytes;
        long free;
        try
        {
            // On Unix any path on the volume works and finds the right mount; Windows wants the drive root.
            var path = _layout.GameDirectory;
            free = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(path)! : path).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            _logger.LogWarning(ex, "Could not read free space for {Dir}", _layout.GameDirectory);
            return;
        }
        if (free < required)
            throw new UpdateException(UpdateErrorCode.InsufficientDiskSpace,
                $"Need {required / 1048576} MB free, only {free / 1048576} MB available.");
    }

    private Task SavePendingAsync(PendingOperation pending, CancellationToken ct) =>
        AtomicFile.WriteJsonAsync(_layout.PendingStatePath, pending with { UpdatedAt = DateTimeOffset.UtcNow }, ct);

    private Task ClearPendingAsync()
    {
        File.Delete(_layout.PendingStatePath);
        return Task.CompletedTask;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    /// <summary>Anything in downloads/ that pending.json does not account for is garbage.</summary>
    private void CleanStrayDownloads(PendingOperation? pending)
    {
        if (!Directory.Exists(_layout.DownloadsDirectory)) return;
        var keep = pending is null ? null : _layout.ObjectsDirectory(pending.Version);
        foreach (var entry in Directory.EnumerateFileSystemEntries(_layout.DownloadsDirectory).ToList())
        {
            if (entry == keep) continue;
            _logger.LogInformation("Removing stray download {Entry}", entry);
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
    }
}
