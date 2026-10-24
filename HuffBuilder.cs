using System.Collections.Generic;
namespace JpegStego;

/// <summary>
/// 从 Huffman 表构建编码查找表的工具
/// </summary>
internal static class HuffBuilder
{
    public static Dictionary<int, (int code, int length)> BuildEncodeTable(byte[] bits, byte[] values)
    {
        var table = new Dictionary<int, (int, int)>();
        int symbolIdx = 0;
        int code = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < bits[length - 1]; i++)
            {
                int symbol = values[symbolIdx++];
                table[symbol] = (code, length);
                code++;
            }
            code <<= 1;
        }
        return table;
    }
}
