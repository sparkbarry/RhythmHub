using RhythmHub.Updater.Models;

namespace RhythmHub.Updater;

/// <summary>Rules for the folder a player picks for a game.</summary>
public static class GameFolder
{
    /// <summary>macOS and Windows volumes are case-insensitive by default.</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// The folder a game picked as <paramref name="chosen"/> should use. A new or empty folder, one named after the game,
    /// this game's RhythmHub folder or a standalone copy of the game is used as is. Any other folder gets a subfolder
    /// named after the game, so picking something like <c>D:\Games</c> does not spill game files over it.
    /// </summary>
    public static string Resolve(string chosen, string gameId)
    {
        var dir = Normalize(chosen);
        if (FileSystemEntry.IsEmptyDirectory(dir) ||
            string.Equals(Path.GetFileName(dir), gameId, StringComparison.OrdinalIgnoreCase) ||
            OwnerOf(dir) == gameId ||
            File.Exists(Path.Combine(dir, gameId + ".exe")) ||
            Directory.Exists(Path.Combine(dir, gameId + ".app")) ||
            Directory.Exists(Path.Combine(dir, gameId + "_Data")))
            return dir;
        return Path.Combine(dir, gameId);
    }

    /// <summary>The game whose installed version lives in <paramref name="directory"/>, or null.</summary>
    public static string? OwnerOf(string directory)
    {
        var layout = new GameInstallLayout(directory, "");
        try
        {
            var state = AtomicFile.ReadJsonAsync<LocalGameState>(layout.CurrentStatePath, CancellationToken.None).GetAwaiter().GetResult();
            if (state?.ActiveVersion is not { } active) return null;
            return AtomicFile.ReadJsonAsync<ReleaseManifest>(layout.ManifestCopyPath(active), CancellationToken.None)
                .GetAwaiter().GetResult()?.GameId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or UpdateException)
        {
            return null;
        }
    }

    /// <summary>True if the folder holds anything of the launcher's: an install, a download in progress.</summary>
    public static bool HasLauncherData(string directory) =>
        Directory.Exists(Path.Combine(directory, ManifestPaths.ReservedDirectory));

    /// <summary>Rejects a folder that is a volume root or nests with this game's current folder or another game's.</summary>
    public static void Validate(string target, string current, IEnumerable<string> otherGames)
    {
        target = Normalize(target);
        if (Path.GetPathRoot(target) is { } root && SamePath(Path.TrimEndingDirectorySeparator(root), target))
            throw new UpdateException(UpdateErrorCode.InvalidDestination, $"'{target}' is the root of a volume.");
        if (IsInside(target, Normalize(current)))
            throw new UpdateException(UpdateErrorCode.InvalidDestination, $"'{target}' is inside the current game folder.");
        foreach (var other in otherGames.Select(Normalize))
        {
            if (SamePath(target, other) || IsInside(target, other) || IsInside(other, target))
                throw new UpdateException(UpdateErrorCode.InvalidDestination, $"'{target}' overlaps another game's folder '{other}'.");
        }
    }

    public static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), PathComparison);

    /// <summary>
    /// Moves a whole game folder: a rename on the same volume, otherwise a copy and then removal of the original.
    /// <paramref name="to"/> must be missing or empty.
    /// </summary>
    /// <returns>False if the original could not be removed completely after a copy.</returns>
    internal static bool Move(string from, string to, CancellationToken ct)
    {
        if (!FileSystemEntry.IsEmptyDirectory(to))
            throw new UpdateException(UpdateErrorCode.DestinationNotEmpty, $"'{to}' is not empty.");
        if (Directory.Exists(to)) FileSystemEntry.DeleteEmptyDirectory(to);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        try
        {
            Directory.Move(from, to);
            return true;
        }
        catch (IOException ex) when (IsCrossVolume(from, to, ex))
        {
        }

        try
        {
            FileSystemEntry.CopyTree(from, to, ct);
        }
        catch
        {
            if (Directory.Exists(to)) Directory.Delete(to, recursive: true);
            throw;
        }
        try
        {
            Directory.Delete(from, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsCrossVolume(string from, string to, IOException ex)
    {
        const int exdev = 18; // errno on both macOS and Linux
        return OperatingSystem.IsWindows()
            ? !string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase)
            : ex.HResult == exdev;
    }

    private static bool IsInside(string path, string parent) =>
        path.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
