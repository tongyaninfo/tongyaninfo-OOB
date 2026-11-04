using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace OobManagement.Infrastructure.Common;

/// <summary>跨平台静态PNG折线图，不依赖本机字体、浏览器、远程图表服务或原生图形库。</summary>
public static class ReportChartRenderer
{
    public static string DataUri(IReadOnlyList<ReportChartLine> lines) => "data:image/png;base64," + Convert.ToBase64String(RenderPng(lines));

    public static byte[] RenderPng(IReadOnlyList<ReportChartLine> lines, int width = 960, int height = 240)
    {
        if (lines.Count is < 1 or > 16 || width is < 160 or > 2048 || height is < 80 or > 1024
            || lines.Any(x => x.Values.Count > 4096) || lines.Select(x => x.Values.Count).Distinct().Count() != 1)
            throw new ArgumentException("报表图表尺寸、曲线数量或点数无效");
        var pixels = new byte[checked(width * height * 3)];
        Array.Fill(pixels, (byte)255);
        var values = lines.SelectMany(x => x.Values).Where(x => x.HasValue && double.IsFinite(x.Value)).Select(x => x!.Value).ToArray();
        var minimum = values.Length == 0 ? 0 : values.Min();
        var maximum = values.Length == 0 ? 1 : values.Max();
        if (minimum == maximum)
        {
            var padding = Math.Max(1, Math.Abs(minimum) * .05);
            minimum -= padding; maximum += padding;
        }
        var span = maximum - minimum;
        if (!double.IsFinite(span)) throw new ArgumentException("报表图表数值范围超限");
        var step = NiceStep(span / 4);
        var low = Math.Floor(minimum / step) * step;
        var high = Math.Ceiling(maximum / step) * step;
        if (!double.IsFinite(low) || !double.IsFinite(high) || high <= low) { low = minimum; high = maximum; }
        const int left = 104, top = 14;
        var right = width - 12; var bottom = height - 20;
        for (var row = 0; row <= 4; row++)
        {
            var y = top + (bottom - top) * row / 4;
            Segment(left, y, right, y, 0xe5eaf1, false, 1);
            var label = (high - (high - low) * row / 4).ToString("G4", CultureInfo.InvariantCulture);
            Text(label, Math.Max(0, left - label.Length * 18 - 10), y - 10, 0x7b8798);
        }
        for (var column = 0; column <= 6; column++)
        {
            var x = left + (right - left) * column / 6;
            Segment(x, top, x, bottom, 0xf0f3f8, false, 1);
        }
        Segment(left, bottom, right, bottom, 0xcdd6e3, false, 1);
        foreach (var line in lines)
        {
            if (line.Color.Length != 7 || line.Color[0] != '#' || !int.TryParse(line.Color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var color))
                throw new ArgumentException("报表曲线颜色无效");
            (int X, int Y)? previous = null;
            for (var index = 0; index < line.Values.Count; index++)
            {
                var value = line.Values[index];
                if (!value.HasValue || !double.IsFinite(value.Value)) { previous = null; continue; }
                var x = left + (int)Math.Round(index * (right - left) / (double)Math.Max(1, line.Values.Count - 1));
                var y = Math.Clamp(top + (int)Math.Round((high - value.Value) / (high - low) * (bottom - top)), top, bottom);
                if (previous is { } start) Segment(start.X, start.Y, x, y, color, line.Dashed, 2);
                else Dot(x, y, color, 2);
                previous = (x, y);
            }
        }
        return EncodePng(pixels, width, height);

        void Dot(int x, int y, int color, int radius)
        {
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
                if (dx * dx + dy * dy <= radius * radius) Pixel(x + dx, y + dy, color);
        }
        void Pixel(int x, int y, int color)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            var offset = (y * width + x) * 3;
            pixels[offset] = (byte)(color >> 16); pixels[offset + 1] = (byte)(color >> 8); pixels[offset + 2] = (byte)color;
        }
        void Segment(int x0, int y0, int x1, int y1, int color, bool dashed, int thickness)
        {
            var dx = Math.Abs(x1 - x0); var sx = x0 < x1 ? 1 : -1;
            var dy = -Math.Abs(y1 - y0); var sy = y0 < y1 ? 1 : -1;
            var error = dx + dy; var distance = 0;
            while (true)
            {
                if (!dashed || distance % 12 < 7)
                {
                    Pixel(x0, y0, color);
                    if (thickness > 1) Pixel(x0, y0 + 1, color);
                }
                if (x0 == x1 && y0 == y1) break;
                var twice = 2 * error;
                if (twice >= dy) { error += dy; x0 += sx; }
                if (twice <= dx) { error += dx; y0 += sy; }
                distance++;
            }
        }
        void Text(string text, int x, int y, int color)
        {
            foreach (var character in text)
            {
                var glyph = Glyphs.GetValueOrDefault(character, "00000000000000000000000000000000000");
                for (var index = 0; index < 35; index++)
                    if (glyph[index] == '1')
                        for (var dy = 0; dy < 3; dy++)
                        for (var dx = 0; dx < 3; dx++)
                            Pixel(x + index % 5 * 3 + dx, y + index / 5 * 3 + dy, color);
                x += 18;
            }
        }
    }

    private static double NiceStep(double value)
    {
        if (value <= 0 || !double.IsFinite(value)) return 1;
        var scale = Math.Pow(10, Math.Floor(Math.Log10(value)));
        if (scale == 0 || !double.IsFinite(scale)) return value;
        var fraction = value / scale;
        return (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10) * scale;
    }

    private static byte[] EncodePng(byte[] pixels, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8; header[9] = 2; // RGB、不透明、无交错；无需字体或平台图形依赖。
        Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var encoder = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            for (var row = 0; row < height; row++)
            {
                encoder.WriteByte(0); // PNG每行独立选择None过滤，像素内容由zlib无损压缩。
                encoder.Write(pixels, row * width * 3, width * 3);
            }
        }
        Chunk(output, "IDAT", compressed.ToArray()); Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream target, string type, byte[] data)
    {
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, data.Length); target.Write(size);
        var name = Encoding.ASCII.GetBytes(type); target.Write(name); target.Write(data);
        var crc = uint.MaxValue;
        foreach (var value in name.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(size, ~crc); target.Write(size);
    }

    private static readonly Dictionary<char, string> Glyphs = new()
    {
        ['0']="01110100011001110101110011000101110", ['1']="00100011000010000100001000010001110",
        ['2']="01110100010000100010001000100011111", ['3']="11110000010000101110000010000111110",
        ['4']="00010001100101010010111110001000010", ['5']="11111100001000011110000010000111110",
        ['6']="01110100001000011110100011000101110", ['7']="11111000010001000100010000100001000",
        ['8']="01110100011000101110100011000101110", ['9']="01110100011000101111000010000101110",
        ['.']="00000000000000000000000000011000110", ['-']="00000000000000011111000000000000000",
        ['+']="00000001000010011111001000010000000", ['E']="11111100001000011110100001000011111"
    };
}
