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

        // 确保 Huffman 表包含编码所需的必要符号
        EnsureHuffmanTables(structure);

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

        // 重新编码（使用更新后的 Huffman 表）
        var encoder = new DctEncoder(structure);
        byte[] newScanData = encoder.EncodeCoefficients(coefficients);

        // 重建 JPEG 文件（包含更新后的 DHT 段）
        RebuildJpeg(fileData, structure, newScanData, outputJpeg);
    }

    /// <summary>
    /// 确保 Huffman 表包含必要的编码符号
    /// </summary>
    private static void EnsureHuffmanTables(JpegStructure structure)
    {
        foreach (var ht in structure.HuffTablesDC)
            HuffHelper.EnsureDcTable(ht);
        foreach (var ht in structure.HuffTablesAC)
            HuffHelper.EnsureAcTable(ht);
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
    /// 判断系数是否可嵌入：|coeff| >= 2 且 coeff != -2
    /// （-2 的 LSB 是 0，嵌入 1 会变成 -1，绝对值降到 1，破坏提取一致性）
    /// </summary>
    private static bool IsEmbeddable(short coeff)
    {
        return Math.Abs(coeff) >= 2 && coeff != -2;
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

                    // LSB 替换：修改最低位
                    block[i] = (short)((block[i] & ~1) | (bit ? 1 : 0));
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
                        return (block[i] & 1) != 0;
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

        // 写入所有段直到 SOS，DHT 段使用更新后的数据
        foreach (var seg in structure.Segments)
        {
            if (seg.Marker == JpegMarker.SOS) break;

            if (seg.Marker == JpegMarker.DHT)
            {
                // 使用更新后的 Huffman 表重写 DHT 段
                WriteUpdatedDht(writer, structure, seg);
            }
            else
            {
                // 其他段直接复制原始数据
                WriteSegmentRaw(writer, original, seg);
            }
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

    private static void WriteUpdatedDht(BinaryWriter writer, JpegStructure structure, JpegSegment originalSeg)
    {
        var updatedData = BuildUpdatedDhtData(originalSeg, structure);
        writer.Write((byte)0xFF);
        writer.Write(JpegMarker.DHT);
        ushort length = (ushort)(updatedData.Length + 2);
        byte[] lenBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lenBytes.AsSpan(), length);
        writer.Write(lenBytes);
        writer.Write(updatedData);
    }

    private static byte[] BuildUpdatedDhtData(JpegSegment originalSeg, JpegStructure structure)
    {
        var result = new List<byte>();
        int pos = 0;
        var data = originalSeg.Data;

        while (pos < data.Length)
        {
            if (pos >= data.Length) break;
            byte classAndId = data[pos++];
            byte tableClass = (byte)((classAndId >> 4) & 0x0F);
            byte id = (byte)(classAndId & 0x0F);

            // 找到对应的更新表
            HuffTable? match;
            if (tableClass == 0)
                match = structure.HuffTablesDC.FirstOrDefault(t => t.Id == id);
            else
                match = structure.HuffTablesAC.FirstOrDefault(t => t.Id == id);

            if (match is null)
            {
                // 找不到匹配的，安全复制原始数据（带边界检查）
                result.Add(classAndId);
                if (pos + 16 > data.Length) break;
                var origBits = new byte[16];
                Array.Copy(data, pos, origBits, 0, 16);
                result.AddRange(origBits);
                pos += 16;
                int valCount = origBits.Sum(b => b);
                if (pos + valCount > data.Length) valCount = data.Length - pos;
                var origVals = new byte[valCount];
                Array.Copy(data, pos, origVals, 0, valCount);
                result.AddRange(origVals);
                pos += valCount;
            }
            else
            {
                // 使用更新后的表
                result.Add(classAndId);
                result.AddRange(match.Bits);
                result.AddRange(match.Values);
            }
        }

        return result.ToArray();
    }
}
