namespace RhythmHub.Updater;

internal enum EntryKind
{
    None,
    File,
    Directory,
    Link,
}

/// <summary>File system helpers that look at links themselves and never follow them.</summary>
internal static class FileSystemEntry
{
    /// <summary>Finder and Explorer drop these into folders on their own; they do not make a folder "used".</summary>
    private static readonly HashSet<string> SystemClutter = new(StringComparer.OrdinalIgnoreCase) { ".DS_Store", "desktop.ini", "Thumbs.db" };

    public static EntryKind Kind(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null) return EntryKind.Link;
        if (info.Exists) return EntryKind.File;
        return Directory.Exists(path) ? EntryKind.Directory : EntryKind.None;
    }

    /// <summary>Removes whatever is at <paramref name="path"/>; a link is removed, never what it points at.</summary>
    public static void Delete(string path)
    {
        switch (Kind(path))
        {
            case EntryKind.Link when OperatingSystem.IsWindows() && Directory.Exists(path):
                Directory.Delete(path);
                break;
            case EntryKind.Link:
                File.Delete(path);
                break;
            case EntryKind.File:
                DeleteFile(path);
                break;
            case EntryKind.Directory:
                Directory.Delete(path, recursive: true);
                break;
        }
    }

    public static void DeleteFile(string path)
    {
        // Windows refuses to delete read-only files.
        if (OperatingSystem.IsWindows() && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    /// <summary>True if a directory between <paramref name="root"/> (exclusive) and <paramref name="path"/> is a link.</summary>
    public static bool HasLinkedParent(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        for (var dir = Path.GetDirectoryName(path); dir is not null && dir.Length > root.Length; dir = Path.GetDirectoryName(dir))
        {
            if (Kind(dir) == EntryKind.Link) return true;
        }
        return false;
    }

    /// <summary>Missing, or holding nothing but system clutter.</summary>
    public static bool IsEmptyDirectory(string path) =>
        !Directory.Exists(path) ||
        Directory.EnumerateFileSystemEntries(path).All(e => SystemClutter.Contains(Path.GetFileName(e)));

    /// <summary>Removes an empty directory, clutter included.</summary>
    public static void DeleteEmptyDirectory(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path).ToList())
            DeleteFile(entry);
        Directory.Delete(path);
    }

    /// <summary>Copies a tree, recreating links as links and keeping modes and modification times.</summary>
    public static void CopyTree(string from, string to, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in Directory.EnumerateFileSystemEntries(from))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(to, Path.GetFileName(entry));
            switch (Kind(entry))
            {
                case EntryKind.Link:
                    var linkTarget = new FileInfo(entry).LinkTarget!;
                    if (Directory.Exists(entry)) Directory.CreateSymbolicLink(target, linkTarget);
                    else File.CreateSymbolicLink(target, linkTarget);
                    break;
                case EntryKind.Directory:
                    CopyTree(entry, target, ct);
                    break;
                case EntryKind.File:
                    CopyFile(entry, target);
                    break;
            }
        }
    }

    /// <summary>
    /// Copies one file (an APFS clone on macOS) and keeps its modification time, which lets a later deployment tell
    /// an untouched copy from one the player replaced.
    /// </summary>
    public static void CopyFile(string from, string to)
    {
        File.Copy(from, to);
        File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(to, File.GetUnixFileMode(from));
    }
}
