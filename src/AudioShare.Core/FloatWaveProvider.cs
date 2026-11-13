using NAudio.Wave;

namespace AudioShare.Core;

/// <summary>
/// 把 ISampleProvider 包装成 IWaveProvider，并保留原始的（可能是 WAVEFORMATEXTENSIBLE 的）格式。
/// NAudio 自带的 SampleToWaveProvider 只接受 Encoding == IeeeFloat，
/// 而大多数 Windows 设备的混音格式是 Extensible，所以需要自己包一层。
/// </summary>
public sealed class FloatWaveProvider : IWaveProvider
{
    private readonly ISampleProvider _source;
    private float[] _temp = Array.Empty<float>();

    public FloatWaveProvider(ISampleProvider source)
    {
        if (!SampleConverter.IsFloat32(source.WaveFormat))
            throw new ArgumentException("源必须是 32 位浮点格式");
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        int samples = count / 4;
        if (_temp.Length < samples) _temp = new float[samples];
        int read = _source.Read(_temp, 0, samples);
        Buffer.BlockCopy(_temp, 0, buffer, offset, read * 4);
        return read * 4;
    }
}
