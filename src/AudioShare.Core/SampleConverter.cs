using NAudio.Wave;

namespace AudioShare.Core;

public static class SampleConverter
{
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    public static bool IsFloat32(WaveFormat format)
    {
        if (format.BitsPerSample != 32) return false;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat) return true;
        return format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat;
    }

    /// <summary>把采集到的字节流转换为交错的 float 样本，返回样本数。</summary>
    public static int ToFloat(WaveFormat format, byte[] source, int byteCount, ref float[] destination)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        int samples = byteCount / bytesPerSample;
        if (destination.Length < samples) destination = new float[samples * 2];

        var dst = destination.AsSpan(0, samples);
        var src = source.AsSpan(0, samples * bytesPerSample);

        if (IsFloat32(format))
        {
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(src).CopyTo(dst);
            return samples;
        }

        switch (format.BitsPerSample)
        {
            case 16:
                for (int i = 0; i < samples; i++)
                    dst[i] = BitConverter.ToInt16(src.Slice(i * 2, 2)) / 32768f;
                break;
            case 24:
                for (int i = 0; i < samples; i++)
                {
                    int o = i * 3;
                    int v = (src[o] << 8) | (src[o + 1] << 16) | (src[o + 2] << 24);
                    dst[i] = v / 2147483648f;
                }
                break;
            case 32:
                for (int i = 0; i < samples; i++)
                    dst[i] = BitConverter.ToInt32(src.Slice(i * 4, 4)) / 2147483648f;
                break;
            default:
                throw new NotSupportedException($"不支持的采样位深：{format.BitsPerSample}");
        }
        return samples;
    }
}
