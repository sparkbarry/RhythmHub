using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace RhythmHub.Updater.Tests;

/// <summary>The games' icons come from the feed ({gameId}/icon.png) and are kept for offline use.</summary>
public class GameIconCacheTests
{
    private const string G = Factory.Game;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Uri IconUri = FeedLayout.IconUri(FakeFeed.Base, G);

    private static byte[] Png(byte tail) => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, tail];

    private static GameIconCache Cache(TempDir tmp, FakeFeed feed) =>
        new(FakeFeed.Base, tmp.Path, new HttpClient(feed, disposeHandler: false), NullLogger.Instance);

    [Fact]
    public void The_icon_is_one_per_game_beside_its_platforms() =>
        Assert.Equal("https://cdn.test/Demo%20Game/icon.png", FeedLayout.IconUri(FakeFeed.Base, "Demo Game").AbsoluteUri);

    [Fact]
    public async Task An_icon_is_kept_and_an_unchanged_one_costs_a_304()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var cache = Cache(tmp, feed);
        feed.Put(IconUri, Png(1));

        Assert.Null(cache.Cached(G));
        Assert.True(await cache.RefreshAsync(G, Ct));
        Assert.Equal(Png(1), File.ReadAllBytes(cache.Cached(G)!));

        Assert.False(await cache.RefreshAsync(G, Ct));
        Assert.Equal(Png(1), File.ReadAllBytes(cache.Cached(G)!));
    }

    [Fact]
    public async Task A_replaced_icon_replaces_the_kept_one()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var cache = Cache(tmp, feed);
        feed.Put(IconUri, Png(1));
        await cache.RefreshAsync(G, Ct);

        feed.Put(IconUri, Png(2));

        Assert.True(await cache.RefreshAsync(G, Ct));
        Assert.Equal(Png(2), File.ReadAllBytes(cache.Cached(G)!));
    }

    [Fact]
    public async Task An_icon_taken_off_the_feed_is_forgotten()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var cache = Cache(tmp, feed);
        feed.Put(IconUri, Png(1));
        await cache.RefreshAsync(G, Ct);

        feed.Remove(IconUri); // S3 then says 403

        Assert.True(await cache.RefreshAsync(G, Ct));
        Assert.Null(cache.Cached(G));
        Assert.False(await cache.RefreshAsync(G, Ct));
    }

    [Fact]
    public async Task A_game_without_an_icon_on_the_feed_has_none_kept()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var cache = Cache(tmp, feed);

        Assert.False(await cache.RefreshAsync(G, Ct));
        Assert.Null(cache.Cached(G));
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "icons")));
    }

    [Fact]
    public async Task A_failing_feed_or_a_file_that_is_not_a_png_leaves_the_kept_icon()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var cache = Cache(tmp, feed);
        feed.Put(IconUri, Png(1));
        await cache.RefreshAsync(G, Ct);

        feed.FailWith = HttpStatusCode.ServiceUnavailable;
        Assert.False(await cache.RefreshAsync(G, Ct));
        feed.FailWith = null;

        feed.Put(IconUri, "<html>not an icon</html>"u8.ToArray());
        Assert.False(await cache.RefreshAsync(G, Ct));

        feed.Put(IconUri, [.. Png(3), .. new byte[GameIconCache.MaxBytes]]);
        Assert.False(await cache.RefreshAsync(G, Ct));

        Assert.Equal(Png(1), File.ReadAllBytes(cache.Cached(G)!));
    }
}
