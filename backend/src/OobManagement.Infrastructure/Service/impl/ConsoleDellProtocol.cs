using System.Buffers.Binary;
using System.Text;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>已核对的 iDRAC8 AVSP/AVMP 帧格式；只在正式认证字段替换临时凭据。</summary>
public static class ConsoleDellProtocol
{
    /// <summary>拒绝诊断只包含已识别的包头、命令号和长度，不解析或保存正文。</summary>
    public static string RejectionCode(ReadOnlySpan<byte> packet)
    {
        var magic = packet.Length >= 4 && packet[..4].SequenceEqual("APCP"u8) ? "APCP"
            : packet.Length >= 4 && packet[..4].SequenceEqual("BEEF"u8) ? "BEEF"
            : packet.Length >= 4 && packet[..4].SequenceEqual("AVMP"u8) ? "AVMP" : "UNKNOWN";
        var type = magic == "UNKNOWN" ? -1 : Type(packet, magic);
        return "CONSOLE_PROTOCOL_REJECTED_" + magic + "_" + (type < 0 ? "NONE" : type.ToString("X4", System.Globalization.CultureInfo.InvariantCulture))
            + "_" + packet.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static int Type(ReadOnlySpan<byte> data, string header)
    {
        if (data.Length < (header == "BEEF" ? 8 : 12) || !data[..4].SequenceEqual(Encoding.ASCII.GetBytes(header))) return -1;
        return BinaryPrimitives.ReadUInt16BigEndian(data.Slice(header == "BEEF" ? 4 : 8, 2));
    }
    public static bool IsWholePacket(ReadOnlySpan<byte> data, string header) => Type(data, header) >= 0
        && (header == "BEEF" ? BinaryPrimitives.ReadUInt16BigEndian(data.Slice(6, 2)) : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4))) == data.Length;

    /// <summary>原厂关闭重连时的媒体握手固定为 53 字节，不复用 KVM 的 68 字节布局。</summary>
    public static bool IsMediaSessionOpen(ReadOnlySpan<byte> packet) => IsWholePacket(packet, "APCP")
        && Type(packet, "APCP") == 0x100 && packet.Length == 53
        && packet[10] == 0 && packet[11] == 0 && packet[12] == 2
        && packet[13] == 1 && packet[14] == 14 && packet[15] == 0
        && BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(16, 4)) is 0x604 or 0x605
        && packet[20] == 32 && packet[21..].IndexOfAnyExcept((byte)0) < 0;

    public static byte[] ReplaceLogin(ReadOnlySpan<byte> packet, string user, string password, bool media)
    {
        var u = Encoding.UTF8.GetBytes(user); var p = Encoding.UTF8.GetBytes(password);
        if (u.Length is < 1 or > 96 || p.Length is < 1 or > 96) throw new InvalidDataException("控制台临时凭据超出协议长度");
        var header = media ? "AVMP" : "BEEF";
        if (!IsWholePacket(packet, header)) throw new InvalidDataException("控制台认证帧无效");
        var type = Type(packet, header);
        if ((!media && type == 0x102) || (media && type == 0x100))
        {
            var expectedLength = media ? 153 : 217;
            if (packet.Length != expectedLength || media && p.Length > 32) throw new InvalidDataException("控制台认证版本不受支持");
            // 媒体仅接受原厂默认 RIP/端口/共享/抢占标志，防止给客户端构造的抢占请求加持真实身份。
            if (media && packet[141..153].IndexOfAnyExcept((byte)0) >= 0)
                throw new InvalidDataException("禁止媒体替代端口、共享和抢占登录");
            var result = packet.ToArray();
            var start = media ? 12 : 8;
            result[start] = (byte)u.Length;
            result.AsSpan(start + 1, 96).Clear(); u.CopyTo(result.AsSpan(start + 1));
            var passwordOffset = start + 97;
            if (!media) { result[passwordOffset++] = (byte)p.Length; }
            result.AsSpan(passwordOffset, media ? 32 : 96).Clear(); p.CopyTo(result.AsSpan(passwordOffset));
            if (!media && result[216] is not (0 or 1)) throw new InvalidDataException("禁止抢占、独占和隐身控制台登录");
            return result;
        }
        if (type != 0x104) throw new InvalidDataException("控制台认证版本不受支持");
        var baseLength = media ? 64 : 31;
        if (packet.Length < baseLength || !media && packet[30] is not (0 or 1)) throw new InvalidDataException("控制台认证元数据无效");
        if (media && packet[20..64].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("禁止媒体替代端口、共享和抢占登录");
        var output = new byte[baseLength + u.Length + p.Length]; packet[..baseLength].CopyTo(output);
        var fields = media ? 12 : 8;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(fields), (ushort)u.Length);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(fields + 2), (ushort)baseLength);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(fields + 4), (ushort)p.Length);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(fields + 6), (ushort)(baseLength + u.Length));
        u.CopyTo(output.AsSpan(baseLength)); p.CopyTo(output.AsSpan(baseLength + u.Length));
        if (media) BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), (uint)output.Length);
        else BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6), (ushort)output.Length);
        return output;
    }

    public static byte[] KeyboardRelease(ushort key)
    {
        byte[] result = [0x42, 0x45, 0x45, 0x46, 2, 0, 0, 16, 0, 1, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(10), key); return result;
    }
}
