namespace AudioShare.Core;

/// <summary>
/// 在不同声道数之间转换（例如 5.1 源 → 立体声耳机，立体声源 → 单声道蓝牙免提）。
/// 声道顺序按 Windows 标准：FL, FR, FC, LFE, BL, BR, SL, SR。
/// </summary>
public static class ChannelMapper
{
    private const float Minus3dB = 0.7071f;

    public static void Map(ReadOnlySpan<float> src, int srcChannels, Span<float> dst, int dstChannels, int frames)
    {
        if (srcChannels == dstChannels)
        {
            src[..(frames * srcChannels)].CopyTo(dst);
            return;
        }

        for (int f = 0; f < frames; f++)
        {
            var s = src.Slice(f * srcChannels, srcChannels);
            var d = dst.Slice(f * dstChannels, dstChannels);

            if (dstChannels == 1)
            {
                float sum = 0;
                for (int c = 0; c < srcChannels; c++) sum += s[c];
                d[0] = sum / srcChannels;
            }
            else if (srcChannels == 1)
            {
                d.Clear();
                d[0] = s[0];
                d[1] = s[0];
            }
            else if (dstChannels == 2)
            {
                // 多声道下混到立体声
                float l = s[0], r = s[1];
                if (srcChannels >= 3) { l += s[2] * Minus3dB; r += s[2] * Minus3dB; }
                if (srcChannels >= 6) { l += s[4] * Minus3dB; r += s[5] * Minus3dB; }
                if (srcChannels >= 8) { l += s[6] * Minus3dB; r += s[7] * Minus3dB; }
                float norm = srcChannels >= 3 ? 0.5f : 1f;
                d[0] = l * norm;
                d[1] = r * norm;
            }
            else
            {
                // 其他情况：按顺序复制共有声道，多出的声道静音
                d.Clear();
                int n = Math.Min(srcChannels, dstChannels);
                for (int c = 0; c < n; c++) d[c] = s[c];
            }
        }
    }
}
