using System.Buffers.Binary;
using System.Text;

namespace JpegStego;

/// <summary>
/// JPEG DCT 域信息隐写核心 - 使用 LSB 替换嵌入/提取数据
/// 嵌入位置：AC 系数的最低有效位（跳过零系数、DC 系数、以及 coeff = -2）
/// </summary>
internal sealed class Steganographer
{
    private const int MAGIC_HEADER_BITS = 32; // 用于存储数据长度

    /// <summary>
    /// 将文本嵌入 JPEG 图片
    /// </summary>
    public static void Embed(string inputJpeg, string outputJpeg, string secretText)
    {
        var fileData = File.ReadAllBytes(inputJpeg);
        var structure = JpegParser.Parse(fileData);

        var decoder = new DctDecoder(structure);
        var coefficients = decoder.DecodeCoefficients();

        // 准备秘密数据：4 字节长度 + UTF-8 编码的文本
        byte[] textBytes = Encoding.UTF8.GetBytes(secretText);
        byte[] payload = new byte[4 + textBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), textBytes.Length);
        textBytes.CopyTo(payload, 4);

        // 计算容量
        int availableBits = CountEmbeddableBits(coefficients);
        int requiredBits = payload.Length * 8;
        if (requiredBits > availableBits)
            throw new InvalidOperationException(
                $"图片容量不足。需要 {requiredBits} 位，但只有 {availableBits} 位可用。" +
                $"最多可嵌入 {availableBits / 8} 字节。");

        // 嵌入数据
        EmbedBits(coefficients, payload);

        // 重新编码
        var encoder = new DctEncoder(structure);
        byte[] newScanData = encoder.EncodeCoefficients(coefficients);

        // 重建 JPEG 文件
        RebuildJpeg(fileData, structure, newScanData, outputJpeg);
    }

    /// <summary>
    /// 从 JPEG 图片中提取隐藏文本
    /// </summary>
    public static string Extract(string inputJpeg)
    {
        var fileData = File.ReadAllBytes(inputJpeg);
        var structure = JpegParser.Parse(fileData);
        var decoder = new DctDecoder(structure);
        var coefficients = decoder.DecodeCoefficients();

        // 先读取 32 位长度
        int bitOffset = 0;
        int length = 0;
        for (int i = 0; i < 32; i++)
        {
            bool bit = ReadNextBit(coefficients, ref bitOffset);
            length = (length << 1) | (bit ? 1 : 0);
        }
        // 注意：按位读取（MSB优先）自然得到大端序整数值，无需字节交换

        if (length < 0 || length > 10_000_000) // 10MB sanity check
            throw new InvalidDataException("未找到有效的隐藏数据（长度异常）");

        // 读取数据
        byte[] data = new byte[length];
        for (int byteIdx = 0; byteIdx < length; byteIdx++)
        {
            int b = 0;
            for (int bitIdx = 0; bitIdx < 8; bitIdx++)
            {
                bool bit = ReadNextBit(coefficients, ref bitOffset);
                b = (b << 1) | (bit ? 1 : 0);
            }
            data[byteIdx] = (byte)b;
        }

        return Encoding.UTF8.GetString(data);
    }

    /// <summary>
    /// 获取图片的最大嵌入容量（字节）
    /// </summary>
    public static int GetCapacity(string inputJpeg)
    {
        var fileData = File.ReadAllBytes(inputJpeg);
        var structure = JpegParser.Parse(fileData);
        var decoder = new DctDecoder(structure);
        var coefficients = decoder.DecodeCoefficients();

        // 总位数 - 32 位长度头
        int totalBits = CountEmbeddableBits(coefficients);
        int usableBits = totalBits - MAGIC_HEADER_BITS;
        return Math.Max(0, usableBits / 8);
    }

    /// <summary>
    /// 判断系数是否可嵌入：|coeff| >= 2
    /// 对绝对值最低位进行修改，确保系数编码尺寸不会跨级变化
    /// </summary>
    private static bool IsEmbeddable(short coeff)
    {
        return Math.Abs(coeff) >= 2;
    }

    /// <summary>
    /// 计算可嵌入位数
    /// </summary>
    private static int CountEmbeddableBits(Dictionary<byte, List<short[]>> coefficients)
    {
        int count = 0;
        foreach (var kvp in coefficients)
        {
            foreach (var block in kvp.Value)
            {
                for (int i = 1; i < 64; i++)
                {
                    if (IsEmbeddable(block[i]))
                        count++;
                }
            }
        }
        return count;
    }

    private static void EmbedBits(Dictionary<byte, List<short[]>> coefficients, byte[] data)
    {
        int bitOffset = 0;
        int totalBits = data.Length * 8;

        foreach (var kvp in coefficients)
        {
            foreach (var block in kvp.Value)
            {
                for (int i = 1; i < 64 && bitOffset < totalBits; i++)
                {
                    if (!IsEmbeddable(block[i])) continue;

                    int byteIdx = bitOffset / 8;
                    int bitIdx = 7 - (bitOffset % 8);
                    bool bit = ((data[byteIdx] >> bitIdx) & 1) != 0;

                    // LSB 替换：修改绝对值的最低位
                    short coeff = block[i];
                    int abs = Math.Abs(coeff);
                    int targetLsb = bit ? 1 : 0;
                    if ((abs & 1) != targetLsb)
                    {
                        if ((abs & 1) == 1) abs--; else abs++;
                    }
                    block[i] = (short)(coeff < 0 ? -abs : abs);
                    bitOffset++;
                }
            }
        }
    }

    private static bool ReadNextBit(Dictionary<byte, List<short[]>> coefficients, ref int bitOffset)
    {
        int currentOffset = 0;
        foreach (var kvp in coefficients)
        {
            foreach (var block in kvp.Value)
            {
                for (int i = 1; i < 64; i++)
                {
                    if (!IsEmbeddable(block[i])) continue;
                    if (currentOffset == bitOffset)
                    {
                        bitOffset++;
                        return (Math.Abs(block[i]) & 1) != 0;
                    }
                    currentOffset++;
                }
            }
        }
        throw new EndOfStreamException("无法读取更多位");
    }

    private static void RebuildJpeg(byte[] original, JpegStructure structure, byte[] newScanData, string outputPath)
    {
        using var stream = new FileStream(outputPath, FileMode.Create);
        using var writer = new BinaryWriter(stream);

        // SOI
        writer.Write((byte)0xFF);
        writer.Write(JpegMarker.SOI);

        // 写入所有段直到 SOS
        foreach (var seg in structure.Segments)
        {
            if (seg.Marker == JpegMarker.SOS) break;
            WriteSegmentRaw(writer, original, seg);
        }

        // SOS 段
        var sosSegment = structure.Segments.First(s => s.Marker == JpegMarker.SOS);
        WriteSegmentRaw(writer, original, sosSegment);

        // 写入新的扫描数据
        writer.Write(newScanData);

        // EOI
        writer.Write((byte)0xFF);
        writer.Write(JpegMarker.EOI);
    }

    private static void WriteSegmentRaw(BinaryWriter writer, byte[] original, JpegSegment seg)
    {
        int start = seg.Offset;
        int length = seg.Length;
        writer.Write(original, start, length);
    }
}
