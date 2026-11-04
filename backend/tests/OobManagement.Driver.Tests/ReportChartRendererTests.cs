using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using OobManagement.Infrastructure.Common;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>解压真实PNG像素验证文件结构和缺口，不能只检查data URI前缀。</summary>
public sealed class ReportChartRendererTests
{
    [Fact]
    public void Png_has_valid_chunks_crc_and_decodable_rgb_pixels()
    {
        var png = ReportChartRenderer.RenderPng([new("温度", "#ef4444", [10, 20, 15, 30])]);
        var (width, height, pixels) = Decode(png);
        Assert.Equal(960, width); Assert.Equal(240, height);
        Assert.Equal((width * 3 + 1) * height, pixels.Length);
        Assert.All(Enumerable.Range(0, height), row => Assert.Equal(0, pixels[row * (width * 3 + 1)]));
        Assert.True(CountColor(pixels, width, 0, width - 1, 0xef4444) > 100);
        Assert.True(png.Length < 192000);
    }

    [Fact]
    public void Missing_bucket_is_not_connected_but_contiguous_values_are()
    {
        const int width = 400, height = 160;
        var gap = Decode(ReportChartRenderer.RenderPng([new("功率", "#ef4444", [1, null, 3])], width, height));
        var connected = Decode(ReportChartRenderer.RenderPng([new("功率", "#ef4444", [1, 2, 3])], width, height));
        Assert.Equal(0, CountColor(gap.Pixels, width, 190, 250, 0xef4444));
        Assert.True(CountColor(connected.Pixels, width, 190, 250, 0xef4444) > 20);
    }

    [Fact]
    public void Empty_and_constant_series_render_without_inventing_samples_and_invalid_sizes_are_rejected()
    {
        var empty = Decode(ReportChartRenderer.RenderPng([new("空值", "#ef4444", [null, double.NaN, double.PositiveInfinity])]));
        Assert.Equal(0, CountColor(empty.Pixels, empty.Width, 0, empty.Width - 1, 0xef4444));
        Assert.NotEmpty(ReportChartRenderer.RenderPng([new("常量", "#ef4444", [0, 0, 0])]));
        Assert.Throws<ArgumentException>(() => ReportChartRenderer.RenderPng([]));
        Assert.Throws<ArgumentException>(() => ReportChartRenderer.RenderPng([new("越界", "#ef4444", [1])], 100000, 240));
        Assert.Throws<ArgumentException>(() => ReportChartRenderer.RenderPng([new("非法颜色", "red", [1])]));
        Assert.Throws<ArgumentException>(() => ReportChartRenderer.RenderPng([new("A", "#ef4444", [1]), new("B", "#3b82f6", [1, 2])]));
    }

    private static (int Width, int Height, byte[] Pixels) Decode(byte[] png)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8));
        var offset = 8; var width = 0; var height = 0;
        using var compressed = new MemoryStream();
        var types = new List<string>();
        while (offset < png.Length)
        {
            var count = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            types.Add(type);
            var expected = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + count, 4));
            var crc = uint.MaxValue;
            foreach (var value in png.AsSpan(offset + 4, count + 4))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            Assert.Equal(expected, ~crc);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset + 8, 4));
                height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset + 12, 4));
                Assert.Equal(8, png[offset + 16]); Assert.Equal(2, png[offset + 17]);
            }
            else if (type == "IDAT") compressed.Write(png, offset + 8, count);
            offset += count + 12;
        }
        Assert.Equal(["IHDR", "IDAT", "IEND"], types);
        compressed.Position = 0;
        using var decoder = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream(); decoder.CopyTo(raw);
        return (width, height, raw.ToArray());
    }

    private static int CountColor(byte[] pixels, int width, int fromX, int toX, int color)
    {
        var count = 0; var stride = width * 3 + 1;
        for (var y = 0; y < pixels.Length / stride; y++)
        for (var x = fromX; x <= toX; x++)
        {
            var offset = y * stride + 1 + x * 3;
            if (pixels[offset] == (byte)(color >> 16) && pixels[offset + 1] == (byte)(color >> 8) && pixels[offset + 2] == (byte)color) count++;
        }
        return count;
    }
}
