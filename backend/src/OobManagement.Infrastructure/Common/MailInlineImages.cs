using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MimeKit;

namespace OobManagement.Infrastructure.Common;

/// <summary>仅转换可信报表模板的内嵌PNG；不访问网络或本地文件，原始HTML仍由业务层冻结保存。</summary>
public static class MailInlineImages
{
    public const int MaximumCount = 16;
    public const int MaximumImageBytes = 192000;
    public const int MaximumTotalBytes = 320000;
    private const string PngPrefix = "data:image/png;base64,";
    private static readonly Regex ImageTags = new("<img(?=\\s|/?>)[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ImageSource = new("\\ssrc\\s*=\\s*(?:\"(?<source>[^\"]*)\"|'(?<source>[^']*)'|(?<source>[^\\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static MimeEntity CreateAlternative(string textBody, string canonicalHtml)
    {
        var images = new List<(string ContentId, byte[] Bytes)>();
        var totalBytes = 0;
        // 这里只解析本项目生成的img/src，不是面向任意HTML的清洗器；名称等文本已由模板生成器编码。
        var html = ImageTags.Replace(canonicalHtml, tag =>
        {
            var sourceAttribute = ImageSource.Match(tag.Value);
            if (!sourceAttribute.Success) return tag.Value;
            var source = sourceAttribute.Groups["source"];
            if (!source.Value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return tag.Value;
            if (!source.Value.StartsWith(PngPrefix, StringComparison.OrdinalIgnoreCase)) throw InvalidImage();
            if (images.Count >= MaximumCount) throw new ArgumentException("运行报表内嵌图片超过16张。", "inlineImages");
            var encoded = source.Value[PngPrefix.Length..];
            if (encoded.Length == 0 || encoded.Length > ((MaximumImageBytes + 2) / 3) * 4
                || encoded.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('+' or '/' or '='))) throw InvalidImage();
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw InvalidImage(); }
            if (bytes.Length > MaximumImageBytes || totalBytes + bytes.Length > MaximumTotalBytes)
                throw new ArgumentException("运行报表内嵌图片大小超过允许范围。", "inlineImages");
            ValidatePng(bytes);
            totalBytes += bytes.Length;
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var contentId = $"report-chart-{images.Count + 1}-{digest[..16]}@oob.local";
            images.Add((contentId, bytes));
            return tag.Value[..source.Index] + "cid:" + contentId + tag.Value[(source.Index + source.Length)..];
        });

        var builder = new BodyBuilder { TextBody = textBody, HtmlBody = html };
        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            var resource = (MimePart)builder.LinkedResources.Add($"report-chart-{index + 1}.png", image.Bytes, new ContentType("image", "png"));
            resource.ContentId = image.ContentId;
            resource.ContentDisposition = new ContentDisposition(ContentDisposition.Inline);
            resource.ContentTransferEncoding = ContentEncoding.Base64;
        }
        return builder.ToMessageBody();
    }

    private static void ValidatePng(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        ReadOnlySpan<byte> end = [0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130];
        if (bytes.Length < 45 || !bytes[..8].SequenceEqual(signature)
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4)) != 13
            || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8) || !bytes[^12..].SequenceEqual(end)) throw InvalidImage();
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));
        if (width is 0 or > 4096 || height is 0 or > 4096 || (ulong)width * height > 8000000) throw InvalidImage();
    }

    private static ArgumentException InvalidImage() => new("运行报表内嵌图片格式无效，仅支持有效且尺寸受限的PNG。", "inlineImages");
}
