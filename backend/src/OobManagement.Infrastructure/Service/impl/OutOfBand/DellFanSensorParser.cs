using System.Globalization;
using System.Text.RegularExpressions;

namespace OobManagement.Infrastructure.Service.impl.OutOfBand;

/// <summary>只解析 RACADM 风扇表的独立 PWM 列，不以转速或其它区域的百分比推算。</summary>
public static class DellFanSensorParser
{
    private static readonly Regex Columns = new("[ \\t]{2,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Section = new("^[ \\t]*Sensor[ \\t]+Type[ \\t]*:[ \\t]*(.*?)[ \\t]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex RpmColumn = new("^(?:[+-]?(?:[0-9]+(?:\\.[0-9]+)?|\\.[0-9]+)[ \\t]*RPM|N/?A)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly string[] ExpectedHeader = ["<Sensor Name>", "<Status>", "<Reading>", "<lc>", "<uc>", "<PWM %>"];

    /// <summary>名称移除空白后精确匹配；重复名称一律保留空值，避免误把不唯一的读数配到风扇。</summary>
    public static IReadOnlyDictionary<string, double?> Parse(string output)
    {
        var result = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output)) return result;
        var inFans = false;
        var hasPwmHeader = false;
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            var section = Section.Match(line);
            if (section.Success)
            {
                inFans = section.Groups[1].Value.Equals("FAN", StringComparison.OrdinalIgnoreCase);
                hasPwmHeader = false;
                continue;
            }
            if (!inFans || string.IsNullOrWhiteSpace(line)) continue;
            var columns = Columns.Split(line.Trim());
            if (columns[0].StartsWith('<'))
            {
                hasPwmHeader = columns.SequenceEqual(ExpectedHeader, StringComparer.OrdinalIgnoreCase);
                continue;
            }
            // 六列、RPM 单位和明确表头一起限定边界，防止列缺失后把 RPM/阈值当作 PWM。
            if (!hasPwmHeader || columns.Length != ExpectedHeader.Length
                || !RpmColumn.IsMatch(columns[2]) || !RpmColumn.IsMatch(columns[3]) || !RpmColumn.IsMatch(columns[4])) continue;
            var name = string.Concat(columns[0].Where(character => !char.IsWhiteSpace(character)));
            if (name.Length == 0) continue;
            var value = ParsePwm(columns[5]);
            if (!result.TryAdd(name, value)) result[name] = null;
        }
        return result;
    }

    private static double? ParsePwm(string value)
    {
        // 必须带百分号；NA、解析失败或越界均为缺测，0% 是真实有效读数。
        if (!value.EndsWith('%')) return null;
        return double.TryParse(value.AsSpan(0, value.Length - 1).Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number is >= 0 and <= 100 ? number : null;
    }
}
