using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "archive-extractor-" + Guid.NewGuid().ToString("N"));

    public ArchiveExtractorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("pkg.zip", true)]
    [InlineData("pkg.tar.gz", true)]
    [InlineData("pkg.tgz", true)]
    [InlineData("llama-server.exe", false)]
    public void IsSupportedArchive(string filename, bool expected)
        => Assert.Equal(expected, ArchiveExtractor.IsSupportedArchive(filename));

    [Fact]
    public void ExtractInto_Zip_WritesFilesIncludingNested()
    {
        var zipPath = Path.Combine(_dir, "pkg.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "llama-server.exe", "MAIN");
            WriteZipEntry(zip, "sub/lib.dll", "LIB");
        }

        var dest = Path.Combine(_dir, "out");
        ArchiveExtractor.ExtractInto(zipPath, dest);

        Assert.Equal("MAIN", File.ReadAllText(Path.Combine(dest, "llama-server.exe")));
        Assert.Equal("LIB", File.ReadAllText(Path.Combine(dest, "sub", "lib.dll")));
    }

    [Fact]
    public void ExtractInto_TarGz_WritesFiles()
    {
        var tarGzPath = Path.Combine(_dir, "pkg.tar.gz");
        CreateTarGz(tarGzPath, ("llama-server", "ELF"), ("libggml.so", "GGML"));

        var dest = Path.Combine(_dir, "out-tar");
        ArchiveExtractor.ExtractInto(tarGzPath, dest);

        Assert.Equal("ELF", File.ReadAllText(Path.Combine(dest, "llama-server")));
        Assert.Equal("GGML", File.ReadAllText(Path.Combine(dest, "libggml.so")));
    }

    [Fact]
    public void ExtractInto_DoesNotClearDestination_DependenciesMergeOnTop()
    {
        // Simulates the cudart zip merging into the dir that already holds the
        // main binary: the pre-existing file must survive.
        var dest = Path.Combine(_dir, "merge");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "llama-server.exe"), "MAIN");

        var cudartZip = Path.Combine(_dir, "cudart.zip");
        using (var zip = ZipFile.Open(cudartZip, ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "cudart64_12.dll", "CUDART");
        }

        ArchiveExtractor.ExtractInto(cudartZip, dest);

        Assert.Equal("MAIN", File.ReadAllText(Path.Combine(dest, "llama-server.exe")));
        Assert.Equal("CUDART", File.ReadAllText(Path.Combine(dest, "cudart64_12.dll")));
    }

    [Fact]
    public void ExtractInto_RejectsZipSlip()
    {
        var evilZip = Path.Combine(_dir, "evil.zip");
        using (var zip = ZipFile.Open(evilZip, ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "../escaped.txt", "PWN");
        }

        var dest = Path.Combine(_dir, "safe");
        Assert.Throws<InvalidDataException>(() => ArchiveExtractor.ExtractInto(evilZip, dest));
    }

    private static void WriteZipEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void CreateTarGz(string path, params (string Name, string Content)[] entries)
    {
        using var fileStream = File.Create(path);
        using var gzip = new GZipStream(fileStream, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
        foreach (var (name, content) in entries)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name);
            var bytes = Encoding.UTF8.GetBytes(content);
            entry.DataStream = new MemoryStream(bytes);
            tar.WriteEntry(entry);
        }
    }
}
