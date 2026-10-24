using System.Collections.Generic;
using System.Buffers.Binary;

namespace JpegStego;

/// <summary>
/// DCT 系数编码器 - 将修改后的 DCT 系数重新编码为 JPEG 比特流
/// </summary>
internal sealed class DctEncoder
{
    private readonly JpegStructure _structure;
    private readonly Dictionary<int, HuffmanCodec> _dcEncoders = new();
    private readonly Dictionary<int, HuffmanCodec> _acEncoders = new();
    private readonly Dictionary<byte, (byte dcId, byte acId, byte quantId)> _componentMapping = new();

    public DctEncoder(JpegStructure structure)
    {
        _structure = structure;
        BuildCodecs();
        MapComponents();
    }

    private void BuildCodecs()
    {
        foreach (var ht in _structure.HuffTablesDC)
            _dcEncoders[ht.Id] = new HuffmanCodec(ht);
        foreach (var ht in _structure.HuffTablesAC)
            _acEncoders[ht.Id] = new HuffmanCodec(ht);
    }

    private void MapComponents()
    {
        foreach (var comp in _structure.Components)
        {
            _componentMapping[comp.Id] = (comp.DcTableId, comp.AcTableId, comp.QuantTableId);
        }
    }

    /// <summary>
    /// 将修改后的系数重新编码为扫描数据
    /// </summary>
    public byte[] EncodeCoefficients(Dictionary<byte, List<short[]>> coefficients)
    {
        int[] zigzag = DctDecoder.ZigZagOrder();
        var writer = new BitWriter();
        var prevDc = new Dictionary<byte, int>();
        foreach (var comp in _structure.Components)
            prevDc[comp.Id] = 0;

        // 计算每个 MCU 的块结构（与解码时一致）
        int maxH = _structure.Components.Max(c => (c.SamplingFactor >> 4) & 0x0F);
        int maxV = _structure.Components.Max(c => c.SamplingFactor & 0x0F);
        int mcuWidth = 8 * maxH;
        int mcuHeight = 8 * maxV;
        int mcuCols = (_structure.ImageWidth + mcuWidth - 1) / mcuWidth;
        int mcuRows = (_structure.ImageHeight + mcuHeight - 1) / mcuHeight;
        int totalMcu = mcuCols * mcuRows;

        // 计算每个分量在每个 MCU 中的块索引
        var blockIndices = new Dictionary<byte, int>();
        foreach (var comp in _structure.Components)
            blockIndices[comp.Id] = 0;

        int rstCount = 0;
        for (int mcu = 0; mcu < totalMcu; mcu++)
        {
            if (_structure.RestartInterval > 0 && mcu > 0 && mcu % _structure.RestartInterval == 0)
            {
                writer.WriteRestartMarker(rstCount++);
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
                        int idx = blockIndices[comp.Id];
                        if (idx >= coefficients[comp.Id].Count) goto done;

                        var block = coefficients[comp.Id][idx];
                        EncodeBlock(writer, block, prevDc[comp.Id], comp.Id, zigzag);
                        prevDc[comp.Id] = block[0];
                        blockIndices[comp.Id]++;
                    }
                }
            }
        }

    done:
        return writer.ToByteArray();
    }

    private void EncodeBlock(BitWriter writer, short[] block, int previousDc, byte compId, int[] zigzag)
    {
        if (!_componentMapping.TryGetValue(compId, out var mapping))
            throw new InvalidOperationException($"未知分量: {compId}");

        // 编码 DC
        int dcDiff = block[0] - previousDc;
        if (dcDiff == 0)
        {
            _dcEncoders[mapping.dcId].EncodeSymbol(writer, 0);
        }
        else
        {
            int coeffSize = GetCoeffSize(dcDiff);
            _dcEncoders[mapping.dcId].EncodeSymbol(writer, coeffSize);
            int value = dcDiff;
            if (dcDiff < 0)
                value = dcDiff - 1 + (1 << coeffSize);
            writer.WriteBits(value, coeffSize);
        }

        // 编码 AC
        int lastNonZero = 0;
        for (int i = 63; i >= 1; i--)
        {
            if (block[zigzag[i]] != 0)
            {
                lastNonZero = i;
                break;
            }
        }

        if (lastNonZero == 0)
        {
            _acEncoders[mapping.acId].EncodeSymbol(writer, 0x00); // EOB
            return;
        }

        int runLength = 0;
        for (int i = 1; i <= lastNonZero; i++)
        {
            int zigzagIdx = zigzag[i];
            int coeff = block[zigzagIdx];

            if (i < lastNonZero && coeff == 0)
            {
                runLength++;
                if (runLength == 16)
                {
                    _acEncoders[mapping.acId].EncodeSymbol(writer, 0xF0); // ZRL
                    runLength = 0;
                }
            }
            else
            {
                while (runLength >= 16)
                {
                    _acEncoders[mapping.acId].EncodeSymbol(writer, 0xF0); // ZRL
                    runLength -= 16;
                }

                int coeffSize = GetCoeffSize(coeff);
                int symbol = (runLength << 4) | coeffSize;
                _acEncoders[mapping.acId].EncodeSymbol(writer, symbol);

                int value = coeff;
                if (coeff < 0)
                    value = coeff - 1 + (1 << coeffSize);
                writer.WriteBits(value, coeffSize);
                runLength = 0;
            }
        }

        if (lastNonZero < 63)
        {
            _acEncoders[mapping.acId].EncodeSymbol(writer, 0x00); // EOB
        }
    }

    private static int GetCoeffSize(int value)
    {
        if (value == 0) return 0;
        int abs = System.Math.Abs(value);
        int size = 0;
        while (abs > 0)
        {
            size++;
            abs >>= 1;
        }
        return size;
    }
}
