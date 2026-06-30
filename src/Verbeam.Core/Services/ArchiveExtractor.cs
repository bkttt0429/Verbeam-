using System.Formats.Tar;
using System.IO.Compression;

namespace Verbeam.Core.Services;

/// <summary>
/// Safe extraction of llama.cpp release archives (.zip on Windows, .tar.gz on
/// Linux/macOS). Always rejects entries that would escape the destination
/// directory (zip-slip), and never clears the destination so dependency archives
/// (e.g. the Windows cudart zip) merge on top of the main binary.
/// </summary>
public static class ArchiveExtractor
{
    public static bool IsSupportedArchive(string filename)
        => filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
           filename.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
           filename.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    public static void ExtractInto(string archivePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            ExtractTarGz(archivePath, destinationDirectory);
        }
        else
        {
            ExtractZip(archivePath, destinationDirectory);
        }
    }

    private static void ExtractZip(string archivePath, string destinationDirectory)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue; // directory entry
            }

            var destinationPath = ResolveSafePath(destinationDirectory, entry.FullName);
            EnsureParentDirectory(destinationPath);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static void ExtractTarGz(string archivePath, string destinationDirectory)
    {
        using var fileStream = File.OpenRead(archivePath);
        using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }

            var destinationPath = ResolveSafePath(destinationDirectory, entry.Name);
            EnsureParentDirectory(destinationPath);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static string ResolveSafePath(string destinationDirectory, string entryName)
    {
        var destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, entryName));
        var root = Path.GetFullPath(destinationDirectory);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!destinationPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(destinationPath, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Archive contains an unsafe path: {entryName}");
        }

        return destinationPath;
    }

    private static void EnsureParentDirectory(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
