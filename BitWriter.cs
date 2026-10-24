namespace JpegStego;

/// <summary>
/// 按位写入器 - 逐位写入并自动添加字节填充
/// </summary>
internal sealed class BitWriter
{
    private readonly List<byte> _buffer = new();
    private byte _currentByte;
    private int _bitPos; // 0-7

    public void WriteBit(bool bit)
    {
        if (bit)
            _currentByte |= (byte)(1 << (7 - _bitPos));
        _bitPos++;
        if (_bitPos == 8)
        {
            FlushByte();
        }
    }

    public void WriteBits(int value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
            WriteBit(((value >> i) & 1) != 0);
    }

    private void FlushByte()
    {
        _buffer.Add(_currentByte);
        // 字节填充：如果写入 0xFF，必须跟 0x00
        if (_currentByte == 0xFF)
            _buffer.Add(0x00);
        _currentByte = 0;
        _bitPos = 0;
    }

    /// <summary>
    /// 完成写入，补齐剩余位（填1）
    /// </summary>
    public byte[] ToByteArray()
    {
        if (_bitPos > 0)
        {
            // 剩余位填 1（符合 JPEG 规范）
            _currentByte |= (byte)(0xFF >> _bitPos);
            FlushByte();
        }
        return _buffer.ToArray();
    }
}
