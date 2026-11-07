using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RhythmHub.Updater.Models;
using RhythmHub.Updater.Security;

namespace RhythmHub.Updater.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("rhythmhub-tests-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal static class TestKeys
{
    public const string KeyId = "test-key";
    public static readonly (string Private, string Public) Pair = ManifestSigning.GenerateKeyPair();
    public static IReadOnlyDictionary<string, string> Trusted => new Dictionary<string, string> { [KeyId] = Pair.Public };
}

/// <summary>Build content for one release: files (with Unix modes), symlinks and empty directories.</summary>
internal sealed class Build(string entrypoint = "Game.app")
{
    public string Entrypoint { get; } = entrypoint;
    public Dictionary<string, (string Content, int Mode)> Files { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Links { get; } = new(StringComparer.Ordinal);
    public List<string> EmptyDirs { get; } = [];

    public Build File(string path, string content, int mode = 0b110_100_100)
    {
        Files[path] = (content, mode);
        return this;
    }

    public Build Link(string path, string target)
    {
        Links[path] = target;
        return this;
    }

    public Build Dir(string path)
    {
        EmptyDirs.Add(path);
        return this;
    }

    public Build Clone()
    {
        var b = new Build(Entrypoint);
        foreach (var (k, v) in Files) b.Files[k] = v;
        foreach (var (k, v) in Links) b.Links[k] = v;
        b.EmptyDirs.AddRange(EmptyDirs);
        return b;
    }

    public void WriteTo(string root)
    {
        foreach (var (path, (content, mode)) in Files)
        {
            var full = System.IO.Path.Combine(root, path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllText(full, content);
            if (!OperatingSystem.IsWindows()) System.IO.File.SetUnixFileMode(full, (UnixFileMode)mode);
        }
        foreach (var dir in EmptyDirs) Directory.CreateDirectory(System.IO.Path.Combine(root, dir));
        foreach (var (path, target) in Links)
        {
            var full = System.IO.Path.Combine(root, path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            System.IO.File.CreateSymbolicLink(full, target);
        }
    }
}

/// <summary>In-memory S3: flat objects by URL, Range support, signed pointer + manifest per release.</summary>
internal sealed class FakeFeed : HttpMessageHandler, IDisposable
{
    public static readonly Uri Base = new("https://cdn.test/");
    private readonly Dictionary<string, byte[]> _objects = new();
    private readonly TempDir _work = new();

    public List<(Uri Uri, long? RangeFrom)> Log { get; } = [];

    /// <summary>One-shot fault: the next response for a path ending in <see cref="DropPathSuffix"/> breaks after this many bytes.</summary>
    public long? DropConnectionAfter { get; set; }

    public string DropPathSuffix { get; set; } = "big.bin";

    public IEnumerable<(Uri Uri, long? RangeFrom)> FileRequests =>
        Log.Where(r => !r.Uri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal));

    public void Put(Uri uri, byte[] content) => _objects[uri.AbsoluteUri] = content;

    public void Remove(Uri uri) => _objects.Remove(uri.AbsoluteUri);

    public Uri FileUri(string game, string platform, string version, string path) =>
        FeedLayout.FileUri(Base, game, platform, version, path);

    public ReleaseManifest Publish(string game, string platform, string version, Build build,
        string? minLauncher = null, bool sign = true, string channel = "stable")
    {
        var dir = System.IO.Path.Combine(_work.Path, game, platform, version);
        build.WriteTo(dir);
        var manifest = ReleaseManifestBuilder.FromDirectoryAsync(dir, game, platform, version, build.Entrypoint, default)
            .GetAwaiter().GetResult() with { MinimumLauncherVersion = minLauncher };
        foreach (var f in manifest.Files)
            Put(FileUri(game, platform, version, f.Path), System.IO.File.ReadAllBytes(System.IO.Path.Combine(dir, f.Path)));

        var manifestBytes = SignedBytes(manifest, sign);
        Put(FeedLayout.ManifestUri(Base, game, platform, version), manifestBytes);
        Point(game, platform, version, manifestBytes, sign, channel);
        return manifest;
    }

    public void Point(string game, string platform, string version, byte[] manifestBytes, bool sign = true,
        string channel = "stable")
    {
        var pointer = new ChannelPointer
        {
            SchemaVersion = 2,
            GameId = game,
            Platform = platform,
            Channel = channel,
            Version = version,
            ManifestSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(manifestBytes)),
        };
        Put(FeedLayout.ChannelUri(Base, game, platform, channel), SignedBytes(pointer, sign));
    }

    public static byte[] SignedBytes<T>(T value, bool sign = true)
    {
        var node = JsonSerializer.SerializeToNode(value, Json)!.AsObject();
        if (sign) ManifestSigning.Sign(node, TestKeys.Pair.Private, TestKeys.KeyId);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var range = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
        lock (Log) Log.Add((request.RequestUri!, range));
        if (!_objects.TryGetValue(request.RequestUri!.AbsoluteUri, out var body))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); // what S3 says for missing keys

        var offset = range ?? 0;
        if (offset > body.Length)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));

        Stream stream = new MemoryStream(body, (int)offset, body.Length - (int)offset);
        if (DropConnectionAfter is { } cut && request.RequestUri.AbsolutePath.EndsWith(DropPathSuffix, StringComparison.Ordinal))
        {
            DropConnectionAfter = null;
            stream = new BrokenStream(stream, cut);
        }
        var content = new StreamContent(stream);
        content.Headers.ContentLength = body.Length - offset;
        var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = content };
        if (range is not null)
            content.Headers.ContentRange = new ContentRangeHeaderValue(offset, body.Length - 1, body.Length);
        return Task.FromResult(response);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _work.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Yields <paramref name="limit"/> bytes, then fails like a dropped TCP connection.</summary>
    private sealed class BrokenStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= limit) throw new IOException("Connection reset by peer");
            var n = inner.Read(buffer, offset, (int)Math.Min(count, limit - _read));
            _read += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed class FakePlatform : IPackageVerifier
{
    public bool Running { get; set; }
    public bool RejectPackage { get; set; }

    public Task VerifyPlatformPackageAsync(string unpackedDirectory, string entrypoint, CancellationToken ct) =>
        RejectPackage ? throw new UpdateException(UpdateErrorCode.InvalidPackage, "rejected by test") : Task.CompletedTask;

    public Task<bool> IsGameRunningAsync(string gameDirectory, string entrypoint, CancellationToken ct) => Task.FromResult(Running);
}

internal static class Factory
{
    public const string Game = "demo";
    public const string Platform = "MacOS";

    public static UpdaterOptions Options(string root, string launcher = "1.0.0") => new()
    {
        InstallRoot = root,
        PlatformId = Platform,
        FeedBaseUri = FakeFeed.Base,
        LauncherVersion = Version.Parse(launcher),
        DiskSafetyMarginBytes = 0,
        TrustedKeys = TestKeys.Trusted,
    };

    public static GameUpdateService Service(string root, FakeFeed feed, FakePlatform? platform = null, int maxAttempts = 4,
        string? gameDirectory = null) =>
        new(Game, Options(root), new HttpClient(feed), platform ?? new FakePlatform(), NullLoggerFactory.Instance,
            new DownloadService(new HttpClient(feed), NullLogger<DownloadService>.Instance)
            {
                MaxAttempts = maxAttempts,
                InitialBackoff = TimeSpan.Zero,
            }, gameDirectory);

    public static Build GameBuild(string label) => new Build()
        .File("Game.app/Contents/Info.plist", "<plist/>")
        .File("Game.app/Contents/MacOS/Game", "#!/bin/sh\n# " + label, mode: 0b111_101_101)
        .File("Game.app/Contents/Resources/data.bin", "data-" + label);

    /// <summary>Adds a file big enough that a mid-body cut leaves a meaningful partial.</summary>
    public static Build WithBigFile(this Build b, string seed) =>
        b.File("Game.app/Contents/Resources/big.bin", seed + new string('x', 200_000));
}
