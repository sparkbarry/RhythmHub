namespace RhythmHub.Updater.Tests;

public class GameUpdateServiceTests
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

    [Fact]
    public async Task First_install_then_up_to_date()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        var svc = Factory.Service(tmp.Path, feed);

        Assert.Null(await svc.GetInstalledAsync(Ct));
        await InstallLatestAsync(svc);

        var installed = await svc.GetInstalledAsync(Ct);
        Assert.NotNull(installed);
        Assert.Equal("1.0.0", installed.Version);
        Assert.Equal("data-v1", File.ReadAllText(Path.Combine(installed.GameDirectory, "Game.app/Contents/Resources/data.bin")));
        Assert.Null(await svc.CheckAsync(Ct));
        Assert.Empty(Directory.EnumerateFileSystemEntries(svc.Layout.DownloadsDirectory));
        Assert.Empty(Directory.GetDirectories(svc.Layout.StagingDirectory));
        Assert.Null(await svc.GetPendingAsync(Ct));
    }

    [Fact]
    public async Task Switching_channel_installs_what_that_channel_points_at()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        feed.Publish(G, P, "1.1.0-abc1234", Factory.GameBuild("nightly"), channel: "nightly");
        var svc = Factory.Service(tmp.Path, feed);

        await InstallLatestAsync(svc);
        Assert.Equal("1.0.0", (await svc.GetInstalledAsync(Ct))!.Version);

        svc.Channel = "nightly";
        await InstallLatestAsync(svc);
        Assert.Equal("1.1.0-abc1234", (await svc.GetInstalledAsync(Ct))!.Version);
        Assert.Null(await svc.CheckAsync(Ct));

        // Back to stable goes to the older stable release.
        svc.Channel = "stable";
        await InstallLatestAsync(svc);
        Assert.Equal("1.0.0", (await svc.GetInstalledAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Missing_channel_is_reported_as_not_found()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        feed.Publish(G, P, "1.1.0-abc1234", Factory.GameBuild("nightly"), channel: "nightly");
        var svc = Factory.Service(tmp.Path, feed);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.CheckAsync(Ct));
        Assert.Equal(UpdateErrorCode.ManifestNotFound, ex.Code);
        svc.Channel = "nightly";
        Assert.NotNull(await svc.CheckAsync(Ct));
    }

    [Fact]
    public async Task Update_downloads_only_changed_files()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        var v1 = Factory.GameBuild("v1").File("Game.app/Contents/Resources/music.ogg", new string('m', 5000));
        feed.Publish(G, P, "1.0.0", v1);
        await InstallLatestAsync(svc);

        // 1.1.0 changes one file and moves another to a new path with identical content.
        var v2 = v1.Clone().File("Game.app/Contents/Resources/data.bin", "data-v2");
        v2.Files.Remove("Game.app/Contents/Resources/music.ogg");
        v2.File("Game.app/Contents/Resources/Audio/music.ogg", new string('m', 5000));
        feed.Publish(G, P, "1.1.0", v2);
        feed.Log.Clear();

        var release = (await svc.CheckAsync(Ct))!;
        Assert.Equal("data-v2".Length, release.DownloadBytes);
        await svc.DownloadAsync(release, null, Ct);
        await svc.InstallAsync(release, Ct);

        var fetched = feed.FileRequests.Select(r => r.Uri.AbsolutePath).ToList();
        Assert.Equal(["/demo/MacOS/1.1.0/Game.app/Contents/Resources/data.bin"], fetched);
        var dir = (await svc.GetInstalledAsync(Ct))!.GameDirectory;
        Assert.Equal(new string('m', 5000), File.ReadAllText(Path.Combine(dir, "Game.app/Contents/Resources/Audio/music.ogg")));
        Assert.False(File.Exists(Path.Combine(dir, "Game.app/Contents/Resources/music.ogg")));
    }

    [Fact]
    public async Task Damaged_local_file_is_downloaded_instead_of_reused()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        await InstallLatestAsync(svc);
        // Reuse copies from the cached version, not the game folder the player can touch.
        var plist = Path.Combine(svc.Layout.VersionDirectory("1.0.0"), "Game.app/Contents/Info.plist");
        File.WriteAllText(plist, "<XXXX>"); // same length, different bytes

        feed.Publish(G, P, "1.1.0", Factory.GameBuild("v2"));
        feed.Log.Clear();
        await InstallLatestAsync(svc);

        Assert.Contains(feed.FileRequests, r => r.Uri.AbsolutePath.EndsWith("Info.plist"));
        var dir = (await svc.GetInstalledAsync(Ct))!.GameDirectory;
        Assert.Equal("<plist/>", File.ReadAllText(Path.Combine(dir, "Game.app/Contents/Info.plist")));
    }

    [Fact]
    public async Task Duplicate_and_empty_files_are_assembled()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var build = Factory.GameBuild("v1")
            .File("Game.app/Contents/Resources/a.txt", "same")
            .File("Game.app/Contents/Resources/b.txt", "same")
            .File("Game.app/Contents/Resources/empty.txt", "")
            .Dir("Game.app/Contents/PlugIns");
        feed.Publish(G, P, "1.0.0", build);
        var svc = Factory.Service(tmp.Path, feed);
        await InstallLatestAsync(svc);

        var dir = (await svc.GetInstalledAsync(Ct))!.GameDirectory;
        Assert.Equal("same", File.ReadAllText(Path.Combine(dir, "Game.app/Contents/Resources/a.txt")));
        Assert.Equal("same", File.ReadAllText(Path.Combine(dir, "Game.app/Contents/Resources/b.txt")));
        Assert.Equal(0, new FileInfo(Path.Combine(dir, "Game.app/Contents/Resources/empty.txt")).Length);
        Assert.True(Directory.Exists(Path.Combine(dir, "Game.app/Contents/PlugIns")));
        Assert.Single(feed.FileRequests, r => r.Uri.AbsolutePath.EndsWith("/a.txt") || r.Uri.AbsolutePath.EndsWith("/b.txt"));
    }

    [Fact]
    public async Task Symlinks_and_permissions_are_recreated()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permissions and symlinks only.");
            return;
        }
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var build = Factory.GameBuild("v1")
            .File("Game.app/Contents/Frameworks/F.framework/Versions/A/F", "bin", mode: 0b111_101_101)
            .Link("Game.app/Contents/Frameworks/F.framework/Versions/Current", "A")
            .Link("Game.app/Contents/Frameworks/F.framework/F", "Versions/Current/F");
        var manifest = feed.Publish(G, P, "1.0.0", build);
        Assert.Equal(2, manifest.Symlinks.Count);
        Assert.Equal(0b111_101_101, manifest.Files.Single(f => f.Path.EndsWith("MacOS/Game")).Mode);

        var svc = Factory.Service(tmp.Path, feed);
        await InstallLatestAsync(svc);
        var dir = (await svc.GetInstalledAsync(Ct))!.GameDirectory;

        Assert.True((File.GetUnixFileMode(Path.Combine(dir, "Game.app/Contents/MacOS/Game")) & UnixFileMode.UserExecute) != 0);
        Assert.Equal(0b110_100_100, (int)File.GetUnixFileMode(Path.Combine(dir, "Game.app/Contents/Info.plist")));
        var current = new DirectoryInfo(Path.Combine(dir, "Game.app/Contents/Frameworks/F.framework/Versions/Current"));
        Assert.Equal("A", current.LinkTarget);
        Assert.Equal("bin", File.ReadAllText(Path.Combine(dir, "Game.app/Contents/Frameworks/F.framework/F")));
        Assert.DoesNotContain(feed.FileRequests, r => r.Uri.AbsolutePath.Contains("Current"));
    }

    [Fact]
    public async Task Update_keeps_previous_version_and_can_roll_back()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        await InstallLatestAsync(svc);
        feed.Publish(G, P, "1.1.0", Factory.GameBuild("v2"));
        await InstallLatestAsync(svc);

        var state = await svc.GetStateAsync(Ct);
        Assert.Equal("1.1.0", state.ActiveVersion);
        Assert.Equal("1.0.0", state.PreviousGoodVersion);
        Assert.True(Directory.Exists(svc.Layout.VersionDirectory("1.0.0")));

        await svc.RollbackAsync(Ct);
        Assert.Equal("1.0.0", (await svc.GetInstalledAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Prunes_versions_older_than_previous_good()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        foreach (var v in new[] { "1.0.0", "1.1.0", "1.2.0" })
        {
            feed.Publish(G, P, v, Factory.GameBuild(v));
            await InstallLatestAsync(svc);
        }

        // 1.1.0 never launched successfully, so 1.0.0 stays the rollback target and 1.1.0 is dropped.
        Assert.Equal(["1.0.0", "1.2.0"], Directory.GetDirectories(svc.Layout.VersionsDirectory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Healthy_version_becomes_rollback_target()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        foreach (var v in new[] { "1.0.0", "1.1.0", "1.2.0" })
        {
            feed.Publish(G, P, v, Factory.GameBuild(v));
            await InstallLatestAsync(svc);
            await svc.RecordLaunchAsync(v, LaunchOutcome.Healthy, Ct);
        }

        Assert.Equal("1.1.0", (await svc.GetStateAsync(Ct)).PreviousGoodVersion);
        Assert.Equal(["1.1.0", "1.2.0"], Directory.GetDirectories(svc.Layout.VersionsDirectory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Rollback_does_not_offer_the_abandoned_version_again()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        await InstallLatestAsync(svc);
        await svc.RecordLaunchAsync("1.0.0", LaunchOutcome.Healthy, Ct);
        feed.Publish(G, P, "1.1.0", Factory.GameBuild("v2"));
        await InstallLatestAsync(svc);
        await svc.RecordLaunchAsync("1.1.0", LaunchOutcome.Exited, Ct);

        await svc.RollbackAsync(Ct);

        var state = await svc.GetStateAsync(Ct);
        Assert.Equal("1.0.0", state.ActiveVersion);
        Assert.Null(state.PreviousGoodVersion);
        Assert.True(state.LastLaunchSuccessful);
    }

    [Fact]
    public async Task Server_file_with_wrong_content_is_rejected_after_one_retry()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        await InstallLatestAsync(svc);

        feed.Publish(G, P, "1.1.0", Factory.GameBuild("v2"));
        feed.Put(feed.FileUri(G, P, "1.1.0", "Game.app/Contents/Resources/data.bin"), "data-XX"u8.ToArray());
        feed.Log.Clear();
        var release = (await svc.CheckAsync(Ct))!;
        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.DownloadAsync(release, null, Ct));

        Assert.Equal(UpdateErrorCode.HashMismatch, ex.Code);
        Assert.Equal(2, feed.FileRequests.Count(r => r.Uri.AbsolutePath.EndsWith("data.bin")));
        Assert.Equal("1.0.0", (await svc.GetInstalledAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Missing_server_file_reports_package_not_found()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        feed.Remove(feed.FileUri(G, P, "1.0.0", "Game.app/Contents/Info.plist"));
        var svc = Factory.Service(tmp.Path, feed);

        var release = (await svc.CheckAsync(Ct))!;
        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.DownloadAsync(release, null, Ct));
        Assert.Equal(UpdateErrorCode.PackageNotFound, ex.Code);
    }

    [Fact]
    public async Task Platform_rejection_is_not_activated_and_can_be_retried()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var platform = new FakePlatform();
        var svc = Factory.Service(tmp.Path, feed, platform);
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        await InstallLatestAsync(svc);
        feed.Publish(G, P, "1.1.0", Factory.GameBuild("v2"));

        var release = (await svc.CheckAsync(Ct))!;
        await svc.DownloadAsync(release, null, Ct);
        platform.RejectPackage = true;
        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.InstallAsync(release, Ct));
        Assert.Equal(UpdateErrorCode.InvalidPackage, ex.Code);
        Assert.Equal("1.0.0", (await svc.GetInstalledAsync(Ct))!.Version);
        Assert.False(Directory.Exists(svc.Layout.VersionDirectory("1.1.0")));
        Assert.Empty(Directory.GetDirectories(svc.Layout.StagingDirectory));

        // Assembly moved objects out; the download step refills them and the install then succeeds.
        platform.RejectPackage = false;
        await svc.DownloadAsync(release, null, Ct);
        await svc.InstallAsync(release, Ct);
        Assert.Equal("1.1.0", (await svc.GetInstalledAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Refuses_to_install_while_game_runs()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var platform = new FakePlatform();
        var svc = Factory.Service(tmp.Path, feed, platform);
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        await InstallLatestAsync(svc);

        feed.Publish(G, P, "1.1.0", Factory.GameBuild("v2"));
        var release = (await svc.CheckAsync(Ct))!;
        await svc.DownloadAsync(release, null, Ct);
        platform.Running = true;

        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.InstallAsync(release, Ct));
        Assert.Equal(UpdateErrorCode.GameRunning, ex.Code);
        Assert.Equal("1.0.0", (await svc.GetInstalledAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Missing_release_reports_not_found()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Factory.Service(tmp.Path, feed).CheckAsync(Ct));
        Assert.Equal(UpdateErrorCode.ManifestNotFound, ex.Code);
    }

    [Fact]
    public async Task Unsigned_channel_is_rejected_before_any_file_request()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"), sign: false);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Factory.Service(tmp.Path, feed).CheckAsync(Ct));
        Assert.Equal(UpdateErrorCode.SignatureInvalid, ex.Code);
        Assert.Empty(feed.FileRequests);
    }

    [Fact]
    public async Task Manifest_not_matching_pointer_is_rejected()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var manifest = feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        // A validly signed but different manifest swapped in under the same name (e.g. an older signing of 1.0.0).
        feed.Put(FeedLayout.ManifestUri(FakeFeed.Base, G, P, "1.0.0"),
            FakeFeed.SignedBytes(manifest with { MinimumLauncherVersion = "0.0.1" }));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Factory.Service(tmp.Path, feed).CheckAsync(Ct));
        Assert.Equal(UpdateErrorCode.SignatureInvalid, ex.Code);
    }

    [Fact]
    public async Task Too_old_launcher_is_refused_before_download()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"), minLauncher: "9.0.0");

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Factory.Service(tmp.Path, feed).CheckAsync(Ct));
        Assert.Equal(UpdateErrorCode.LauncherTooOld, ex.Code);
        Assert.Empty(feed.FileRequests);
    }

    [Fact]
    public async Task Rollback_without_previous_version_fails_cleanly()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        await InstallLatestAsync(svc);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.RollbackAsync(Ct));
        Assert.Equal(UpdateErrorCode.NoRollbackTarget, ex.Code);
    }

    [Fact]
    public async Task Games_are_isolated_by_id()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        await InstallLatestAsync(Factory.Service(tmp.Path, feed));

        Assert.True(File.Exists(Path.Combine(tmp.Path, "games", G, ".rhythmhub", "current.json")));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "games", G, "Game.app", "Contents", "Info.plist")));
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "games", "other")));
    }
}
