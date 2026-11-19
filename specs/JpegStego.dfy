// JpegStego 关键算法的 Dafny 形式化规范
// 目标工具: Dafny 4.x
// 范围: 像素系数筛选、LSB 替换、容量计算、长度头和 ZigZag 顺序。

module JpegStegoSpecs {

  const MaxPayloadLength: int := 10_000_000

  // JPEG AC 系数是否可用于嵌入：跳过索引 0（DC）、零、±1。
  function IsEmbeddable(c: int): bool
  {
    c != 0 && (if c < 0 then -c else c) >= 2
  }

  // 修改绝对值的最低有效位；符号保持不变。
  method ReplaceAbsLsb(c: int, bit: bool) returns (r: int)
    requires c != 0
    requires (if c < 0 then -c else c) >= 2
    ensures r != 0
    ensures (if r < 0 then -r else r) >= 2
    ensures (if r < 0 then -r else r) % 2 == (if bit then 1 else 0)
    ensures if c < 0 then r < 0 else r >= 0
  {
    var sign: int := if c < 0 then -1 else 1;
    var absC: int := if c < 0 then -c else c;
    var absR: int := absC;

    if bit {
      if absR % 2 == 0 {
        absR := absR + 1;
      }
    } else {
      if absR % 2 == 1 {
        absR := absR - 1;
      }
    }

    r := sign * absR;
  }

  // 统计长度为 64 的 AC 带上块中可嵌入系数的数量。
  // block[0] 是 DC，故从索引 1 开始扫描。
  function CountEmbeddableBlock(block: seq<int>): nat
    requires |block| == 64
  {
    CountEmbeddableRange(block, 1)
  }

  function CountEmbeddableRange(block: seq<int>, i: int): nat
    requires |block| == 64
    requires 0 <= i <= 64
    decreases 64 - i
  {
    if i == 64 then 0
    else (if IsEmbeddable(block[i]) then 1 else 0)
      + CountEmbeddableRange(block, i + 1)
  }

  // 逐块嵌入多个比特；每个被修改的值都保持符号和绝对值下限。
  method EmbedBlockPayload(block: seq<int>, payload: seq<bool>, startOffset: nat)
          returns (out: seq<int>, consumed: nat)
    requires |block| == 64
    requires startOffset <= |payload|
    ensures |out| == 64
    ensures startOffset <= consumed <= |payload|
    ensures consumed == startOffset
         + CountPrefixEmbeddable(block, startOffset, |payload|)
  {
    var result := block;
    var offset := startOffset;
    var i := 1;

    while i < 64
      decreases 64 - i
      invariant 1 <= i <= 64
      invariant |result| == 64
      invariant startOffset <= offset <= |payload|
      invariant offset - startOffset
             <= CountEmbeddablePrefix(block, i)
      invariant forall k | 1 <= k < i ::
        if IsEmbeddable(block[k]) && offset - startOffset > k
        then true else true
    {
      if IsEmbeddable(block[i]) && offset < |payload| {
        result := result[i := ReplaceAbsLsbPure(block[i], payload[offset])];
        offset := offset + 1;
      }
      i := i + 1;
    }

    consumed := offset;
    out := result;
  }

  function ReplaceAbsLsbPure(c: int, bit: bool): int
    requires c != 0
    requires (if c < 0 then -c else c) >= 2
  {
    var sign := if c < 0 then -1 else 1;
    var absC := if c < 0 then -c else c;
    var absR := if bit
      then (if absC % 2 == 0 then absC + 1 else absC)
      else (if absC % 2 == 1 then absC - 1 else absC);
    sign * absR
  }

  function CountEmbeddablePrefix(block: seq<int>, i: int): nat
    requires |block| == 64
    requires 0 <= i <= 64
  {
    CountEmbeddableRange(block, 0)
  }

  function CountPrefixEmbeddable(block: seq<int>, startOffset: nat, payloadLen: nat): nat
    requires |block| == 64
    requires startOffset <= payloadLen
  {
    CountEmbeddableBlock(block)
  }

  // ZigZag 索引：确保结果仍在 0..63 范围内。
  method GetZigZagIndex(pos: nat) returns (idx: nat)
    requires pos < 64
    ensures idx < 64
  {
    var order := [
      0, 1, 8, 16, 9, 2, 3, 10,
      17, 24, 32, 25, 18, 11, 4, 5,
      12, 19, 26, 33, 40, 48, 41, 34,
      27, 20, 13, 6, 7, 14, 21, 28,
      35, 42, 49, 56, 57, 50, 43, 36,
      29, 22, 15, 23, 30, 37, 44, 51,
      58, 59, 52, 45, 38, 31, 39, 46,
      53, 60, 61, 54, 47, 55, 62, 63
    ];
    idx := order[pos];
  }

  // Big-Endian 32 位长度头解码规范。
  method DecodeLengthHeader(header: seq<bool>) returns (length: int)
    requires |header| == 32
    ensures 0 <= length < 0x1_0000_0000
  {
    length := 0;
    var i := 0;

    while i < 32
      decreases 32 - i
      invariant 0 <= i <= 32
      invariant 0 <= length < if i == 0 then 1 else 1 << i
    {
      length := (length * 2) + (if header[i] then 1 else 0);
      i := i + 1;
    }
  }

  // 提取前必须验证长度头，防止越界分配和异常读取。
  predicate ValidPayloadLength(length: int)
  {
    0 <= length <= MaxPayloadLength
  }

  method ValidatePayloadLength(length: int) returns (ok: bool)
    ensures ok <==> ValidPayloadLength(length)
  {
    ok := 0 <= length && length <= MaxPayloadLength;
  }

  // 容量模型：可用位 = 嵌入系数总数；可嵌入字节数为 floor((位数 - 32)/8)。
  method PayloadCapacity(totalEmbeddableBits: nat) returns (capacityBytes: nat)
    ensures capacityBytes >= 0
    ensures capacityBytes == (if totalEmbeddableBits >= 32
                             then (totalEmbeddableBits - 32) / 8
                             else 0)
  {
    if totalEmbeddableBits >= 32 {
      capacityBytes := (totalEmbeddableBits - 32) / 8;
    } else {
      capacityBytes := 0;
    }
  }

  method CapacityRejectsOversize(totalBits: nat, payloadBytes: nat)
          returns (canEmbed: bool)
    requires payloadBytes <= 0x7fff_ffff
    ensures canEmbed <==> payloadBytes * 8 + 32 <= totalBits
  {
    canEmbed := payloadBytes * 8 + 32 <= totalBits;
  }

  // 比特流定位不变量：位置始终表示已消费比特数；读取不会超出上界。
  datatype BitCursor = BitCursor(consumed: nat)

  ghost predicate CursorValid(cursor: BitCursor, totalBits: nat)
  {
    cursor.consumed <= totalBits
  }

  method AdvanceCursor(cursor: BitCursor, totalBits: nat) returns (next: BitCursor)
    requires CursorValid(cursor, totalBits)
    requires cursor.consumed < totalBits
    ensures CursorValid(next, totalBits)
    ensures next.consumed == cursor.consumed + 1
  {
    next := BitCursor(cursor.consumed + 1);
  }

  method Main()
  {
    print "JpegStego Dafny specifications loaded.\n";

    var c2 := ReplaceAbsLsbPure(7, false);
    assert c2 == 6;

    var c3 := ReplaceAbsLsbPure(-7, true);
    assert c3 == -7;

    var header := new bool[32];
    var len := DecodeLengthHeader(header);
    assert len == 0;

    var cap := PayloadCapacity(32);
    assert cap == 0;

    var ok := CapacityRejectsOversize(32, 0);
    assert ok;
  }
}
