using System.ComponentModel;
using System.Text.Json;
using RhythmHub.Launcher.Resources;

namespace RhythmHub.Launcher.Services;

/// <summary>A release channel the player can pick for a game; its labels follow the UI language.</summary>
public sealed class ChannelOption : INotifyPropertyChanged
{
    public static readonly IReadOnlyList<ChannelOption> All =
    [
        new("stable", nameof(Strings.Channel_Stable), nameof(Strings.Channel_Stable)),
        new("nightly", nameof(Strings.Channel_Nightly), nameof(Strings.Channel_NightlyShort)),
    ];

    private readonly string _labelKey;
    private readonly string _shortLabelKey;

    private ChannelOption(string id, string labelKey, string shortLabelKey)
    {
        Id = id;
        _labelKey = labelKey;
        _shortLabelKey = shortLabelKey;
        // The options live as long as the app, so the handler never needs removing.
        Localizer.Instance.LanguageChanged += (_, _) =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShortLabel)));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }
    public string Label => Localizer.Instance[_labelKey];

    /// <summary>The label in the segmented channel switch.</summary>
    public string ShortLabel => Localizer.Instance[_shortLabelKey];

    public static ChannelOption Find(string id) => All.FirstOrDefault(c => c.Id == id) ?? All[0];
}

/// <summary>A UI language the player can pick; an empty id follows the system.</summary>
public sealed class LanguageOption : INotifyPropertyChanged
{
    public static readonly IReadOnlyList<LanguageOption> All =
    [
        new("", null),
        new("zh", "中文"),
        new("en", "English"),
        new("ja", "日本語"),
    ];

    /// <summary>The language's own name; null for the system entry, whose label is translated.</summary>
    private readonly string? _nativeName;

    private LanguageOption(string id, string? nativeName)
    {
        Id = id;
        _nativeName = nativeName;
        if (nativeName is null)
            Localizer.Instance.LanguageChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }
    public string Label => _nativeName ?? Localizer.Instance[nameof(Strings.Language_System)];

    public static LanguageOption Find(string? id) => All.FirstOrDefault(l => l.Id == id) ?? All[0];
}

/// <summary>Per-user launcher preferences in {installRoot}/settings.json; survives launcher updates.</summary>
public sealed class LauncherSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _lock = new();
    private Data _data;

    private LauncherSettings(string path, Data data)
    {
        _path = path;
        _data = data;
    }

    public static LauncherSettings Load(string installRoot)
    {
        var path = Path.Combine(installRoot, "settings.json");
        Data? data = null;
        try
        {
            if (File.Exists(path))
                data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            // A damaged settings file only loses preferences; start over.
        }
        return new LauncherSettings(path, data ?? new Data());
    }

    /// <summary>The channel picked for <paramref name="gameId"/>, or <paramref name="fallback"/> when none was.</summary>
    public string GetChannel(string gameId, string fallback)
    {
        lock (_lock)
            return _data.Channels.TryGetValue(gameId, out var channel) ? channel : fallback;
    }

    public void SetChannel(string gameId, string channel)
    {
        lock (_lock)
            Save(_data with { Channels = new Dictionary<string, string>(_data.Channels) { [gameId] = channel } });
    }

    /// <summary>The folder picked for <paramref name="gameId"/>, or null for the default one under the install root.</summary>
    public string? GetGameDirectory(string gameId)
    {
        lock (_lock)
            return _data.GameDirectories.GetValueOrDefault(gameId);
    }

    public void SetGameDirectory(string gameId, string directory)
    {
        lock (_lock)
            Save(_data with { GameDirectories = new Dictionary<string, string>(_data.GameDirectories) { [gameId] = directory } });
    }

    /// <summary>The picked UI language ("zh", "en", "ja"), or null to follow the system.</summary>
    public string? Language
    {
        get
        {
            lock (_lock)
                return _data.Language;
        }
    }

    public void SetLanguage(string? language)
    {
        lock (_lock)
            Save(_data with { Language = string.IsNullOrEmpty(language) ? null : language });
    }

    private void Save(Data data)
    {
        _data = data;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_data, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed record Data
    {
        public Dictionary<string, string> Channels { get; init; } = [];
        public Dictionary<string, string> GameDirectories { get; init; } = [];
        public string? Language { get; init; }
    }
}
