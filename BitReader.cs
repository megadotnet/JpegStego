namespace JpegStego;

/// <summary>
/// 按位读取器 - 从字节流中逐位读取
/// </summary>
internal sealed class BitReader
{
    private readonly byte[] _data;
    private int _bytePos;
    private int _bitPos; // 0-7, MSB first

    public BitReader(byte[] data, int offset = 0)
    {
        _data = data;
        _bytePos = offset;
        _bitPos = 0;
    }

    public bool ReadBit()
    {
        if (_bytePos >= _data.Length)
            throw new EndOfStreamException();

        bool bit = (_data[_bytePos] & (1 << (7 - _bitPos))) != 0;
        _bitPos++;
        if (_bitPos == 8)
        {
            _bitPos = 0;
            _bytePos++;
        }
        return bit;
    }

    public int ReadBits(int count)
    {
        int result = 0;
        for (int i = 0; i < count; i++)
            result = (result << 1) | (ReadBit() ? 1 : 0);
        return result;
    }

    public int BytePosition => _bytePos;
    public int BitPosition => _bitPos;

    /// <summary>
    /// 跳过到下一个字节边界
    /// </summary>
    public void AlignToByte()
    {
        if (_bitPos != 0)
        {
            _bitPos = 0;
            _bytePos++;
        }
    }
}
