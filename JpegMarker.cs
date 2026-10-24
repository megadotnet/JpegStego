namespace JpegStego;

/// <summary>
/// JPEG 标记常量定义
/// </summary>
internal static class JpegMarker
{
    public const byte SOI = 0xD8;   // Start of Image
    public const byte EOI = 0xD9;   // End of Image
    public const byte APP0 = 0xE0;  // JFIF
    public const byte APP1 = 0xE1;  // EXIF
    public const byte APP2 = 0xE2;
    public const byte APP13 = 0xED;
    public const byte APP14 = 0xEE;
    public const byte APP15 = 0xEF;
    public const byte DQT = 0xDB;   // Define Quantization Table
    public const byte SOF0 = 0xC0;  // Start of Frame (Baseline DCT)
    public const byte SOF2 = 0xC2;  // Start of Frame (Progressive DCT)
    public const byte DHT = 0xC4;   // Define Huffman Table
    public const byte SOS = 0xDA;   // Start of Scan
    public const byte DRI = 0xDD;   // Define Restart Interval
    public const byte COM = 0xFE;   // Comment
}

/// <summary>
/// JPEG 标记段信息
/// </summary>
internal sealed class JpegSegment
{
    public byte Marker { get; init; }
    public int Offset { get; init; }
    public int Length { get; init; }   // 包含长度字段的整个段
    public byte[] Data { get; init; } = Array.Empty<byte>(); // 不含标记，不含2字节长度
}

/// <summary>
/// JPEG 文件结构解析结果
/// </summary>
internal sealed class JpegStructure
{
    public byte[] RawFile { get; set; } = Array.Empty<byte>();
    public List<JpegSegment> Segments { get; set; } = new();
    public int ScanDataOffset { get; set; }
    public int ScanDataLength { get; set; }
    public byte[] ScanData { get; set; } = Array.Empty<byte>();
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public int ComponentCount { get; set; }
    public List<JpegComponentInfo> Components { get; set; } = new();
    public List<QuantTable> QuantTables { get; set; } = new();
    public List<HuffTable> HuffTablesDC { get; set; } = new();
    public List<HuffTable> HuffTablesAC { get; set; } = new();
    public byte[] PreviousRestartsHandled { get; set; } = Array.Empty<byte>();
    /// <summary>
    /// 重启间隔（0 = 无重启）
    /// </summary>
    public int RestartInterval { get; set; }
    /// <summary>
    /// 扫描数据中需要重置 DC 系数的 MCU 索引列表
    /// </summary>
    public List<int> RestartPositions { get; set; } = new();
}

internal sealed class JpegComponentInfo
{
    public byte Id { get; init; }
    public byte SamplingFactor { get; init; } // high 4=H, low 4=V
    public byte QuantTableId { get; init; }
    public byte DcTableId { get; set; }
    public byte AcTableId { get; set; }
}

internal sealed class QuantTable
{
    public byte Id { get; init; }
    public byte Precision { get; init; } // 0=8bit, 1=16bit
    public byte[] Data { get; init; } = new byte[64];
}

internal sealed class HuffTable
{
    public byte Id { get; set; }
    public byte TableClass { get; set; } // 0=DC, 1=AC
    public byte[] Bits { get; set; } = new byte[17]; // count per length
    public byte[] Values { get; set; } = Array.Empty<byte>();
}
