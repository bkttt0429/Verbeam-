using Microsoft.Data.Sqlite;
using Verbeam.Core.Models;
using Verbeam.Core.Storage;

namespace Verbeam.Tests;

public sealed class OcrBlockAnnotationStoreTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "verbeam-block-annotation-tests-" + Guid.NewGuid());

    private SqliteOcrBlockAnnotationStore CreateStore()
    {
        Directory.CreateDirectory(_tempDirectory);
        return new SqliteOcrBlockAnnotationStore(Path.Combine(_tempDirectory, "annotations.sqlite"));
    }

    [Fact]
    public async Task Upsert_InsertsThenUpdatesSameKey()
    {
        var store = CreateStore();

        await store.UpsertAsync(new OcrBlockAnnotation(
            "default", "hash-a", "block-1", OcrBlockStatuses.Locked, true, "我的翻譯", DateTimeOffset.UtcNow)
        {
            EditedSource = "原文",
            Note = "manual"
        });

        var first = await store.ListByImageAsync("default", "hash-a");
        Assert.Single(first);
        Assert.True(first[0].Locked);
        Assert.Equal(OcrBlockStatuses.Locked, first[0].Status);
        Assert.Equal("我的翻譯", first[0].EditedTranslation);
        Assert.Equal("原文", first[0].EditedSource);

        // same (profile, image, block) key updates in place
        await store.UpsertAsync(new OcrBlockAnnotation(
            "default", "hash-a", "block-1", OcrBlockStatuses.Edited, false, "更新後", DateTimeOffset.UtcNow));

        var updated = await store.ListByImageAsync("default", "hash-a");
        Assert.Single(updated);
        Assert.False(updated[0].Locked);
        Assert.Equal(OcrBlockStatuses.Edited, updated[0].Status);
        Assert.Equal("更新後", updated[0].EditedTranslation);
    }

    [Fact]
    public async Task ListByImage_IsScopedToImageHash()
    {
        var store = CreateStore();
        await store.UpsertAsync(new OcrBlockAnnotation("default", "hash-a", "block-1", OcrBlockStatuses.Edited, false, "a", DateTimeOffset.UtcNow));
        await store.UpsertAsync(new OcrBlockAnnotation("default", "hash-b", "block-1", OcrBlockStatuses.Ignored, false, "b", DateTimeOffset.UtcNow));

        var imageA = await store.ListByImageAsync("default", "hash-a");
        Assert.Single(imageA);
        Assert.Equal("hash-a", imageA[0].ImageHash);
    }

    [Fact]
    public async Task History_AppendsAndReturnsMostRecentFirst()
    {
        var store = CreateStore();

        await store.AppendHistoryAsync(new OcrBlockHistoryEntry(
            "h1", "default", "hash-a", "block-1", OcrBlockHistoryKinds.Translation,
            "src", "old translation", "mock", "mock", DateTimeOffset.UtcNow.AddSeconds(-5)));
        await store.AppendHistoryAsync(new OcrBlockHistoryEntry(
            "h2", "default", "hash-a", "block-1", OcrBlockHistoryKinds.Ocr,
            "old source", "", "windows", "windows", DateTimeOffset.UtcNow));

        var history = await store.ListHistoryAsync("default", "hash-a", "block-1");
        Assert.Equal(2, history.Count);
        Assert.Equal("h2", history[0].Id);
        Assert.Equal(OcrBlockHistoryKinds.Ocr, history[0].Kind);
        Assert.Equal("h1", history[1].Id);
    }

    [Fact]
    public async Task Upsert_NormalizesUnknownStatusToTranslated()
    {
        var store = CreateStore();
        await store.UpsertAsync(new OcrBlockAnnotation(
            "default", "hash-a", "block-1", "bogus-status", false, "", DateTimeOffset.UtcNow));

        var rows = await store.ListByImageAsync("default", "hash-a");
        Assert.Single(rows);
        Assert.Equal(OcrBlockStatuses.Translated, rows[0].Status);
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
