using System.Collections.Generic;
using System.Linq;

namespace JpegStego;

/// <summary>
/// Huffman 表辅助 - 修复不完整的 Huffman 表
/// </summary>
internal static class HuffHelper
{
    /// <summary>
    /// 重建 DC Huffman 表，确保包含所有 12 个符号 (0-11)
    /// 通过扩展码长来填补缺失的编码位置
    /// </summary>
    public static void EnsureDcTable(HuffTable ht)
    {
        var have = new HashSet<int>(ht.Values.Select(v => (int)v));
        var missing = Enumerable.Range(0, 12).Where(s => !have.Contains(s)).ToList();
        if (missing.Count == 0) return;

        // 重建完整的编码映射
        var encodeMap = BuildEncodeMap(ht);
        
        // 为缺失的符号找到可用的编码位置
        // 策略：将某些短码扩展为两个更长的码，腾出空间给缺失符号
        FillMissingSymbols(encodeMap, missing, isDc: true);

        // 重建 bits 和 values
        RebuildTable(encodeMap, ht);
    }

    /// <summary>
    /// 确保 AC 表包含 EOB(0x00) 和 ZRL(0xF0)
    /// </summary>
    public static void EnsureAcTable(HuffTable ht)
    {
        var have = new HashSet<int>(ht.Values.Select(v => (int)v));
        var missing = new List<int>();
        if (!have.Contains(0x00)) missing.Add(0x00);
        if (!have.Contains(0xF0)) missing.Add(0xF0);
        if (missing.Count == 0) return;

        var encodeMap = BuildEncodeMap(ht);
        FillMissingSymbols(encodeMap, missing, isDc: false);
        RebuildTable(encodeMap, ht);
    }

    /// <summary>
    /// 从 HuffTable 构建编码映射 (symbol -> code, length)
    /// </summary>
    private static Dictionary<int, (int code, int length)> BuildEncodeMap(HuffTable ht)
    {
        var map = new Dictionary<int, (int code, int length)>();
        int idx = 0;
        int code = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < ht.Bits[len - 1]; i++)
            {
                if (idx < ht.Values.Length)
                {
                    map[ht.Values[idx]] = (code, len);
                    idx++;
                }
                code++;
            }
            code <<= 1;
        }
        return map;
    }

    /// <summary>
    /// 填补缺失的符号到编码树中
    /// </summary>
    private static void FillMissingSymbols(Dictionary<int, (int code, int length)> map, List<int> missing, bool isDc)
    {
        // 找出树中所有可用的叶子位置（从现有编码推导）
        // 策略：找到码长最短的叶子，将其扩展为两个更长的码
        // 重复直到有足够的空间放置所有缺失符号
        
        int maxSymbols = isDc ? 12 : 256;
        
        while (missing.Count > 0)
        {
            // 找到码长最短的叶子进行扩展
            var shortest = map.OrderBy(kv => kv.Value.length).First();
            int oldSymbol = shortest.Key;
            var (oldCode, oldLength) = shortest.Value;
            
            if (oldLength >= 16) break; // 无法再扩展
            
            // 移除旧编码，添加两个新编码（在末尾追加 0 和 1）
            map.Remove(oldSymbol);
            int newCode0 = (oldCode << 1);       // 末尾加 0
            int newCode1 = (oldCode << 1) | 1;   // 末尾加 1
            int newLength = oldLength + 1;
            
            map[oldSymbol] = (newCode0, newLength);
            
            // 用新编码分配一个缺失符号
            int symbol = missing[0];
            missing.RemoveAt(0);
            map[symbol] = (newCode1, newLength);
        }
    }

    /// <summary>
    /// 从编码映射重建 HuffTable
    /// </summary>
    private static void RebuildTable(Dictionary<int, (int code, int length)> map, HuffTable ht)
    {
        var bits = new byte[16];
        var values = new List<byte>();

        // 按编码长度和码值排序
        var sorted = map.OrderBy(kv => kv.Value.length).ThenBy(kv => kv.Value.code);

        foreach (var kv in sorted)
        {
            bits[kv.Value.length - 1]++;
            values.Add((byte)kv.Key);
        }

        ht.Bits = bits;
        ht.Values = values.ToArray();
    }
}
