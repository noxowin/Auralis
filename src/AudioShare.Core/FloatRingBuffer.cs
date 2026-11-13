namespace AudioShare.Core;

/// <summary>
/// 线程安全的浮点环形缓冲区：采集线程写入，播放线程读取。
/// 写满时丢弃最旧的数据，保证延迟不会无限增长。
/// </summary>
public sealed class FloatRingBuffer
{
    private readonly float[] _buffer;
    private readonly object _lock = new();
    private int _read;
    private int _write;
    private int _count;

    public FloatRingBuffer(int capacity)
    {
        _buffer = new float[capacity];
    }

    public int Capacity => _buffer.Length;

    /// <summary>最近一次写入的时间（毫秒，由写入方设置）。用来区分"真的断流"和"系统本来就没声音"。</summary>
    public long LastWriteMs { get; set; } = long.MinValue / 2;

    public int Count
    {
        get { lock (_lock) return _count; }
    }

    public void Write(ReadOnlySpan<float> data)
    {
        lock (_lock)
        {
            if (data.Length >= _buffer.Length)
            {
                data = data[^_buffer.Length..];
                data.CopyTo(_buffer);
                _read = 0;
                _write = 0;
                _count = _buffer.Length;
                return;
            }

            int overflow = _count + data.Length - _buffer.Length;
            if (overflow > 0)
            {
                _read = (_read + overflow) % _buffer.Length;
                _count -= overflow;
            }

            int first = Math.Min(data.Length, _buffer.Length - _write);
            data[..first].CopyTo(_buffer.AsSpan(_write));
            data[first..].CopyTo(_buffer);
            _write = (_write + data.Length) % _buffer.Length;
            _count += data.Length;
        }
    }

    public int Read(Span<float> destination)
    {
        lock (_lock)
        {
            int n = Math.Min(destination.Length, _count);
            int first = Math.Min(n, _buffer.Length - _read);
            _buffer.AsSpan(_read, first).CopyTo(destination);
            _buffer.AsSpan(0, n - first).CopyTo(destination[first..]);
            _read = (_read + n) % _buffer.Length;
            _count -= n;
            return n;
        }
    }

    public void Discard(int samples)
    {
        lock (_lock)
        {
            int n = Math.Min(samples, _count);
            _read = (_read + n) % _buffer.Length;
            _count -= n;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _read = _write = _count = 0;
        }
    }
}
