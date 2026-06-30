using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Verbeam.Core.Services;

public interface IEmbeddingProvider
{
    string Model { get; }
    int Dimensions { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

public sealed class HashEmbeddingProvider : IEmbeddingProvider
{
    public HashEmbeddingProvider(int dimensions = 64)
    {
        Dimensions = Math.Clamp(dimensions, 16, 512);
        Model = $"verbeam-hash-v1-{Dimensions}";
    }

    public string Model { get; }
    public int Dimensions { get; }

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var vector = new float[Dimensions];
        foreach (var token in Tokenize(text))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            var index = (int)(BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(0, 4)) % (uint)Dimensions);
            var sign = (hash[4] & 1) == 0 ? 1f : -1f;
            vector[index] += sign;
        }

        Normalize(vector);
        return Task.FromResult(vector);
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var token = new StringBuilder();
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                token.Append(ch);
                continue;
            }

            if (token.Length > 0)
            {
                yield return token.ToString();
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }

    private static void Normalize(float[] vector)
    {
        var sum = 0d;
        foreach (var value in vector)
        {
            sum += value * value;
        }

        if (sum <= 0)
        {
            return;
        }

        var magnitude = Math.Sqrt(sum);
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] = (float)(vector[index] / magnitude);
        }
    }
}
