using OobManagement.Infrastructure.Service.impl.OutOfBand;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>固定文本模拟 RACADM 读数，测试不连接或控制真实设备。</summary>
public sealed class DellFanSensorParserTests
{
    private const string Header = "<Sensor Name>                   <Status>    <Reading>   <lc>        <uc>        <PWM %>";
    private static string Row(string name, string pwm, string rpm = "5280RPM") => $"{name}  Ok  {rpm}  600RPM  NA  {pwm}";
    private static string Fans(params string[] rows) => "Sensor Type : FAN\n" + Header + "\n" + string.Join('\n', rows);

    [Fact]
    public void Actual_fan_table_matches_A_B_names_independently_of_order()
    {
        var output = """
            Sensor Type : FAN
            <Sensor Name>                   <Status>    <Reading>   <lc>        <uc>        <PWM %>
            System Board Fan1B              Ok          4920RPM     600RPM      NA          24%
            System Board Fan1A              Ok          5280RPM     600RPM      NA          24%
            Sensor Type : VOLTAGE
            """;
        var result = DellFanSensorParser.Parse(output);
        Assert.Equal(2, result.Count);
        Assert.Equal(24d, result["SystemBoardFan1A"]);
        Assert.Equal(24d, result["systemboardfan1b"]);
    }

    [Theory]
    [InlineData("0%", 0d)]
    [InlineData("100%", 100d)]
    [InlineData("24.75%", 24.75d)]
    [InlineData(".5%", .5d)]
    [InlineData("24.5 %", 24.5d)]
    public void Valid_pwm_keeps_zero_and_decimal_values(string pwm, double expected)
    {
        Assert.Equal(expected, Assert.Single(DellFanSensorParser.Parse(Fans(Row("System Board Fan1A", pwm)))).Value);
    }

    [Theory]
    [InlineData("NA")]
    [InlineData("N/A")]
    [InlineData("24")]
    [InlineData("-1%")]
    [InlineData("100.01%")]
    [InlineData("NaN%")]
    [InlineData("Infinity%")]
    [InlineData("24,5%")]
    [InlineData("24RPM")]
    [InlineData("bad%")]
    public void Missing_malformed_or_out_of_range_pwm_is_null(string pwm)
    {
        Assert.Null(Assert.Single(DellFanSensorParser.Parse(Fans(Row("System Board Fan1A", pwm)))).Value);
    }

    [Fact]
    public void Other_sections_and_their_pwm_headers_are_ignored()
    {
        var output = "Sensor Type : TEMPERATURE\n" + Header + "\n" + Row("CPU Temp", "80%") + "\n"
            + Fans(Row("System Board Fan1A", "24%")) + "\nSensor Type : VOLTAGE\n" + Header + "\n" + Row("Board Voltage", "90%");
        var result = DellFanSensorParser.Parse(output);
        Assert.Equal("SystemBoardFan1A", Assert.Single(result).Key);
        Assert.Equal(24d, result["SystemBoardFan1A"]);
    }

    [Fact]
    public void Data_without_a_fan_section_and_exact_pwm_header_is_ignored()
    {
        Assert.Empty(DellFanSensorParser.Parse(Header + "\n" + Row("Fan1", "24%")));
        Assert.Empty(DellFanSensorParser.Parse("Sensor Type : FAN\n" + Row("Fan1", "24%")));
        Assert.Empty(DellFanSensorParser.Parse("Sensor Type : FAN\n<Sensor Name>  <Status>  <Reading>  <lc>  <uc>\n" + Row("Fan1", "24%")));
        Assert.DoesNotContain(DellFanSensorParser.Parse(Fans(Row("Fan1", "24%")) + "\nSensor Type : FAN\n" + Row("Fan2", "50%")),
            item => item.Key == "Fan2");
    }

    [Fact]
    public void Duplicate_names_are_ambiguous_even_when_values_are_equal()
    {
        var result = DellFanSensorParser.Parse(Fans(Row("System Board Fan1A", "24%"), Row("system board fan1a", "24%"), Row("System Board Fan1A", "30%")));
        Assert.Null(Assert.Single(result).Value);
    }

    [Fact]
    public void Malformed_columns_never_shift_rpm_or_threshold_values_into_pwm()
    {
        var result = DellFanSensorParser.Parse(Fans(
            "Fan1  Ok  5280RPM  600RPM  24%",
            "Fan2  Ok  5280RPM  600RPM  NA  24%  extra",
            "Fan3  Ok  5280RPM  24%  NA  45%",
            "Fan4  Ok  24%  600RPM  NA  45%",
            Row("Fan5", "24%", "5280 RPM")));
        Assert.Equal("Fan5", Assert.Single(result).Key);
        Assert.Equal(24d, result["Fan5"]);
    }

    [Fact]
    public void Empty_and_na_rpm_do_not_create_fabricated_values()
    {
        Assert.Empty(DellFanSensorParser.Parse(""));
        Assert.Empty(DellFanSensorParser.Parse("  \r\n\t"));
        Assert.Equal(24d, Assert.Single(DellFanSensorParser.Parse(Fans(Row("Fan1", "24%", "NA")))).Value);
    }
}
