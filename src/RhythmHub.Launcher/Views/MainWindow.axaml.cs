using Avalonia.Controls;
using Avalonia.Platform.Storage;
using RhythmHub.Launcher.Resources;
using RhythmHub.Launcher.ViewModels;

namespace RhythmHub.Launcher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsMacOS())
        {
            // Let the sidebar run under the traffic lights, as native macOS apps do.
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = 44;
            TitleBarSpacer.Height = 44;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm) vm.PickFolder = PickFolderAsync;
    }

    private async Task<string?> PickFolderAsync(GameViewModel game)
    {
        // Start at the current folder, or its nearest existing parent before the first install.
        var start = game.GameDirectory;
        while (!Directory.Exists(start) && Path.GetDirectoryName(start) is { } parent) start = parent;
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = string.Format(Strings.ChooseFolder_Title, game.DisplayName),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start),
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}
