using System.Collections.Generic;
using System.Buffers.Binary;

namespace JpegStego;

/// <summary>
/// DCT 系数解码器 - 从 JPEG 扫描数据中提取 DCT 系数
/// </summary>
internal sealed class DctDecoder
{
    private readonly JpegStructure _structure;
    private readonly Dictionary<int, HuffmanCodec> _dcDecoders = new();
    private readonly Dictionary<int, HuffmanCodec> _acDecoders = new();
    private readonly Dictionary<int, byte[]> _quantTables = new();

    // 分量 -> 对应的 DC/AC Huffman 表 ID 和量化表 ID
    private readonly Dictionary<byte, (byte dcId, byte acId, byte quantId)> _componentMapping = new();

    public DctDecoder(JpegStructure structure)
    {
        _structure = structure;
        BuildCodecs();
        MapComponents();
    }

    private void BuildCodecs()
    {
        // 确保 Huffman 表完整（某些编码器生成的表可能缺少符号）
        foreach (var ht in _structure.HuffTablesDC)
        {
            HuffHelper.EnsureDcTable(ht);
            _dcDecoders[ht.Id] = new HuffmanCodec(ht);
        }
        foreach (var ht in _structure.HuffTablesAC)
        {
            HuffHelper.EnsureAcTable(ht);
            _acDecoders[ht.Id] = new HuffmanCodec(ht);
        }
        foreach (var qt in _structure.QuantTables)
            _quantTables[qt.Id] = qt.Data;
    }

    private void MapComponents()
    {
        foreach (var comp in _structure.Components)
        {
            _componentMapping[comp.Id] = (comp.DcTableId, comp.AcTableId, comp.QuantTableId);
        }
    }

    /// <summary>
    /// 解码所有 MCU 的 DCT 系数
    /// 返回: 每个分量一个系数列表（zigzag 顺序的 8x8 块）
    /// </summary>
    public Dictionary<byte, List<short[]>> DecodeCoefficients()
    {
        // 计算 MCU 数量
        int maxH = _structure.Components.Max(c => (c.SamplingFactor >> 4) & 0x0F);
        int maxV = _structure.Components.Max(c => c.SamplingFactor & 0x0F);
        int mcuWidth = 8 * maxH;
        int mcuHeight = 8 * maxV;
        int mcuCols = (_structure.ImageWidth + mcuWidth - 1) / mcuWidth;
        int mcuRows = (_structure.ImageHeight + mcuHeight - 1) / mcuHeight;
        int totalMcu = mcuCols * mcuRows;

        // 计算每个 MCU 的总块数
        int blocksPerMcu = _structure.Components.Sum(c =>
            ((c.SamplingFactor >> 4) & 0x0F) * (c.SamplingFactor & 0x0F));

        // 初始化结果
        var result = new Dictionary<byte, List<short[]>>();
        foreach (var comp in _structure.Components)
            result[comp.Id] = new List<short[]>();

        var reader = new BitReader(_structure.ScanData);
        var prevDc = new Dictionary<byte, int>();
        foreach (var comp in _structure.Components)
            prevDc[comp.Id] = 0;

        // 之字形顺序
        int[] zigzag = ZigZagOrder();

        try
        {
            for (int mcu = 0; mcu < totalMcu; mcu++)
            {
                // 在重启间隔处重置 DC 系数并对齐字节
                if (_structure.RestartInterval > 0 && mcu > 0 && mcu % _structure.RestartInterval == 0)
                {
                    reader.AlignToByte();
                    foreach (var comp in _structure.Components)
                        prevDc[comp.Id] = 0;
                }

                foreach (var comp in _structure.Components)
                {
                    int hFactor = (comp.SamplingFactor >> 4) & 0x0F;
                    int vFactor = comp.SamplingFactor & 0x0F;
                    for (int v = 0; v < vFactor; v++)
                    {
                        for (int h = 0; h < hFactor; h++)
                        {
                            var block = DecodeBlock(reader, comp.Id, prevDc[comp.Id]);
                            prevDc[comp.Id] = block[0]; // DC 是第一个元素
                            result[comp.Id].Add(block);
                        }
                    }
                }
            }
        }
        catch (EndOfStreamException)
        {
            // 到达扫描数据末尾
        }

        return result;
    }

    private short[] DecodeBlock(BitReader reader, byte compId, int previousDc)
    {
        if (!_componentMapping.TryGetValue(compId, out var mapping))
            throw new InvalidDataException($"未知分量: {compId}");

        var block = new short[64];
        int[] zigzag = ZigZagOrder();

        // 解码 DC
        int dcSymbol = _dcDecoders[mapping.dcId].DecodeSymbol(reader);
        int dcValue = 0;
        if (dcSymbol > 0)
        {
            dcValue = ExpandCoeff(reader.ReadBits(dcSymbol), dcSymbol);
        }
        block[0] = (short)(previousDc + dcValue);

        // 解码 AC
        int pos = 1; // zigzag 中的位置
        while (pos < 64)
        {
            int acSymbol = _acDecoders[mapping.acId].DecodeSymbol(reader);
            if (acSymbol == 0x00) // EOB
                break;
            if (acSymbol == 0xF0) // ZRL (16 个零)
            {
                pos += 16;
                continue;
            }
            int runLength = (acSymbol >> 4) & 0x0F;
            int coeffSize = acSymbol & 0x0F;
            pos += runLength;
            if (pos >= 64) break;
            int coeffValue = ExpandCoeff(reader.ReadBits(coeffSize), coeffSize);
            block[zigzag[pos]] = (short)coeffValue;
            pos++;
        }

        return block;
    }

    private static int ExpandCoeff(int value, int size)
    {
        if (size == 0) return 0;
        int threshold = 1 << (size - 1);
        if (value < threshold)
            return value + 1 - (1 << size);
        return value;
    }

    public static int[] ZigZagOrder()
    {
        return new int[]
        {
             0,  1,  8, 16,  9,  2,  3, 10,
            17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63
        };
    }
}
