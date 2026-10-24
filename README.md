# JpegStego - JPEG DCT 域信息隐写工具

`JpegStego` 是一个基于 C# / .NET 10 开发的高性能 JPEG 图像隐写工具。它利用离散余弦变换（DCT）频域中的最低有效位（LSB）替换算法，将机密文本数据安全地嵌入到 JPEG 图像的 AC 系数中，并在不破坏图像原有视觉质量的前提下实现机密信息的隐蔽传输与提取。

---

## 目录

- [工程技术栈](#工程技术栈)
- [核心架构与算法原理](#核心架构与算法原理)
- [环境要求](#环境要求)
- [构建与运行](#构建与运行)
- [命令行使用指南](#命令行使用指南)
- [单元测试](#单元测试)
- [部署与发布](#部署与发布)
- [许可证](#许可证)

---

## 工程技术栈

- **开发语言**: C# 13 (.NET 10.0)
- **运行框架**: .NET 10 SDK / Runtime
- **测试框架**: xUnit, Microsoft.NET.Test.Sdk
- **核心组件**:
  - `System.Buffers.Binary`: 大端/小端高字节序列化
  - 自建 JPEG 二进制解析器与 Huffman 哈夫曼编解码器

---

## 核心架构与算法原理

与普通的空域 LSB 隐写（直接修改像素 RGB 值）不同，`JpegStego` 直接对 JPEG 压缩流中的 DCT 系数进行处理，隐写后依然保持标准的 JPEG 编码规范。

```
                       [ 嵌入流程 ]
原始 JPEG ──> JpegParser ──> DctDecoder ──> DCT系数表 ──> LSB 替换 ──> DctEncoder ──> 重构 JPEG
                                                             │
                                                          秘密数据

                       [ 提取流程 ]
隐写 JPEG ──> JpegParser ──> DctDecoder ──> DCT系数表 ──> 读取 LSB ──> 提取秘密数据
```

### 核心模块职责

1. **JPEG 标记与段解析 (`JpegParser`, `JpegMarker`)**:
   - 解析 JPEG 结构标记（SOI, DQT, DHT, SOF0, SOS, EOI, APPx 等），定位 Header 段与图像 Scan 扫描数据。
2. **比特流读写器 (`BitReader`, `BitWriter`)**:
   - 实现位级别的读取与写入，自动处理 JPEG 标志性的字节填充（Byte Stuffing，如 `0xFF 0x00` 转译）。
3. **哈夫曼编解码 (`HuffmanCodec`, `HuffBuilder`, `HuffHelper`)**:
   - 根据 JPEG DHT 段构造 Huffman 树，对 DC/AC 游程编码（Run-Length Encoding）与范畴值（Category）进行解码与重编码。
4. **DCT 系数解码与编码 (`DctDecoder`, `DctEncoder`)**:
   - 将熵编码的图像 Scan 数据解码为各 MCU（最小编码单元）的 8x8 DCT 量化系数矩阵，并支持反向编码写回 bitstream。
5. **隐写核心逻辑 (`Steganographer`)**:
   - **载荷结构**: 4 字节 Big-Endian 文本长度 Header + UTF-8 编码文本 Payload。
   - **嵌入规则**: 筛选 AC 系数（跳过 DC 系数与零系数），仅选择绝对值 $|coeff| \ge 2$ 的 AC 系数，对其绝对值的最低有效位进行 LSB 替换，保证修改后系数编码尺寸不会跨级变化，最大程度保持图像质量与隐蔽性。
6. **结构调试器 (`JpegDebug`)**:
   - 提供 JPEG 文件结构的详细分析与调试输出（段偏移量、MCU 块统计、Huffman 表项等）。

---

## 环境要求

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) 或更高版本

---

## 构建与运行

### 1. 克隆 / 下载项目

```bash
git clone <repository-url>
cd JpegStego
```

### 2. 编译项目

```bash
dotnet build
```

---

## 命令行使用指南

项目编译完成后，可以通过 `dotnet run -- [command] [options]` 或直接执行编译生成的二进制程序。

### 1. 查看帮助信息

```bash
dotnet run -- help
```

### 2. 查看图片隐藏数据容量

在嵌入数据前，可以分析目标 JPEG 图片的最大嵌入容量：

```bash
dotnet run -- capacity -i input.jpg
```

**输出示例**:
```text
╔══════════════════════════════════════╗
║     图片容量分析报告                 ║
╠══════════════════════════════════════╣
║ 文件名: input.jpg                   ║
║ 文件大小:    124.5 KB                ║
║ 最大嵌入容量:    1536 字节           ║
║ 约等于:      1.5 KB                  ║
╚══════════════════════════════════════╝
```

### 3. 嵌入机密信息 (`embed`)

将文本嵌入到 JPEG 图像中：

- **从文件读取文本嵌入**:
  ```bash
  dotnet run -- embed -i input.jpg -o output_stego.jpg -f secret.txt
  ```

- **使用直接字符串嵌入**:
  ```bash
  dotnet run -- embed -i input.jpg -o output_stego.jpg -t "这是一条机密信息"
  ```

### 4. 提取机密信息 (`extract`)

从嵌入了隐写信息的 JPEG 图像中提取原始文本：

```bash
dotnet run -- extract -i output_stego.jpg
```

**输出示例**:
```text
[处理] 正在提取数据... 完成!

=== 提取的隐藏文本 ===
这是一条机密信息
=== 文本结束 ===

[信息] 共 8 字符 (24 字节)
```

### 5. 调试分析 JPEG 结构 (`debug`)

输出 JPEG 图片的详细 Mark 段分布与 MCU 信息：

```bash
dotnet run -- debug -i input.jpg
```

---

## 单元测试

项目包含基于 xUnit 的自动化测试套件，涵盖容量计算、数据嵌入与提取回溯、DCT 编解码 Roundtrip 等。

运行测试命令：

```bash
dotnet test JpegStego.Tests/JpegStego.Tests.csproj
```

---

## 部署与发布

### 1. 独立部署 (Self-Contained)

如果不希望目标机器安装 .NET Runtime，可将应用打包为独立的单文件可执行程序：

#### Linux (x64):
```bash
dotnet publish JpegStego.csproj -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true
```

#### Windows (x64):
```bash
dotnet publish JpegStego.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

#### macOS (arm64):
```bash
dotnet publish JpegStego.csproj -c Release -r osx-arm64 --self-contained true /p:PublishSingleFile=true
```

生成的可执行文件将存放在 `bin/Release/net10.0/<runtime-identifier>/publish/` 目录下。

### 2. 框架依赖部署 (Framework-Dependent)

如果目标环境已安装 .NET 10 运行时：

```bash
dotnet publish JpegStego.csproj -c Release -o ./publish
```

在目标机器上通过 `dotnet JpegStego.dll [args]` 运行。

---

## 许可证

本项目采用 [MIT License](LICENSE) 授权。
