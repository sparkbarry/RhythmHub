using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RhythmHub.Launcher.Resources;
using RhythmHub.Launcher.Services;

namespace RhythmHub.Launcher.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly LauncherContext _context;

    public MainWindowViewModel(LauncherContext context)
    {
        _context = context;
        Games = new ObservableCollection<GameViewModel>(
            context.Config.Games.Select(g => new GameViewModel(g, context)));
        selectedGame = Games.FirstOrDefault();
        selectedLanguage = LanguageOption.Find(context.Settings.Language);
        Localizer.Instance.LanguageChanged += (_, _) => OnLanguageChanged();
    }

    public IReadOnlyList<LanguageOption> Languages => LanguageOption.All;

    [ObservableProperty]
    private LanguageOption selectedLanguage;

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        // The combo box can briefly clear and restore its selection while binding; only a real change counts.
        if (value is null || value.Id == (_context.Settings.Language ?? "")) return;
        Localizer.Instance.SetLanguage(value.Id);
        try
        {
            _context.Settings.SetLanguage(value.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.Loggers.CreateLogger("Settings").LogWarning(ex, "Could not save the language choice");
        }
    }

    public ObservableCollection<GameViewModel> Games { get; }

    [ObservableProperty]
    private GameViewModel? selectedGame;

    public string LauncherVersionText => $"RhythmHub {_context.Options.LauncherVersion}";

    /// <summary>Version of a downloaded launcher update waiting for a restart; null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLauncherUpdate), nameof(LauncherUpdateTitle))]
    private string? launcherUpdateVersion;

    public bool HasLauncherUpdate => LauncherUpdateVersion is not null;

    public string LauncherUpdateTitle => string.Format(Strings.Launcher_UpdateAvailable, LauncherUpdateVersion);

    private bool _waitingForGames;

    public string LauncherUpdateText => _waitingForGames ? Strings.Launcher_WaitForGames : Strings.Launcher_UpdateReady;

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(LauncherUpdateTitle));
        OnPropertyChanged(nameof(LauncherUpdateText));
    }

    public async Task InitializeAsync()
    {
        _ = PrepareLauncherUpdateAsync();
        foreach (var game in Games)
            await game.InitializeAsync();
    }

    private async Task PrepareLauncherUpdateAsync()
    {
        try
        {
            LauncherUpdateVersion = await _context.SelfUpdate.PrepareAsync();
        }
        catch (Exception ex)
        {
            // A broken launcher feed must never block the games.
            _context.Loggers.CreateLogger("SelfUpdate").LogWarning(ex, "Launcher update check failed");
        }
    }

    [RelayCommand]
    private void RestartToUpdate()
    {
        // Restarting would abort a download or install in progress; the game itself keeps running.
        if (Games.Any(g => g.IsBusy))
        {
            _waitingForGames = true;
            OnPropertyChanged(nameof(LauncherUpdateText));
            return;
        }
        _context.SelfUpdate.ApplyAndRestart();
    }

    [RelayCommand]
    private void OpenLogs() => _context.Platform.RevealDirectory(_context.Loggers.LogDirectory);

    /// <summary>Shows the system folder picker starting near the given folder; set by the window, which owns the dialog.</summary>
    public Func<GameViewModel, Task<string?>>? PickFolder { get; set; }

    [RelayCommand]
    private async Task ChangeGameFolderAsync(GameViewModel game)
    {
        if (PickFolder is null || !game.CanChangeFolder) return;
        if (await PickFolder(game) is not { } picked) return;
        await game.ChangeFolderAsync(picked, [.. Games.Where(g => g != game).Select(g => g.GameDirectory)]);
    }
}
