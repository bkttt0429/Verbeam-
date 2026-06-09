using System.Security.Cryptography;
using System.Text;

namespace YomiBridge.Core.Services;

public static class TranslationCacheKey
{
    public static string Create(
        string text,
        string source,
        string target,
        string mode,
        string provider,
        string model,
        string presetVersion,
        string glossaryHash,
        string contextHash = "")
    {
        var parts = new List<string>
        {
            text,
            source,
            target,
            mode,
            provider,
            model,
            presetVersion,
            glossaryHash
        };

        if (!string.IsNullOrWhiteSpace(contextHash))
        {
            parts.Add(contextHash);
        }

        var raw = string.Join("\u001f", parts);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }
}
