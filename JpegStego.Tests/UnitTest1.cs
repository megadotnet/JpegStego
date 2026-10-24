using System;
using System.IO;
using JpegStego;
using Xunit;

namespace JpegStego.Tests;

public class StegoTests
{
    private static string GetRepoPath(string relativePath)
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../.."));
        return Path.Combine(repoRoot, relativePath);
    }

    [Fact]
    public void TestExistingInputImage()
    {
        string inputJpeg = GetRepoPath("test_input.jpg");
        string secretText = File.ReadAllText(GetRepoPath("secret_photo.txt"));
        string outputJpeg = Path.GetTempFileName() + ".jpg";

        try
        {
            int capacity = Steganographer.GetCapacity(inputJpeg);
            Assert.True(capacity > 0);

            Steganographer.Embed(inputJpeg, outputJpeg, secretText);
            string extractedText = Steganographer.Extract(outputJpeg);

            Assert.Equal(secretText, extractedText);
        }
        finally
        {
            if (File.Exists(outputJpeg))
                File.Delete(outputJpeg);
        }
    }

    [Fact]
    public void TestCapacityCheck()
    {
        string inputJpeg = GetRepoPath("test_input.jpg");
        int capacity = Steganographer.GetCapacity(inputJpeg);
        Assert.True(capacity > 0);

        string outputJpeg = Path.GetTempFileName() + ".jpg";

        try
        {
            string hugeText = new string('A', capacity + 100);
            Assert.Throws<InvalidOperationException>(() => Steganographer.Embed(inputJpeg, outputJpeg, hugeText));
        }
        finally
        {
            if (File.Exists(outputJpeg))
                File.Delete(outputJpeg);
        }
    }

    [Fact]
    public void TestChainedEmbedAndExtract()
    {
        string inputJpeg = GetRepoPath("test_input.jpg");
        string message1 = "First secret message!";
        string message2 = "Second secret message that replaces the first one.";

        string stegoFile1 = Path.GetTempFileName() + ".jpg";
        string stegoFile2 = Path.GetTempFileName() + ".jpg";

        try
        {
            // First embedding
            Steganographer.Embed(inputJpeg, stegoFile1, message1);
            Assert.Equal(message1, Steganographer.Extract(stegoFile1));

            // Re-embedding into the output JPEG file
            Steganographer.Embed(stegoFile1, stegoFile2, message2);
            Assert.Equal(message2, Steganographer.Extract(stegoFile2));
        }
        finally
        {
            if (File.Exists(stegoFile1)) File.Delete(stegoFile1);
            if (File.Exists(stegoFile2)) File.Delete(stegoFile2);
        }
    }

    [Fact]
    public void TestDctDecodeEncodeRoundtrip()
    {
        string inputJpeg = GetRepoPath("test_input.jpg");
        byte[] fileData = File.ReadAllBytes(inputJpeg);
        var structure = JpegParser.Parse(fileData);

        var decoder = new DctDecoder(structure);
        var coefficients = decoder.DecodeCoefficients();

        Assert.NotNull(coefficients);
        Assert.True(coefficients.Count > 0);

        var encoder = new DctEncoder(structure);
        byte[] reencodedScan = encoder.EncodeCoefficients(coefficients);

        Assert.NotNull(reencodedScan);
        Assert.True(reencodedScan.Length > 0);
    }
}
