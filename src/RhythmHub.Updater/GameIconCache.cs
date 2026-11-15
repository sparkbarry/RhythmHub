using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace RhythmHub.Updater;

/// <summary>
/// Each game's icon from the feed (<c>{gameId}/icon.png</c>, served <c>no-cache</c> so a replaced icon shows at once),
/// kept under <c>{installRoot}/icons/</c> so it shows offline and before the feed answers. The ETag of the copy is
/// sent back, so an unchanged icon costs a 304.
/// </summary>
public sealed class GameIconCache(Uri feedBaseUri, string installRoot, HttpClient http, ILogger logger)
{
    /// <summary>Icons are small; anything larger is not one.</summary>
    internal const int MaxBytes = 2 * 1024 * 1024;

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private string Directory => Path.Combine(installRoot, "icons");

    /// <summary>Where the game's icon is kept; it may not exist.</summary>
    public string PathOf(string gameId) => Path.Combine(Directory, gameId + ".png");

    private string ETagPath(string gameId) => Path.Combine(Directory, gameId + ".etag");

    /// <summary>The kept icon's path, or null when there is none.</summary>
    public string? Cached(string gameId) => File.Exists(PathOf(gameId)) ? PathOf(gameId) : null;

    /// <summary>Asks the feed for the game's icon.</summary>
    /// <returns>True when the kept icon changed: replaced by a new one, or removed because the feed has none.</returns>
    public async Task<bool> RefreshAsync(string gameId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, FeedLayout.IconUri(feedBaseUri, gameId));
            if (Cached(gameId) is not null && ReadETag(gameId) is { } etag)
                request.Headers.IfNoneMatch.Add(etag);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotModified:
                    return false;
                // S3 behind the CDN answers 403 for a key that does not exist.
                case HttpStatusCode.NotFound or HttpStatusCode.Forbidden:
                    return Forget(gameId);
                case HttpStatusCode.OK:
                    break;
                default:
                    logger.LogInformation("Icon of {Game}: the feed answered {Status}", gameId, (int)response.StatusCode);
                    return false;
            }
            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                logger.LogWarning("Icon of {Game} is larger than {Max} bytes; ignored", gameId, MaxBytes);
                return false;
            }
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            if (bytes.Length > MaxBytes || !bytes.AsSpan().StartsWith(PngSignature))
            {
                logger.LogWarning("Icon of {Game} is not a PNG of at most {Max} bytes; ignored", gameId, MaxBytes);
                return false;
            }
            if (Cached(gameId) is { } kept && await File.ReadAllBytesAsync(kept, timeout.Token) is var old && bytes.AsSpan().SequenceEqual(old))
            {
                SaveETag(gameId, response.Headers.ETag);
                return false;
            }
            Save(gameId, bytes);
            SaveETag(gameId, response.Headers.ETag);
            logger.LogInformation("Icon of {Game} updated ({Bytes} bytes)", gameId, bytes.Length);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException ||
                                   ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Offline, or a slow CDN: the kept icon (or the built-in one) stays.
            logger.LogInformation("Icon of {Game} not refreshed: {Reason}", gameId, ex.Message);
            return false;
        }
    }

    private bool Forget(string gameId)
    {
        DeleteIfExists(ETagPath(gameId));
        if (Cached(gameId) is not { } kept) return false;
        File.Delete(kept);
        logger.LogInformation("Icon of {Game} removed: the feed has none", gameId);
        return true;
    }

    private void Save(string gameId, byte[] bytes)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = Path.Combine(Directory, $".{gameId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, PathOf(gameId), overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    // File.Delete throws when the folder is missing, as it is before the first icon.
    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private EntityTagHeaderValue? ReadETag(string gameId) =>
        File.Exists(ETagPath(gameId)) && EntityTagHeaderValue.TryParse(File.ReadAllText(ETagPath(gameId)).Trim(), out var tag)
            ? tag
            : null;

    private void SaveETag(string gameId, EntityTagHeaderValue? etag)
    {
        if (etag is null) DeleteIfExists(ETagPath(gameId));
        else File.WriteAllText(ETagPath(gameId), etag.ToString());
    }
}
