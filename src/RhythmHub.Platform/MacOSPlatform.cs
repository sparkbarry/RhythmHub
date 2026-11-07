using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RhythmHub.Updater;

namespace RhythmHub.Platform;

[SupportedOSPlatform("macos")]
public sealed class MacOSPlatform : IGamePlatform
{
    public string PlatformId => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "MacOS",
        var other => throw new PlatformNotSupportedException($"macOS on {other} is not supported."),
    };

    public string DefaultInstallRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", GamePlatform.AppFolderName);

    public async Task LaunchGameAsync(string gameDirectory, string entrypoint, IReadOnlyDictionary<string, string> environment,
        CancellationToken ct)
    {
        // Let LaunchServices start the bundle instead of exec'ing the binary inside it.
        var app = Path.Combine(gameDirectory, entrypoint);
        string[] args = ["-n", .. environment.SelectMany(kv => new[] { "--env", $"{kv.Key}={kv.Value}" }), app];
        var (code, _, err) = await RunAsync("/usr/bin/open", args, ct);
        if (code != 0)
            throw new InvalidOperationException($"open failed ({code}): {err.Trim()}");
    }

    public async Task<bool> IsGameRunningAsync(string gameDirectory, string entrypoint, CancellationToken ct)
    {
        // Any process whose executable path (argv[0]; LaunchServices passes it absolute) is inside the game folder.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory)) + "/";
        var (code, _, _) = await RunAsync("/usr/bin/pgrep", ["-f", "^" + EscapeRegex(root)], ct);
        return code == 0;
    }

    /// <summary>pgrep takes an extended regular expression; a picked folder may contain any of these.</summary>
    private static string EscapeRegex(string text) =>
        string.Concat(text.Select(c => @"\.^$|?*+()[]{}".Contains(c) ? "\\" + c : c.ToString()));

    public Task VerifyPlatformPackageAsync(string unpackedDirectory, string entrypoint, CancellationToken ct)
    {
        if (!entrypoint.EndsWith(".app", StringComparison.Ordinal))
            throw new UpdateException(UpdateErrorCode.InvalidPackage, $"macOS entrypoint must be an .app bundle, got '{entrypoint}'.");
        var app = Path.Combine(unpackedDirectory, entrypoint);
        if (!File.Exists(Path.Combine(app, "Contents", "Info.plist")))
            throw new UpdateException(UpdateErrorCode.InvalidPackage, $"'{entrypoint}' has no Contents/Info.plist.");
        var macOs = Path.Combine(app, "Contents", "MacOS");
        var hasExecutable = Directory.Exists(macOs) && Directory.EnumerateFiles(macOs).Any(f =>
            (File.GetUnixFileMode(f) & UnixFileMode.UserExecute) != 0);
        if (!hasExecutable)
            throw new UpdateException(UpdateErrorCode.InvalidPackage, $"'{entrypoint}' has no executable in Contents/MacOS.");
        // TODO(phase C): `codesign --verify --deep --strict` once release builds are signed and notarized.
        return Task.CompletedTask;
    }

    public void RevealDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start("/usr/bin/open", [path])?.Dispose();
    }

    private static async Task<(int Code, string Out, string Err)> RunAsync(string file, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await stdout, await stderr);
    }
}
