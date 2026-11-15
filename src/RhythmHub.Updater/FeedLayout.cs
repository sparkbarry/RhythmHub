namespace RhythmHub.Updater;

/// <summary>
/// Object keys under the feed root (docs/update-protocol.md §2). Manifests never contain absolute URLs,
/// so moving the feed to another domain does not require re-signing.
/// </summary>
public static class FeedLayout
{
    public static Uri ChannelUri(Uri feed, string gameId, string platform, string channel) =>
        Combine(feed, gameId, platform, channel + ".json");

    public static Uri ManifestUri(Uri feed, string gameId, string platform, string version) =>
        Combine(feed, gameId, platform, version + ".json");

    public static Uri FileUri(Uri feed, string gameId, string platform, string version, string path) =>
        Combine(feed, [gameId, platform, version, .. path.Split('/')]);

    /// <summary>The game's icon, one per game for every platform; the maintainers replace it by hand.</summary>
    public static Uri IconUri(Uri feed, string gameId) => Combine(feed, gameId, "icon.png");

    private static Uri Combine(Uri feed, params string[] segments)
    {
        var root = feed.AbsoluteUri.EndsWith('/') ? feed.AbsoluteUri : feed.AbsoluteUri + "/";
        // Escape every segment: S3 keys may contain spaces, '+', or non-ASCII.
        return new Uri(root + string.Join('/', segments.Select(Uri.EscapeDataString)));
    }
}
