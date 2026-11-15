using System.Reflection;
using Microsoft.Extensions.Logging;
using RhythmHub.Platform;
using RhythmHub.Updater;

namespace RhythmHub.Launcher.Services;

/// <summary>Composition root: one shared HttpClient, logger and platform for all games.</summary>
public sealed class LauncherContext : IDisposable
{
    private LauncherContext(LauncherConfig config, IGamePlatform platform, UpdaterOptions options,
        HttpClient http, FileLoggerFactory loggers, InstanceLock? instanceLock)
    {
        InstanceLock = instanceLock;
        Settings = LauncherSettings.Load(options.InstallRoot);
        SelfUpdate = new LauncherSelfUpdate(options.FeedBaseUri, options.PlatformId, loggers.CreateLogger("SelfUpdate"));
        Config = config;
        Platform = platform;
        Options = options;
        Http = http;
        Loggers = loggers;
        Icons = new GameIconCache(options.FeedBaseUri, options.InstallRoot, http, loggers.CreateLogger("Icons"));
    }

    /// <summary>Redirects all game data for local testing so the real install stays untouched.</summary>
    public const string InstallRootOverrideVariable = "RHYTHMHUB_INSTALL_ROOT";

    public LauncherConfig Config { get; }
    public IGamePlatform Platform { get; }
    public UpdaterOptions Options { get; }
    public HttpClient Http { get; }
    public FileLoggerFactory Loggers { get; }
    public LauncherSelfUpdate SelfUpdate { get; }
    public LauncherSettings Settings { get; }

    /// <summary>The games' icons from the feed, kept for offline use.</summary>
    public GameIconCache Icons { get; }

    /// <summary>Null when another launcher already owns this install root.</summary>
    public InstanceLock? InstanceLock { get; }

    public static LauncherContext Create()
    {
        var config = LauncherConfig.Load();
        var platform = GamePlatform.Current();
        var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        var options = new UpdaterOptions
        {
            InstallRoot = Environment.GetEnvironmentVariable(InstallRootOverrideVariable) is { Length: > 0 } root
                ? Path.GetFullPath(root)
                : platform.DefaultInstallRoot,
            PlatformId = platform.PlatformId,
            FeedBaseUri = new Uri(config.FeedBaseUrl),
            Channel = config.Channel,
            LauncherVersion = new Version(version.Major, version.Minor, Math.Max(version.Build, 0)),
            TrustedKeys = ReleaseKeys.Load(),
        };
        var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // Never ask for Content-Encoding: game files are checked byte-for-byte against the manifest
            // and resumed with Range requests, both of which assume the CDN serves the stored bytes.
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        })
        {
            // Per-request timeouts are handled by cancellation; a whole-download timeout would break slow links.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RhythmHub/{options.LauncherVersion} ({options.PlatformId})");
        var instanceLock = InstanceLock.TryAcquire(options.InstallRoot);
        var loggers = new FileLoggerFactory(Path.Combine(options.InstallRoot, "logs"));
        if (options.TrustedKeys.Count == 0)
            loggers.CreateLogger("Launcher").LogWarning("No trusted release keys configured; every manifest will be rejected");
        return new LauncherContext(config, platform, options, http, loggers, instanceLock);
    }

    public void Dispose()
    {
        InstanceLock?.Dispose();
        Http.Dispose();
        Loggers.Dispose();
    }
}
