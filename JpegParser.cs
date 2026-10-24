using System.Buffers.Binary;

namespace JpegStego;

/// <summary>
/// JPEG 文件结构解析器 - 提取 DCT 系数、Huffman 表、量化表等
/// </summary>
internal sealed class JpegParser
{
    public static JpegStructure Parse(byte[] fileData)
    {
        var structure = new JpegStructure { RawFile = fileData };

        if (fileData.Length < 2 || fileData[0] != 0xFF || fileData[1] != JpegMarker.SOI)
            throw new InvalidDataException("不是有效的 JPEG 文件");

        int pos = 2; // skip SOI
        List<byte> scanBytes = new();

        while (pos < fileData.Length - 1)
        {
            // 跳过 0xFF 填充字节
            if (fileData[pos] != 0xFF)
            {
                pos++;
                continue;
            }

            // 跳过连续的 0xFF
            while (pos < fileData.Length && fileData[pos] == 0xFF)
                pos++;
            if (pos >= fileData.Length) break;

            byte marker = fileData[pos];
            pos++;

            // 无需长度的标记
            if (marker == JpegMarker.SOI)
                continue;
            if (marker == JpegMarker.EOI)
                break;
            if (marker >= 0xD0 && marker <= 0xD7) // RST markers
                continue;

            // 读取段长度
            if (pos + 1 >= fileData.Length)
                break;
            int segLen = BinaryPrimitives.ReadUInt16BigEndian(fileData.AsSpan(pos, 2));
            int dataLen = segLen - 2;
            int dataStart = pos + 2;
            int segStart = pos - 2; // 包含 0xFF + marker

            if (dataStart + dataLen > fileData.Length)
                throw new InvalidDataException("JPEG 数据截断");

            var segment = new JpegSegment
            {
                Marker = marker,
                Offset = segStart,
                Length = segLen + 2,
                Data = fileData.AsSpan(dataStart, dataLen).ToArray()
            };
            structure.Segments.Add(segment);

            // 解析关键段
            switch (marker)
            {
                case JpegMarker.SOF0:
                case JpegMarker.SOF2:
                    ParseSOF(segment.Data, structure);
                    break;
                case JpegMarker.DQT:
                    ParseDQT(segment.Data, structure);
                    break;
                case JpegMarker.DRI:
                    ParseDRI(segment.Data, structure);
                    break;
                case JpegMarker.DHT:
                    ParseDHT(segment.Data, structure);
                    break;
                case JpegMarker.SOS:
                    // SOS 之后是压缩数据
                    structure.ScanDataOffset = dataStart + dataLen;
                    pos = dataStart + dataLen;
                    // 收集扫描数据直到 EOI
                    CollectScanData(fileData, pos, structure);
                    goto done;
            }

            pos = dataStart + dataLen;
        }

    done:
        return structure;
    }

    private static void ParseSOF(byte[] data, JpegStructure structure)
    {
        int pos = 1; // skip precision
        if (data.Length < 8) return;
        structure.ImageHeight = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2)); pos += 2;
        structure.ImageWidth = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2)); pos += 2;
        structure.ComponentCount = data[pos]; pos++;
        for (int i = 0; i < structure.ComponentCount; i++)
        {
            if (pos + 2 >= data.Length) break;
            structure.Components.Add(new JpegComponentInfo
            {
                Id = data[pos++],
                SamplingFactor = data[pos++],
                QuantTableId = data[pos++]
            });
        }
    }

    private static void ParseDQT(byte[] data, JpegStructure structure)
    {
        int pos = 0;
        while (pos < data.Length)
        {
            if (pos >= data.Length) break;
            byte idAndPrec = data[pos++];
            int precision = (idAndPrec >> 4) & 0x0F;
            byte id = (byte)(idAndPrec & 0x0F);
            int count = 64 * (precision + 1);
            if (pos + count > data.Length) break;
            var qt = new QuantTable
            {
                Id = id,
                Precision = (byte)precision,
                Data = data.AsSpan(pos, 64).ToArray()
            };
            // 扩展 16-bit 量化值到 8-bit
            if (precision == 1)
            {
                for (int i = 0; i < 64; i++)
                    qt.Data[i] = (byte)(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + i * 2, 2)) >> 8);
            }
            structure.QuantTables.Add(qt);
            pos += count;
        }
    }

    private static void ParseDHT(byte[] data, JpegStructure structure)
    {
        int pos = 0;
        while (pos < data.Length)
        {
            if (pos >= data.Length) break;
            byte classAndId = data[pos++];
            byte tableClass = (byte)((classAndId >> 4) & 0x0F);
            byte id = (byte)(classAndId & 0x0F);

            if (pos + 16 > data.Length) break;
            var bits = data.AsSpan(pos, 16).ToArray();
            pos += 16;

            int valueCount = bits.Sum(b => b);
            if (pos + valueCount > data.Length) break;
            var values = data.AsSpan(pos, valueCount).ToArray();
            pos += valueCount;

            var ht = new HuffTable
            {
                Id = id,
                TableClass = tableClass,
                Bits = bits,
                Values = values
            };
            if (tableClass == 0)
                structure.HuffTablesDC.Add(ht);
            else
                structure.HuffTablesAC.Add(ht);
        }
    }

    private static void CollectScanData(byte[] fileData, int startPos, JpegStructure structure)
    {
        // 扫描数据以 0xFF 0xD9 (EOI) 结束
        // 0xFF 后面跟 0x00 是字节填充（stuffed），需要跳过
        // 0xFF 后面跟 0xD0-0xD7 是 RST 标记，需要移除并记录 DC 重置位置
        int pos = startPos;
        List<byte> scan = new();
        int mcuCounter = 0;
        int bytesSinceLastMCU = 0;
        int bytesPerMcuEstimate = structure.Components.Sum(c => ((c.SamplingFactor >> 4) & 0x0F) * (c.SamplingFactor & 0x0F)) * 8;

        while (pos < fileData.Length - 1)
        {
            if (fileData[pos] == 0xFF)
            {
                byte next = fileData[pos + 1];
                if (next == 0xD9) // EOI
                    break;
                if (next == 0x00) // 字节填充
                {
                    scan.Add(0xFF);
                    pos += 2;
                    continue;
                }
                // RST 标记 0xD0-0xD7：跳过不写入，记录 DC 重置位置
                if (next >= 0xD0 && next <= 0xD7)
                {
                    structure.RestartPositions.Add(mcuCounter);
                    pos += 2;
                    continue;
                }
                // 其他 0xFF xx：写入 0xFF 并继续（某些编码器会生成非标准序列）
                scan.Add(0xFF);
                pos += 1;
                continue;
            }
            else
            {
                scan.Add(fileData[pos]);
                pos++;
            }
        }

        structure.ScanData = scan.ToArray();
        structure.ScanDataLength = scan.Count;
    }

    private static void ParseDRI(byte[] data, JpegStructure structure)
    {
        if (data.Length >= 2)
        {
            structure.RestartInterval = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2));
        }
    }
}
