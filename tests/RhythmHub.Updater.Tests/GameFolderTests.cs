using RhythmHub.Updater.Models;

namespace RhythmHub.Updater.Tests;

/// <summary>The game folder holds the live game; .rhythmhub/ inside it holds the cached versions and state.</summary>
public class GameFolderTests
{
    private const string G = Factory.Game;
    private const string P = Factory.Platform;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task InstallLatestAsync(GameUpdateService svc)
    {
        var release = await svc.CheckAsync(Ct);
        Assert.NotNull(release);
        await svc.DownloadAsync(release, null, Ct);
        await svc.InstallAsync(release, Ct);
    }

    private static string Read(GameUpdateService svc, string path) =>
        File.ReadAllText(Path.Combine(svc.Layout.GameDirectory, path));

    private static bool Exists(GameUpdateService svc, string path) =>
        File.Exists(Path.Combine(svc.Layout.GameDirectory, path));

    private static void Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task Game_runs_from_the_picked_folder_with_its_cache_inside()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        var folder = Path.Combine(tmp.Path, "My Games (x86)", "Demo");
        var svc = Factory.Service(tmp.Path, feed, gameDirectory: folder);

        await InstallLatestAsync(svc);

        var installed = (await svc.GetInstalledAsync(Ct))!;
        Assert.Equal(folder, installed.GameDirectory);
        Assert.Equal("data-v1", Read(svc, "Game.app/Contents/Resources/data.bin"));
        Assert.Equal(Path.Combine(folder, ".rhythmhub", "versions", "1.0.0"), svc.Layout.VersionDirectory("1.0.0"));
        Assert.True(File.Exists(Path.Combine(svc.Layout.VersionDirectory("1.0.0"), "Game.app/Contents/Resources/data.bin")));
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "games")));
    }

    [Fact]
    public async Task Files_the_game_saved_survive_update_and_rollback()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1").File("Game.app/Contents/Resources/old.bin", "old"));
        await InstallLatestAsync(svc);
        await svc.RecordLaunchAsync("1.0.0", LaunchOutcome.Healthy, Ct);

        // What the game writes beside its files (MajdataPlay and OurTaikoPlay keep settings next to the executable).
        Write(svc.Layout.GameDirectory, "Game.app/Contents/settings.json", "{\"volume\":3}");
        Write(svc.Layout.GameDirectory, "Charts/song/maidata.txt", "chart");

        feed.Publish(G, P, "1.1.0", Factory.GameBuild("v2").File("Game.app/Contents/Resources/new.bin", "new"));
        await InstallLatestAsync(svc);

        Assert.Equal("data-v2", Read(svc, "Game.app/Contents/Resources/data.bin"));
        Assert.True(Exists(svc, "Game.app/Contents/Resources/new.bin"));
        Assert.False(Exists(svc, "Game.app/Contents/Resources/old.bin"));
        Assert.Equal("{\"volume\":3}", Read(svc, "Game.app/Contents/settings.json"));
        Assert.Equal("chart", Read(svc, "Charts/song/maidata.txt"));

        await svc.RollbackAsync(Ct);

        Assert.Equal("data-v1", Read(svc, "Game.app/Contents/Resources/data.bin"));
        Assert.True(Exists(svc, "Game.app/Contents/Resources/old.bin"));
        Assert.False(Exists(svc, "Game.app/Contents/Resources/new.bin"));
        Assert.Equal("{\"volume\":3}", Read(svc, "Game.app/Contents/settings.json"));
        Assert.Equal("chart", Read(svc, "Charts/song/maidata.txt"));
    }

    [Fact]
    public async Task A_file_can_become_a_directory_between_versions()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1").File("Game.app/Contents/Resources/x", "file"));
        await InstallLatestAsync(svc);
        feed.Publish(G, P, "1.1.0", Factory.GameBuild("v2").File("Game.app/Contents/Resources/x/y.txt", "nested"));
        await InstallLatestAsync(svc);

        Assert.Equal("nested", Read(svc, "Game.app/Contents/Resources/x/y.txt"));
    }

    [Fact]
    public async Task Interrupted_layout_is_finished_on_recovery()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        await InstallLatestAsync(svc);

        // As if the launcher died halfway through laying out 1.0.0 over some other version.
        var deployed = await AtomicFile.ReadJsonAsync<DeploymentState>(svc.Layout.DeploymentStatePath, Ct);
        await AtomicFile.WriteJsonAsync(svc.Layout.DeploymentStatePath,
            deployed! with { Complete = false, Paths = [.. deployed.Paths, "Game.app/Contents/Resources/half.bin"] }, Ct);
        File.Delete(Path.Combine(svc.Layout.GameDirectory, "Game.app/Contents/Info.plist"));
        Write(svc.Layout.GameDirectory, "Game.app/Contents/Resources/half.bin", "from the other version");
        Write(svc.Layout.GameDirectory, "Game.app/Contents/Resources/data.bin", "half-written");

        var restarted = Factory.Service(tmp.Path, feed);
        await restarted.RecoverAsync(Ct);

        Assert.Equal("<plist/>", Read(restarted, "Game.app/Contents/Info.plist"));
        Assert.Equal("data-v1", Read(restarted, "Game.app/Contents/Resources/data.bin"));
        Assert.False(Exists(restarted, "Game.app/Contents/Resources/half.bin"));
        Assert.True((await AtomicFile.ReadJsonAsync<DeploymentState>(restarted.Layout.DeploymentStatePath, Ct))!.Complete);
    }

    [Fact]
    public async Task Unfinished_layout_waits_while_the_game_runs()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var platform = new FakePlatform();
        var svc = Factory.Service(tmp.Path, feed, platform);
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        await InstallLatestAsync(svc);
        File.Delete(svc.Layout.DeploymentStatePath);

        platform.Running = true;
        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.RecoverAsync(Ct));
        Assert.Equal(UpdateErrorCode.GameRunning, ex.Code);

        platform.Running = false;
        await svc.EnsureDeployedAsync(Ct);
        Assert.True((await AtomicFile.ReadJsonAsync<DeploymentState>(svc.Layout.DeploymentStatePath, Ct))!.Complete);
    }

    [Fact]
    public async Task Old_layout_moves_into_rhythmhub_and_keeps_saved_files()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        var svc = Factory.Service(tmp.Path, feed);
        await InstallLatestAsync(svc);

        // Rebuild what launchers up to 1.0.x left: state beside versions/, the game running inside versions/{v}.
        var game = svc.Layout.GameDirectory;
        Directory.Move(svc.Layout.VersionsDirectory, Path.Combine(game, "versions"));
        File.Move(svc.Layout.CurrentStatePath, Path.Combine(game, "current.json"));
        Directory.Delete(svc.Layout.DataDirectory, recursive: true);
        Directory.Delete(Path.Combine(game, "Game.app"), recursive: true);
        var legacyVersion = Path.Combine(game, "versions", "1.0.0");
        Write(legacyVersion, "Game.app/Contents/settings.json", "saved");
        Write(legacyVersion, "Charts/song/maidata.txt", "chart");

        var restarted = Factory.Service(tmp.Path, feed);
        await restarted.RecoverAsync(Ct);

        Assert.Equal("1.0.0", (await restarted.GetInstalledAsync(Ct))!.Version);
        Assert.Equal("data-v1", Read(restarted, "Game.app/Contents/Resources/data.bin"));
        Assert.Equal("saved", Read(restarted, "Game.app/Contents/settings.json"));
        Assert.Equal("chart", Read(restarted, "Charts/song/maidata.txt"));
        Assert.False(Directory.Exists(Path.Combine(game, "versions")));
        Assert.False(File.Exists(Path.Combine(game, "current.json")));
        Assert.False(File.Exists(Path.Combine(restarted.Layout.VersionDirectory("1.0.0"), "Game.app/Contents/settings.json")));
        Assert.Null(await restarted.CheckAsync(Ct));
    }

    [Fact]
    public async Task Moving_takes_the_game_and_its_saved_files_along()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        var svc = Factory.Service(tmp.Path, feed);
        await InstallLatestAsync(svc);
        Write(svc.Layout.GameDirectory, "settings.json", "saved");

        var occupied = Path.Combine(tmp.Path, "occupied");
        Write(occupied, "something.txt", "x");
        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.MoveToAsync(occupied, Ct));
        Assert.Equal(UpdateErrorCode.DestinationNotEmpty, ex.Code);

        var target = Path.Combine(tmp.Path, "elsewhere", "Demo");
        await svc.MoveToAsync(target, Ct);

        var moved = Factory.Service(tmp.Path, feed, gameDirectory: target);
        await moved.RecoverAsync(Ct);
        Assert.Equal("1.0.0", (await moved.GetInstalledAsync(Ct))!.Version);
        Assert.Equal("saved", Read(moved, "settings.json"));
        Assert.Equal("data-v1", Read(moved, "Game.app/Contents/Resources/data.bin"));
        Assert.Null(await moved.CheckAsync(Ct));
        Assert.False(Directory.Exists(svc.Layout.GameDirectory));
        Assert.Equal(G, GameFolder.OwnerOf(target));
    }

    [Fact]
    public async Task Picked_folder_is_used_as_is_only_when_it_fits_the_game()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var empty = Directory.CreateDirectory(Path.Combine(tmp.Path, "empty")).FullName;
        File.WriteAllText(Path.Combine(empty, ".DS_Store"), "");
        var busy = Path.Combine(tmp.Path, "Games");
        Write(busy, "other/readme.txt", "x");
        var named = Path.Combine(tmp.Path, "games2", G);
        Write(named, "leftover.txt", "x");
        var standalone = Path.Combine(tmp.Path, "standalone");
        Write(standalone, G + ".exe", "x");
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        var own = Path.Combine(tmp.Path, "own");
        await InstallLatestAsync(Factory.Service(tmp.Path, feed, gameDirectory: own));

        Assert.Equal(empty, GameFolder.Resolve(empty, G));
        Assert.Equal(Path.Combine(tmp.Path, "new"), GameFolder.Resolve(Path.Combine(tmp.Path, "new"), G));
        Assert.Equal(Path.Combine(busy, G), GameFolder.Resolve(busy, G));
        Assert.Equal(named, GameFolder.Resolve(named, G));
        Assert.Equal(standalone, GameFolder.Resolve(standalone, G));
        Assert.Equal(own, GameFolder.Resolve(own + Path.DirectorySeparatorChar, G));
    }

    [Fact]
    public void Folders_that_nest_with_other_games_are_refused()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var games = Path.Combine(Path.GetTempPath(), "Games");
        var current = Path.Combine(games, "Demo");
        string[] others = [Path.Combine(games, "Other")];

        Assert.Equal(UpdateErrorCode.InvalidDestination,
            Assert.Throws<UpdateException>(() => GameFolder.Validate(root, current, others)).Code);
        Assert.Equal(UpdateErrorCode.InvalidDestination,
            Assert.Throws<UpdateException>(() => GameFolder.Validate(Path.Combine(current, "sub"), current, others)).Code);
        Assert.Equal(UpdateErrorCode.InvalidDestination,
            Assert.Throws<UpdateException>(() => GameFolder.Validate(Path.Combine(games, "Other", "Demo"), current, others)).Code);
        Assert.Equal(UpdateErrorCode.InvalidDestination,
            Assert.Throws<UpdateException>(() => GameFolder.Validate(games, current, others)).Code);
        GameFolder.Validate(Path.Combine(games, "Demo2"), current, others);
    }
}
