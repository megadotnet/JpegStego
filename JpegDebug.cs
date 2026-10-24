using System.Buffers.Binary;
using System.Text;
namespace JpegStego;

/// <summary>
/// JPEG 调试工具 - 详细分析 Huffman 解码过程
/// </summary>
internal static class JpegDebug
{
    public static void Analyze(string jpegPath, int maxScanBytes = 2000)
    {
        var data = File.ReadAllBytes(jpegPath);
        Console.WriteLine($"=== 文件分析: {Path.GetFileName(jpegPath)} ===");
        Console.WriteLine($"文件大小: {data.Length} 字节");

        // 检查 SOI
        if (data.Length < 2 || data[0] != 0xFF || data[1] != 0xD8)
        {
            Console.WriteLine("错误: 不是有效的 JPEG 文件");
            return;
        }

        // 解析结构
        try
        {
            var structure = JpegParser.Parse(data);
            Console.WriteLine($"\n图片尺寸: {structure.ImageWidth} x {structure.ImageHeight}");
            Console.WriteLine($"分量数: {structure.ComponentCount}");
            foreach (var comp in structure.Components)
            {
                int hFactor = (comp.SamplingFactor >> 4) & 0x0F;
                int vFactor = comp.SamplingFactor & 0x0F;
                Console.WriteLine($"  分量 {comp.Id}: {hFactor}x{vFactor}, QT={comp.QuantTableId}, DC={comp.DcTableId}, AC={comp.AcTableId}");
            }
            Console.WriteLine($"量化表: {structure.QuantTables.Count}");
            Console.WriteLine($"DC Huffman 表: {structure.HuffTablesDC.Count}");
            Console.WriteLine($"AC Huffman 表: {structure.HuffTablesAC.Count}");
            Console.WriteLine($"扫描数据偏移: {structure.ScanDataOffset}, 长度: {structure.ScanDataLength}");

            // 分析 DQT
            foreach (var qt in structure.QuantTables)
            {
                Console.WriteLine($"\n--- 量化表 {qt.Id} ---");
                for (int row = 0; row < 8; row++)
                {
                    for (int col = 0; col < 8; col++)
                        Console.Write($"{qt.Data[row * 8 + col],4}");
                    Console.WriteLine();
                }
            }

            // 分析 DHT
            foreach (var ht in structure.HuffTablesDC)
            {
                Console.WriteLine($"\n--- DC Huffman 表 {ht.Id} ---");
                Console.Write("码长计数: ");
                for (int i = 0; i < 16; i++)
                    Console.Write($"L{i+1}={ht.Bits[i]} ");
                Console.WriteLine($"\n符号数: {ht.Values.Length}");
                Console.Write("符号: ");
                for (int i = 0; i < Math.Min(ht.Values.Length, 30); i++)
                    Console.Write($"{ht.Values[i]:X2} ");
                if (ht.Values.Length > 30) Console.Write("...");
                Console.WriteLine();
            }

            foreach (var ht in structure.HuffTablesAC)
            {
                Console.WriteLine($"\n--- AC Huffman 表 {ht.Id} ---");
                Console.Write("码长计数: ");
                for (int i = 0; i < 16; i++)
                    Console.Write($"L{i+1}={ht.Bits[i]} ");
                Console.WriteLine($"\n符号数: {ht.Values.Length}");
                Console.Write("符号(前50): ");
                for (int i = 0; i < Math.Min(ht.Values.Length, 50); i++)
                    Console.Write($"{ht.Values[i]:X2} ");
                if (ht.Values.Length > 50) Console.Write("...");
                Console.WriteLine();

                // 检查是否有 EOB 和 ZRL
                bool hasEob = false, hasZrl = false;
                foreach (var v in ht.Values)
                {
                    if (v == 0x00) hasEob = true;
                    if (v == 0xF0) hasZrl = true;
                }
                Console.WriteLine($"包含 EOB(0x00): {hasEob}");
                Console.WriteLine($"包含 ZRL(0xF0): {hasZrl}");
            }

            // 详细分析前几个字节的扫描数据
            Console.WriteLine($"\n--- 扫描数据 (前100字节) ---");
            for (int i = 0; i < Math.Min(100, structure.ScanData.Length); i++)
            {
                if (i % 20 == 0) Console.Write($"\n{i,5}: ");
                Console.Write($"{structure.ScanData[i]:X2} ");
                // 标记 0xFF 字节
                if (structure.ScanData[i] == 0xFF)
                {
                    if (i + 1 < structure.ScanData.Length)
                    {
                        Console.Write($"[{structure.ScanData[i + 1]:X2}] ");
                        i++; // 跳过填充字节
                    }
                }
            }
            Console.WriteLine();

            // 尝试解码前几个 MCU
            Console.WriteLine($"\n--- 解码尝试 ---");
            TryDecodeScanData(structure, maxScanBytes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n解析错误: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    private static void TryDecodeScanData(JpegStructure structure, int maxBytes)
    {
        // 计算 MCU 结构
        if (structure.Components.Count == 0) return;

        int maxH = structure.Components.Max(c => (c.SamplingFactor >> 4) & 0x0F);
        int maxV = structure.Components.Max(c => c.SamplingFactor & 0x0F);
        int blocksPerMcu = structure.Components.Sum(c =>
            ((c.SamplingFactor >> 4) & 0x0F) * (c.SamplingFactor & 0x0F));

        Console.WriteLine($"最大采样: {maxH}x{maxV}, 每MCU块数: {blocksPerMcu}");

        var dcCodecs = new Dictionary<int, HuffmanCodec>();
        var acCodecs = new Dictionary<int, HuffmanCodec>();
        foreach (var ht in structure.HuffTablesDC)
            dcCodecs[ht.Id] = new HuffmanCodec(ht);
        foreach (var ht in structure.HuffTablesAC)
            acCodecs[ht.Id] = new HuffmanCodec(ht);

        var reader = new BitReader(structure.ScanData);
        var prevDc = new Dictionary<byte, int>();
        foreach (var comp in structure.Components)
            prevDc[comp.Id] = 0;

        int[] zigzag = {
             0, 1, 8,16, 9, 2, 3,10,17,24,32,25,18,11, 4, 5,
            12,19,26,33,40,48,41,34,27,20,13, 6, 7,14,21,28,
            35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,
            58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63
        };

        int mcuCount = 0;
        int totalMcuRows = (structure.ImageHeight + 8 * maxV - 1) / (8 * maxV);
        int totalMcuCols = (structure.ImageWidth + 8 * maxH - 1) / (8 * maxH);
        int totalMcu = totalMcuRows * totalMcuCols;
        Console.WriteLine($"总MCU数: {totalMcu} ({totalMcuCols}x{totalMcuRows})");

        int errors = 0;
        int maxErrors = 10;

        try
        {
            for (int mcu = 0; mcu < totalMcu && errors < maxErrors; mcu++)
            {
                int mcuBitOffset = reader.BytePosition * 8 + reader.BitPosition;

                foreach (var comp in structure.Components)
                {
                    int hFactor = (comp.SamplingFactor >> 4) & 0x0F;
                    int vFactor = comp.SamplingFactor & 0x0F;

                    for (int v = 0; v < vFactor; v++)
                    {
                        for (int h = 0; h < hFactor; h++)
                        {
                            try
                            {
                                // 解码 DC
                                int dcSize = dcCodecs[comp.DcTableId].DecodeSymbol(reader);
                                int dcValue = 0;
                                if (dcSize > 0)
                                    dcValue = ExpandCoeff(reader.ReadBits(dcSize), dcSize);
                                prevDc[comp.Id] += dcValue;

                                // 解码 AC
                                int acCount = 0;
                                while (acCount < 63)
                                {
                                    int acSymbol = acCodecs[comp.AcTableId].DecodeSymbol(reader);
                                    if (acSymbol == 0x00) break; // EOB
                                    if (acSymbol == 0xF0) // ZRL
                                    {
                                        acCount += 16;
                                        continue;
                                    }
                                    int run = (acSymbol >> 4) & 0x0F;
                                    int size = acSymbol & 0x0F;
                                    acCount += run;
                                    if (acCount >= 64) break;
                                    int acVal = ExpandCoeff(reader.ReadBits(size), size);
                                    acCount++;
                                }
                            }
                            catch (InvalidDataException ex)
                            {
                                errors++;
                                Console.WriteLine($"  MCU {mcu} 分量{comp.Id} ({h},{h}): {ex.Message}");
                                Console.WriteLine($"    字节位置: {reader.BytePosition}, 位: {reader.BitPosition}");

                                // 显示附近的比特
                                if (reader.BytePosition < structure.ScanData.Length)
                                {
                                    Console.Write("    附近字节: ");
                                    for (int i = Math.Max(0, reader.BytePosition - 2); i < Math.Min(structure.ScanData.Length, reader.BytePosition + 8); i++)
                                        Console.Write($"{structure.ScanData[i]:X2} ");
                                    Console.WriteLine();
                                }

                                // 跳过此块，尝试恢复
                                break;
                            }
                        }
                    }
                }

                mcuCount++;
                if (mcu < 5 || mcu % 100 == 0)
                {
                    Console.WriteLine($"  MCU {mcu}: OK (字节位置: {reader.BytePosition})");
                }
            }

            if (errors == 0)
            {
                Console.WriteLine($"\n成功解码 {mcuCount} 个MCU，无错误!");
            }
            else
            {
                Console.WriteLine($"\n共 {errors} 个解码错误");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n解码异常: {ex.Message}");
            Console.WriteLine($"已解码 {mcuCount} 个MCU");
        }
    }

    private static int ExpandCoeff(int value, int size)
    {
        if (size == 0) return 0;
        int threshold = 1 << (size - 1);
        if (value < threshold)
            return value + 1 - (1 << size);
        return value;
    }
}
