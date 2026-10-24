using System.Collections.Generic;

namespace JpegStego;

/// <summary>
/// Huffman 编解码器 - 构建解码树/编码表
/// </summary>
internal sealed class HuffmanCodec
{
    // 解码树：每个节点是 (内部节点用两个子节点) 或 (叶节点用值)
    private sealed class Node
    {
        public int Symbol = -1;  // >=0 表示叶节点
        public Node? Left;       // 0
        public Node? Right;      // 1
    }

    private readonly Node _root = new();
    private readonly Dictionary<int, (int code, int length)> _encodeTable = new();

    public HuffmanCodec(HuffTable table)
    {
        BuildDecodeTree(table);
        CompleteTree();  // 填充缺失的子节点
        BuildEncodeTable(table);
    }

    private void BuildDecodeTree(HuffTable table)
    {
        int symbolIndex = 0;
        int code = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < table.Bits[length - 1]; i++)
            {
                if (symbolIndex >= table.Values.Length) break;
                int symbol = table.Values[symbolIndex++];
                InsertCode(code, length, symbol);
                code++;
            }
            code <<= 1;
        }
    }

    /// <summary>
    /// 填充缺失的子节点，使树完整
    /// 对于不完整的 Huffman 表（某些编码器生成的），将缺失的子节点用默认符号填充
    /// </summary>
    private void CompleteTree()
    {
        var queue = new Queue<Node>();
        queue.Enqueue(_root);
        
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            
            // 叶节点跳过
            if (node.Symbol >= 0) continue;
            
            // 缺失左子节点（0）- 用默认符号 0 填充
            if (node.Left == null)
            {
                node.Left = new Node { Symbol = 0 };
            }
            else if (node.Left.Symbol < 0)
            {
                queue.Enqueue(node.Left);
            }
            
            // 缺失右子节点（1）- 用默认符号 0 填充
            if (node.Right == null)
            {
                node.Right = new Node { Symbol = 0 };
            }
            else if (node.Right.Symbol < 0)
            {
                queue.Enqueue(node.Right);
            }
        }
    }

    private void InsertCode(int code, int length, int symbol)
    {
        Node current = _root;
        for (int i = length - 1; i >= 0; i--)
        {
            bool bit = ((code >> i) & 1) != 0;
            if (!bit)
            {
                current.Left ??= new Node();
                current = current.Left;
            }
            else
            {
                current.Right ??= new Node();
                current = current.Right;
            }
        }
        current.Symbol = symbol;
    }

    private void BuildEncodeTable(HuffTable table)
    {
        int symbolIndex = 0;
        int code = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < table.Bits[length - 1]; i++)
            {
                if (symbolIndex >= table.Values.Length) break;
                int symbol = table.Values[symbolIndex++];
                _encodeTable[symbol] = (code, length);
                code++;
            }
            code <<= 1;
        }
    }

    /// <summary>
    /// 从 BitReader 解码一个符号
    /// </summary>
    public int DecodeSymbol(BitReader reader)
    {
        Node current = _root;
        while (current.Symbol < 0)
        {
            if (reader.ReadBit())
                current = current.Right ?? throw new InvalidDataException("无效 Huffman 码");
            else
                current = current.Left ?? throw new InvalidDataException("无效 Huffman 码");
        }
        return current.Symbol;
    }

    /// <summary>
    /// 检查是否可以编码指定符号
    /// </summary>
    public bool CanEncode(int symbol)
    {
        return _encodeTable.ContainsKey(symbol);
    }

    /// <summary>
    /// 写入一个符号
    /// </summary>
    public void EncodeSymbol(BitWriter writer, int symbol)
    {
        if (!_encodeTable.TryGetValue(symbol, out var entry))
        {
            // 容错处理：用 EOB 替代无效符号（截断当前块）
            if (_encodeTable.TryGetValue(0x00, out var eob))
            {
                writer.WriteBits(eob.code, eob.length);
                return;
            }
            throw new InvalidOperationException($"无 Huffman 编码: {symbol}");
        }
        writer.WriteBits(entry.code, entry.length);
    }
}
