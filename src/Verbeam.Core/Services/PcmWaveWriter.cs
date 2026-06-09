using System.Buffers.Binary;

namespace Verbeam.Core.Services;

public static class PcmWaveWriter
{
    public static byte[] BuildPcm16MonoWav(byte[] pcm16, int sampleRate)
        => BuildPcmWave(pcm16, sampleRate, channels: 1, bitsPerSample: 16);

    public static byte[] BuildPcmWave(byte[] pcm, int sampleRate, short channels, short bitsPerSample)
    {
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var output = new byte[44 + pcm.Length];

        WriteAscii(output, 0, "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), 36 + pcm.Length);
        WriteAscii(output, 8, "WAVE");
        WriteAscii(output, 12, "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(22), channels);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(28), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(34), bitsPerSample);
        WriteAscii(output, 36, "data");
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(40), pcm.Length);
        Buffer.BlockCopy(pcm, 0, output, 44, pcm.Length);

        return output;
    }

    private static void WriteAscii(byte[] buffer, int offset, string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            buffer[offset + index] = (byte)value[index];
        }
    }
}
