using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

public static class MemoryEmbeddingText
{
    public static string CreateText(MemoryItem item)
        => string.Join(
            "\n",
            item.MemoryKind,
            item.SourceLanguage,
            item.TargetLanguage,
            item.SourceText,
            item.TargetText,
            item.Note);

    public static string CreateContentHash(MemoryItem item)
        => RagSecurityPolicy.ComputeSourceHash(
            item.MemoryKind,
            item.SourceLanguage,
            item.TargetLanguage,
            item.SourceHash,
            item.SourceText,
            item.TargetText,
            item.Note);
}
