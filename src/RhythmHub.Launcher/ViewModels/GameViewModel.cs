using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RhythmHub.Launcher.Resources;
using RhythmHub.Launcher.Services;
using RhythmHub.Updater;
using RhythmHub.Updater.Models;

namespace RhythmHub.Launcher.ViewModels;

public enum GameState
{
    Idle,
    Checking,
    NotInstalled,
    Ready,
    UpdateAvailable,
    Downloading,
    Verifying,
    Installing,
    Moving,
    Running,
    Failed,
}

public sealed partial class GameViewModel : ObservableObject
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromMinutes(2);

    private readonly LauncherContext _context;
    private readonly GameEntry _entry;
    private GameUpdateService _updater;
    private readonly ILogger _logger;
    private CancellationTokenSource? _operation;
    private bool _userCancelled;
    private GameRelease? _available;

    /// <summary>Recovery can be refused while the game runs; it is then retried before the next check.</summary>
    private bool _recovered;

    public GameViewModel(GameEntry entry, LauncherContext context)
    {
        _context = context;
        _entry = entry;
        Id = entry.Id;
        DisplayName = entry.DisplayName;
        Accent = Brush.Parse(entry.Color ?? "#0071E3");
        Icon = LoadIcon(entry.Icon);
        _logger = context.Loggers.CreateLogger($"Game.{entry.Id}");
        selectedChannel = ChannelOption.Find(context.Settings.GetChannel(entry.Id, context.Options.Channel));
        _updater = CreateUpdater(context.Settings.GetGameDirectory(entry.Id));
        Localizer.Instance.LanguageChanged += (_, _) => OnLanguageChanged();
    }

    /// <summary>Re-reads every string shown, so a language switch needs no restart.</summary>
    private void OnLanguageChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(Description), nameof(RevealFolderText), nameof(InstalledVersionShort), nameof(SidebarText),
                     nameof(PrimaryText), nameof(ShowCheckButton), nameof(StatusText),
                 })
            OnPropertyChanged(name);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Description => _entry.LocalizedDescription;
    public string Initial => DisplayName[..1];
    public IBrush Accent { get; }

    /// <summary>The game's icon; null shows the initial on the accent color instead.</summary>
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;

    /// <summary>The folder the game runs from; its saved settings and charts stay there across versions.</summary>
    public string GameDirectory => _updater.Layout.GameDirectory;

    /// <summary>Where the game lives, with the home directory shortened to ~.</summary>
    public string GameFolderText
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var root = GameDirectory;
            return OperatingSystem.IsWindows() || !root.StartsWith(home, StringComparison.Ordinal) ? root : "~" + root[home.Length..];
        }
    }

    public string RevealFolderText => OperatingSystem.IsMacOS() ? Strings.RevealInFinder : Strings.RevealInExplorer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(PrimaryText), nameof(ShowProgress), nameof(ShowStatus), nameof(CanChangeChannel),
        nameof(CanChangeFolder), nameof(IsFailed), nameof(SidebarText), nameof(ShowCheckButton))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand), nameof(CheckCommand), nameof(CancelCommand), nameof(RollbackCommand))]
    private GameState state;

    public IReadOnlyList<ChannelOption> Channels => ChannelOption.All;

    [ObservableProperty]
    private ChannelOption selectedChannel;

    /// <summary>Switching mid-download would orphan the transfer, and mid-game the install would be refused.</summary>
    public bool CanChangeChannel => !IsBusy && State != GameState.Running;

    /// <summary>The game's files can't be moved while it runs or while they are being written.</summary>
    public bool CanChangeFolder => !IsBusy && State != GameState.Running;

    /// <summary>Builds the status line; kept instead of the text so it can be rebuilt in a new language.</summary>
    private Func<string> _status = () => "";

    public string StatusText => _status();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledVersionShort), nameof(PrimaryText), nameof(SidebarText),
        nameof(HasInstall), nameof(ShowCheckButton))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand), nameof(OpenGameFolderCommand))]
    private InstalledGame? installed;

    [ObservableProperty]
    private string? remoteVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarText))]
    private double progressValue;

    [ObservableProperty]
    private bool isProgressIndeterminate;

    [ObservableProperty]
    private string progressText = "";

    [ObservableProperty]
    private bool canRollback;

    [ObservableProperty]
    private string? previousVersion;

    public bool IsBusy => State is GameState.Checking or GameState.Downloading or GameState.Verifying or GameState.Installing
        or GameState.Moving;
    public bool ShowProgress => State is GameState.Downloading or GameState.Verifying or GameState.Installing or GameState.Moving;
    public bool ShowStatus => !ShowProgress;

    /// <summary>The separate check button, hidden while the primary button already checks.</summary>
    public bool ShowCheckButton => !IsBusy && PrimaryText != CheckText;
    public bool IsFailed => State == GameState.Failed;
    public bool HasInstall => Installed is not null;
    public string InstalledVersionShort => Installed?.Version ?? Strings.NotInstalled;

    /// <summary>The one-line state under the game's name in the sidebar.</summary>
    public string SidebarText => State switch
    {
        GameState.Downloading or GameState.Verifying => string.Format(Strings.Sidebar_Downloading, ProgressValue.ToString("0")),
        GameState.Installing => Strings.Sidebar_Installing,
        GameState.Moving => Strings.Sidebar_Moving,
        GameState.Running => Strings.Sidebar_Running,
        _ => InstalledVersionShort,
    };

    public string PrimaryText => State switch
    {
        GameState.UpdateAvailable when Installed is null => Strings.Action_Install,
        GameState.UpdateAvailable => Strings.Action_Update,
        GameState.Checking => Strings.Action_Checking,
        GameState.Downloading or GameState.Verifying or GameState.Installing => Strings.Action_Updating,
        GameState.Moving => Strings.Sidebar_Moving,
        GameState.Running => Strings.Action_Running,
        GameState.Failed when _available is not null => Strings.Action_Retry,
        _ when Installed is not null => Strings.Action_Play,
        _ => CheckText,
    };

    private static string CheckText => Strings.Action_CheckForUpdates;

    partial void OnSelectedChannelChanged(ChannelOption value)
    {
        if (value is null || _updater.Channel == value.Id) return;
        _logger.LogInformation("Channel {Old} -> {New}", _updater.Channel, value.Id);
        _updater.Channel = value.Id;
        try
        {
            _context.Settings.SetChannel(Id, value.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the channel choice");
        }
        _available = null;
        _ = CheckAsync();
    }

    public async Task InitializeAsync()
    {
        try
        {
            await EnsureRecoveredAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery failed");
        }
        await RefreshInstalledAsync();
        await CheckAsync();
    }

    private GameUpdateService CreateUpdater(string? directory) =>
        new(Id, _context.Options, _context.Http, _context.Platform, _context.Loggers, gameDirectory: directory)
        {
            Channel = SelectedChannel.Id,
        };

    private async Task EnsureRecoveredAsync(CancellationToken ct)
    {
        if (_recovered) return;
        await _updater.RecoverAsync(ct);
        _recovered = true;
    }

    /// <summary>
    /// Makes <paramref name="picked"/> (or a subfolder named after the game, see <see cref="GameFolder.Resolve"/>) the game's
    /// folder. A folder holding this game's files already is taken over as is; otherwise the current folder moves there
    /// with everything in it, saved settings and charts included.
    /// </summary>
    public async Task ChangeFolderAsync(string picked, IReadOnlyCollection<string> otherGameFolders)
    {
        if (!CanChangeFolder) return;
        var current = GameDirectory;
        var target = GameFolder.Resolve(picked, Id);
        if (GameFolder.SamePath(target, current)) return;

        await RunAsync(GameState.Moving, () => Strings.Status_Moving, null, async _ =>
        {
            IsProgressIndeterminate = true;
            GameFolder.Validate(target, current, otherGameFolders);
            // Not cancellable: a half-moved folder is worse than waiting.
            if (GameFolder.OwnerOf(target) != Id && GameFolder.HasLauncherData(current))
                await _updater.MoveToAsync(target, CancellationToken.None);

            _context.Settings.SetGameDirectory(Id, target);
            _logger.LogInformation("Game folder {Old} -> {New}", current, target);
            _updater = CreateUpdater(target);
            _recovered = false;
            OnPropertyChanged(nameof(GameDirectory));
            OnPropertyChanged(nameof(GameFolderText));

            await EnsureRecoveredAsync(CancellationToken.None);
            await RefreshInstalledAsync();
            var folder = GameFolderText;
            SetState(_available is not null ? GameState.UpdateAvailable : Installed is null ? GameState.Idle : GameState.Ready,
                () => string.Format(Strings.Status_FolderChanged, folder));
        });
    }

    [RelayCommand(CanExecute = nameof(CanPrimary))]
    private async Task PrimaryAsync()
    {
        if (State is GameState.UpdateAvailable or GameState.Failed && _available is not null)
            await UpdateAsync(_available);
        else if (Installed is not null)
            await LaunchAsync();
        else
            await CheckAsync();
    }

    private bool CanPrimary() => !IsBusy && State != GameState.Running;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        await RunAsync(GameState.Checking, () => Strings.Status_Checking, CheckTimeout, async ct =>
        {
            if (!_recovered)
            {
                await EnsureRecoveredAsync(ct);
                await RefreshInstalledAsync();
            }
            var available = _available = await _updater.CheckAsync(ct);
            var installed = Installed?.Version;
            RemoteVersion = available?.Version ?? installed;
            if (available is null)
            {
                SetState(GameState.Ready, () => string.Format(Strings.Status_UpToDate, installed));
                return;
            }
            var resumable = await _updater.GetResumableBytesAsync(available, ct);
            var download = available.DownloadBytes;
            var channel = SelectedChannel;
            string Size() => resumable > 0
                ? string.Format(Strings.Size_Resumable, FormatSize(resumable))
                : installed is not null && download < available.InstalledSizeBytes
                    ? string.Format(Strings.Size_Delta, FormatSize(download), FormatSize(available.InstalledSizeBytes))
                    : FormatSize(download);
            SetState(GameState.UpdateAvailable, installed is null
                ? () => string.Format(Strings.Status_CanInstall, available.Version, Size())
                : () => string.Format(Strings.Status_ChannelLatest, channel.Label, available.Version, Size()));
        });
    }

    private bool CanCheck() => !IsBusy;

    private async Task UpdateAsync(GameRelease release)
    {
        await RunAsync(GameState.Downloading, () => string.Format(Strings.Status_Downloading, release.Version), null, async ct =>
        {
            var meter = new SpeedMeter();
            var stage = DownloadStage.Downloading;
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.Stage != stage)
                {
                    stage = p.Stage;
                    meter = new SpeedMeter();
                    SetStatus(stage == DownloadStage.ReusingLocalFiles
                        ? () => Strings.Status_Reusing
                        : () => string.Format(Strings.Status_Downloading, release.Version));
                }
                ProgressValue = p.TotalBytes == 0 ? 0 : 100.0 * p.CompletedBytes / p.TotalBytes;
                ProgressText = stage == DownloadStage.ReusingLocalFiles
                    ? $"{FormatSize(p.CompletedBytes)} / {FormatSize(p.TotalBytes)}"
                    : $"{FormatSize(p.CompletedBytes)} / {FormatSize(p.TotalBytes)}  ·  {FormatSize((long)meter.Sample(p.CompletedBytes))}/s";
            });
            await _updater.DownloadAsync(release, progress, ct);

            SetState(GameState.Installing, () => string.Format(Strings.Status_Installing, release.Version));
            IsProgressIndeterminate = true;
            ProgressText = "";
            await _updater.InstallAsync(release, ct);

            _available = null;
            await RefreshInstalledAsync();
            SetState(GameState.Ready, () => string.Format(Strings.Status_Installed, release.Version));
        });
    }

    private async Task LaunchAsync()
    {
        if (Installed is not { } game) return;
        try
        {
            if (await _context.Platform.IsGameRunningAsync(game.GameDirectory, game.Entrypoint, CancellationToken.None))
            {
                SetStatus(() => string.Format(Strings.Status_AlreadyRunning, DisplayName));
                return;
            }
            await _updater.EnsureDeployedAsync(CancellationToken.None);
            var ticket = _updater.Health.Prepare(game);
            _logger.LogInformation("Launching {Version} from {Dir}", game.Version, game.GameDirectory);
            await _context.Platform.LaunchGameAsync(game.GameDirectory, game.Entrypoint, ticket.Environment, CancellationToken.None);
            SetState(GameState.Running, () => string.Format(Strings.Status_Launching, game.Version));

            var outcome = await _updater.Health.WaitAsync(ticket, _entry.HealthCheck,
                ct => _context.Platform.IsGameRunningAsync(game.GameDirectory, game.Entrypoint, ct),
                HealthTimeout, CancellationToken.None);
            await _updater.RecordLaunchAsync(game.Version, outcome, CancellationToken.None);
            await RefreshInstalledAsync();

            switch (outcome)
            {
                case LaunchOutcome.Healthy:
                    SetState(GameState.Ready, () => string.Format(Strings.Status_Launched, game.Version));
                    break;
                case LaunchOutcome.Exited:
                    SetState(GameState.Failed, CanRollback
                        ? () => string.Format(Strings.Status_LaunchFailedRollback, game.Version)
                        : () => string.Format(Strings.Status_LaunchFailed, game.Version));
                    break;
                case LaunchOutcome.TimedOut:
                    var canRollback = CanRollback;
                    SetState(GameState.Ready, () => string.Format(Strings.Status_LaunchTimedOut, game.Version, HealthTimeout.TotalMinutes.ToString("0")) +
                        (canRollback ? Strings.Status_LaunchTimedOutRollback : ""));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launch failed");
            SetState(GameState.Failed, () => string.Format(Strings.Status_LaunchError, ex.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _userCancelled = true;
        _operation?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanRollbackNow))]
    private async Task RollbackAsync()
    {
        await RunAsync(GameState.Installing, () => Strings.Status_RollingBack, null, async ct =>
        {
            await _updater.RollbackAsync(ct);
            await RefreshInstalledAsync();
            var version = Installed?.Version;
            SetState(GameState.Ready, () => string.Format(Strings.Status_RolledBack, version));
        });
    }

    private bool CanRollbackNow() => !IsBusy && CanRollback;

    partial void OnCanRollbackChanged(bool value) => RollbackCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(HasInstall))]
    private void OpenGameFolder() => _context.Platform.RevealDirectory(GameDirectory);

    private async Task RefreshInstalledAsync()
    {
        Installed = await _updater.GetInstalledAsync(CancellationToken.None);
        var stateFile = await new VersionRepository(_updater.Layout).LoadAsync(CancellationToken.None);
        CanRollback = stateFile.PreviousGoodVersion is { } prev && prev != stateFile.ActiveVersion &&
                      Directory.Exists(_updater.Layout.VersionDirectory(prev));
        PreviousVersion = CanRollback ? stateFile.PreviousGoodVersion : null;
    }

    /// <summary>Runs one state-machine step; failures keep whatever version is installed playable.</summary>
    private async Task RunAsync(GameState busyState, Func<string> message, TimeSpan? timeout, Func<CancellationToken, Task> body)
    {
        using var cts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        _operation = cts;
        _userCancelled = false;
        ProgressValue = 0;
        ProgressText = "";
        IsProgressIndeterminate = false;
        SetState(busyState, message);
        try
        {
            await body(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            var timedOut = !_userCancelled && timeout is not null;
            var paused = _available is not null;
            var hint = OfflineHint();
            _logger.LogWarning("{Step} cancelled", busyState);
            SetState(paused ? GameState.UpdateAvailable : Installed is null ? GameState.Failed : GameState.Ready,
                () => (timedOut ? Strings.Status_TimedOut : paused ? Strings.Status_Paused : Strings.Status_Cancelled) + hint());
        }
        catch (UpdateException ex)
        {
            _logger.LogError(ex, "{Step} failed with {Code}", busyState, ex.Code);
            var channel = SelectedChannel;
            var hint = OfflineHint();
            SetState(GameState.Failed, () => (ex.Code == UpdateErrorCode.ManifestNotFound
                ? string.Format(Strings.Status_ChannelEmpty, channel.Label)
                : Describe(ex)) + hint());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Step} failed unexpectedly", busyState);
            var hint = OfflineHint();
            SetState(GameState.Failed, () => string.Format(Strings.Status_Error, ex.Message) + hint());
        }
        finally
        {
            _operation = null;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>The hint for the version installed now, rendered in whatever language is current.</summary>
    private Func<string> OfflineHint()
    {
        var version = Installed?.Version;
        return () => version is null ? "" : "\n" + string.Format(Strings.Status_OfflineHint, version);
    }

    private void SetState(GameState next, Func<string> message)
    {
        State = next;
        SetStatus(message);
    }

    private void SetStatus(Func<string> message)
    {
        _status = message;
        OnPropertyChanged(nameof(StatusText));
    }

    private static string Describe(UpdateException ex) => string.Format(Strings.Error_Detail, ex.Code switch
    {
        UpdateErrorCode.Network => Strings.Error_Network,
        UpdateErrorCode.Timeout => Strings.Error_Timeout,
        UpdateErrorCode.RateLimited => Strings.Error_RateLimited,
        UpdateErrorCode.PackageNotFound => Strings.Error_PackageNotFound,
        UpdateErrorCode.SignatureInvalid => Strings.Error_SignatureInvalid,
        UpdateErrorCode.ManifestNotFound => Strings.Error_ManifestNotFound,
        UpdateErrorCode.ManifestInvalid => Strings.Error_ManifestInvalid,
        UpdateErrorCode.LauncherTooOld => Strings.Error_LauncherTooOld,
        UpdateErrorCode.InsufficientDiskSpace => Strings.Error_InsufficientDiskSpace,
        UpdateErrorCode.DownloadIncomplete => Strings.Error_DownloadIncomplete,
        UpdateErrorCode.HashMismatch => Strings.Error_HashMismatch,
        UpdateErrorCode.UnsafeArchive or UpdateErrorCode.InvalidPackage => Strings.Error_InvalidPackage,
        UpdateErrorCode.GameRunning => Strings.Error_GameRunning,
        UpdateErrorCode.NoRollbackTarget => Strings.Error_NoRollbackTarget,
        UpdateErrorCode.DestinationNotEmpty => Strings.Error_DestinationNotEmpty,
        UpdateErrorCode.InvalidDestination => Strings.Error_InvalidDestination,
        _ => Strings.Error_UpdateFailed,
    }, ex.Message);

    private Bitmap? LoadIcon(string? path)
    {
        if (path is null) return null;
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://RhythmHub.Launcher/Assets/{path}"));
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
            _logger.LogWarning(ex, "Icon {Path} not found", path);
            return null;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>Bytes per second over a sliding ~2s window.</summary>
    private sealed class SpeedMeter
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Queue<(TimeSpan At, long Bytes)> _samples = new();

        public double Sample(long bytes)
        {
            var now = _clock.Elapsed;
            _samples.Enqueue((now, bytes));
            while (_samples.Count > 2 && now - _samples.Peek().At > TimeSpan.FromSeconds(2)) _samples.Dequeue();
            var (at, first) = _samples.Peek();
            var dt = (now - at).TotalSeconds;
            return dt <= 0 ? 0 : (bytes - first) / dt;
        }
    }
}
