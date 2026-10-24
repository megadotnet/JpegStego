using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("JpegStego.Tests")]

﻿namespace JpegStego;

internal static class Program
{
    private const string VERSION = "1.0.0";

    private static readonly string Banner = @"
╔══════════════════════════════════════════════════════╗
║          JPEG DCT 域信息隐写工具 v{0,-8}           ║
║          .NET 9.0 | LSB 替换算法                     ║
╚══════════════════════════════════════════════════════╝";

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine(Banner, VERSION);
        Console.WriteLine();

        if (args.Length == 0)
        {
            ShowUsage();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "embed" or "e" => HandleEmbed(args),
                "extract" or "x" => HandleExtract(args),
                "capacity" or "c" => HandleCapacity(args),
                "debug" or "d" => HandleDebug(args),
                "help" or "h" or "--help" or "-h" => ShowUsage(),
                _ => ShowUsage()
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[错误] {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static int HandleEmbed(string[] args)
    {
        string inputJpeg = "";
        string outputJpeg = "";
        string textFile = "";
        string? directText = null;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-i" or "--input":
                    if (++i < args.Length) inputJpeg = args[i];
                    break;
                case "-o" or "--output":
                    if (++i < args.Length) outputJpeg = args[i];
                    break;
                case "-f" or "--file":
                    if (++i < args.Length) textFile = args[i];
                    break;
                case "-t" or "--text":
                    if (++i < args.Length) directText = args[i];
                    break;
            }
        }

        if (string.IsNullOrEmpty(inputJpeg) || string.IsNullOrEmpty(outputJpeg))
        {
            Console.WriteLine("用法: jpegstego embed -i <输入.jpg> -o <输出.jpg> -f <秘密.txt>");
            Console.WriteLine("  或: jpegstego embed -i <输入.jpg> -o <输出.jpg> -t \"直接文本\"");
            return 1;
        }

        if (!File.Exists(inputJpeg))
        {
            Console.WriteLine($"[错误] 输入图片不存在: {inputJpeg}");
            return 1;
        }

        string secret;
        if (!string.IsNullOrEmpty(textFile))
        {
            if (!File.Exists(textFile))
            {
                Console.WriteLine($"[错误] 文本文件不存在: {textFile}");
                return 1;
            }
            secret = File.ReadAllText(textFile);
            Console.WriteLine($"[信息] 已读取文本文件: {textFile} ({secret.Length} 字符)");
        }
        else if (directText != null)
        {
            secret = directText;
            Console.WriteLine($"[信息] 使用直接文本输入: {secret.Length} 字符");
        }
        else
        {
            Console.WriteLine("[错误] 请使用 -f <文件> 或 -t <文本> 指定秘密内容");
            return 1;
        }

        // 检查容量
        int capacity = Steganographer.GetCapacity(inputJpeg);
        byte[] secretBytes = System.Text.Encoding.UTF8.GetBytes(secret);
        Console.WriteLine($"[信息] 图片容量: {capacity} 字节 | 需要: {secretBytes.Length} 字节");

        if (secretBytes.Length > capacity)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[警告] 秘密文本 ({secretBytes.Length} 字节) 超过图片容量 ({capacity} 字节)");
            Console.WriteLine($"        建议: 使用更大尺寸或更高质量的 JPEG 图片");
            Console.ResetColor();
            return 1;
        }

        Console.Write("[处理] 正在嵌入数据...");
        Steganographer.Embed(inputJpeg, outputJpeg, secret);
        Console.WriteLine(" 完成!");

        FileInfo fi = new(outputJpeg);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[成功] 隐写完成 -> {outputJpeg} ({fi.Length / 1024.0:F1} KB)");
        Console.WriteLine($"       秘密文本长度: {secretBytes.Length} 字节 ({secret.Length} 字符)");
        Console.ResetColor();
        return 0;
    }

    private static int HandleExtract(string[] args)
    {
        string inputJpeg = "";

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-i" or "--input":
                    if (++i < args.Length) inputJpeg = args[i];
                    break;
            }
        }

        if (string.IsNullOrEmpty(inputJpeg))
        {
            Console.WriteLine("用法: jpegstego extract -i <隐写.jpg>");
            return 1;
        }

        if (!File.Exists(inputJpeg))
        {
            Console.WriteLine($"[错误] 输入图片不存在: {inputJpeg}");
            return 1;
        }

        Console.Write("[处理] 正在提取数据...");
        string extracted = Steganographer.Extract(inputJpeg);
        Console.WriteLine(" 完成!");

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n=== 提取的隐藏文本 ===");
        Console.ResetColor();
        Console.WriteLine(extracted);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=== 文本结束 ===");
        Console.ResetColor();

        Console.WriteLine($"\n[信息] 共 {extracted.Length} 字符 ({System.Text.Encoding.UTF8.GetByteCount(extracted)} 字节)");
        return 0;
    }

    private static int HandleCapacity(string[] args)
    {
        string inputJpeg = "";

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-i" or "--input":
                    if (++i < args.Length) inputJpeg = args[i];
                    break;
            }
        }

        if (string.IsNullOrEmpty(inputJpeg))
        {
            Console.WriteLine("用法: jpegstego capacity -i <图片.jpg>");
            return 1;
        }

        if (!File.Exists(inputJpeg))
        {
            Console.WriteLine($"[错误] 输入图片不存在: {inputJpeg}");
            return 1;
        }

        int capacity = Steganographer.GetCapacity(inputJpeg);
        var fileInfo = new FileInfo(inputJpeg);

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"╔══════════════════════════════════════╗");
        Console.WriteLine($"║     图片容量分析报告                 ║");
        Console.WriteLine($"╠══════════════════════════════════════╣");
        Console.WriteLine($"║ 文件名: {fileInfo.Name,-28}║");
        Console.WriteLine($"║ 文件大小: {fileInfo.Length / 1024.0,8:F1} KB{"",17}║");
        Console.WriteLine($"║ 最大嵌入容量: {capacity,7} 字节{"",13}║");
        Console.WriteLine($"║ 约等于: {capacity / 1024.0,8:F1} KB{"",18}║");
        Console.WriteLine($"╚══════════════════════════════════════╝");
        Console.ResetColor();
        return 0;
    }

    private static int HandleDebug(string[] args)
    {
        string inputJpeg = "";
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-i" or "--input":
                    if (++i < args.Length) inputJpeg = args[i];
                    break;
            }
        }
        if (string.IsNullOrEmpty(inputJpeg))
        {
            Console.WriteLine("用法: jpegstego debug -i <图片.jpg>");
            return 1;
        }
        if (!File.Exists(inputJpeg))
        {
            Console.WriteLine($"[错误] 文件不存在: {inputJpeg}");
            return 1;
        }
        try
        {
            JpegDebug.Analyze(inputJpeg);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int ShowUsage()
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(@"
═══════════════════════════════════════════════════════
  JPEG DCT 域信息隐写工具
═══════════════════════════════════════════════════════

用法:
  jpegstego embed   -i <输入.jpg> -o <输出.jpg> -f <秘密.txt>   从文件嵌入
  jpegstego embed   -i <输入.jpg> -o <输出.jpg> -t ""文本""        直接文本嵌入
  jpegstego extract -i <隐写.jpg>                                 提取隐藏文本
  jpegstego capacity -i <图片.jpg>                                查看嵌入容量
  jpegstego debug   -i <图片.jpg>                                 调试分析JPEG结构

参数:
  -i, --input   输入 JPEG 图片路径
  -o, --output  输出 JPEG 图片路径
  -f, --file    要嵌入的文本文件 (.txt)
  -t, --text    要嵌入的直接文本
  -h, --help    显示帮助

示例:
  jpegstego embed -i photo.jpg -o photo_stego.txt -f secret.txt
  jpegstego embed -i photo.jpg -o out.jpg -t ""机密信息123""
  jpegstego extract -i photo_stego.jpg
  jpegstego capacity -i photo.jpg

算法说明:
  基于 DCT（离散余弦变换）域的 LSB 替换隐写算法。
  将秘密数据嵌入到 JPEG AC 系数的最低有效位中。
  支持任意大小 JPEG (Baseline)，容量取决于图片尺寸和质量。
═══════════════════════════════════════════════════════
");
        Console.ResetColor();
        return 0;
    }
}
