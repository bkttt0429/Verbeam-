using Microsoft.Data.Sqlite;
using Verbeam.Core.Models;
using Verbeam.Core.Storage;

namespace Verbeam.Tests;

public sealed class OcrBlockLayoutStoreTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "verbeam-block-layout-tests-" + Guid.NewGuid());

    private SqliteOcrBlockLayoutStore CreateStore()
    {
        Directory.CreateDirectory(_tempDirectory);
        return new SqliteOcrBlockLayoutStore(Path.Combine(_tempDirectory, "layout.sqlite"));
    }

    [Fact]
    public async Task Upsert_InsertsThenUpdatesSameKey()
    {
        var store = CreateStore();

        await store.UpsertLayoutAsync(new OcrBlockLayout(
            "default", "job1:0", "block-1", DateTimeOffset.UtcNow)
        {
            Nx = 0.1,
            Ny = 0.2,
            Nw = 0.3,
            Nh = 0.05,
            FontSize = 12.5,
            LineHeight = 1.4,
            TextAlign = "center",
            Overflow = OcrBlockOverflowModes.Wrap
        });

        var first = await store.ListByDocKeyAsync("default", "job1:0");
        var row = Assert.Single(first);
        Assert.Equal(0.1, row.Nx);
        Assert.Equal(0.05, row.Nh);
        Assert.Equal(12.5, row.FontSize);
        Assert.Equal("center", row.TextAlign);
        Assert.Equal(OcrBlockOverflowModes.Wrap, row.Overflow);

        // Same (profile, doc_key, block) updates in place, including clearing geometry to null.
        await store.UpsertLayoutAsync(new OcrBlockLayout(
            "default", "job1:0", "block-1", DateTimeOffset.UtcNow)
        {
            Nx = null,
            FontSize = 9,
            Overflow = OcrBlockOverflowModes.Shrink
        });

        var updated = await store.ListByDocKeyAsync("default", "job1:0");
        var only = Assert.Single(updated);
        Assert.Null(only.Nx);
        Assert.Equal(9, only.FontSize);
        Assert.Equal(OcrBlockOverflowModes.Shrink, only.Overflow);
    }

    [Fact]
    public async Task ListByDocKey_IsScopedToDocKey()
    {
        var store = CreateStore();
        await store.UpsertLayoutAsync(new OcrBlockLayout("default", "job1:0", "block-1", DateTimeOffset.UtcNow));
        await store.UpsertLayoutAsync(new OcrBlockLayout("default", "job1:1", "block-1", DateTimeOffset.UtcNow));

        var page0 = await store.ListByDocKeyAsync("default", "job1:0");
        Assert.Single(page0);
        Assert.Equal("job1:0", page0[0].DocKey);
    }

    [Fact]
    public async Task Upsert_NormalizesUnknownOverflowToShrink()
    {
        var store = CreateStore();
        await store.UpsertLayoutAsync(new OcrBlockLayout("default", "job1:0", "block-1", DateTimeOffset.UtcNow)
        {
            Overflow = "bogus"
        });

        var rows = await store.ListByDocKeyAsync("default", "job1:0");
        Assert.Equal(OcrBlockOverflowModes.Shrink, Assert.Single(rows).Overflow);
    }

    [Fact]
    public async Task Upsert_DefaultGeometryIsNull()
    {
        var store = CreateStore();
        await store.UpsertLayoutAsync(new OcrBlockLayout("default", "job1:0", "block-1", DateTimeOffset.UtcNow));

        var row = Assert.Single(await store.ListByDocKeyAsync("default", "job1:0"));
        Assert.Null(row.Nx);
        Assert.Null(row.Ny);
        Assert.Null(row.Nw);
        Assert.Null(row.Nh);
        Assert.Null(row.FontSize);
        Assert.Null(row.LineHeight);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
