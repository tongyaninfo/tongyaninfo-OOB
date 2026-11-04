import { describe, expect, it } from 'vitest';
import enUS from '@/locales/en-US/oob';
import zhCN from '@/locales/zh-CN/oob';
import {
  formatCapacity,
  formatEnergyNumber,
  formatReading,
  formatTime,
  metricDisplayName,
  readingCodeLabel,
} from './format';

describe('设备读数的展示边界', () => {
  it('保留合法零值并区分缺测', () => {
    expect(formatReading(0, 'W')).toBe('0 W');
    expect(formatReading(null, 'W')).toBe('—');
    expect(formatReading(undefined)).toBe('—');
  });
  it('UTC只在展示时转成上海时间', () => {
    expect(formatTime('2026-10-24T00:00:00Z')).toBe('2026-10-24 08:00:00');
    expect(formatTime(null)).toBe('—');
  });
  it('容量原始字符串不会先损失长整型精度', () => {
    expect(formatCapacity('1073741824')).toBe('1 GiB');
    expect(formatCapacity(null)).toBe('—');
  });
  it('所有计算值最多两位小数，能耗遵循同一规则且不补末尾零', () => {
    expect(formatEnergyNumber(1.25)).toBe('1.25');
    expect(formatEnergyNumber(0)).toBe('0');
    expect(formatReading(0.0000833333, 'kWh')).toBe('0 kWh');
    expect(formatEnergyNumber(1e-14)).toBe('0');
    expect(formatEnergyNumber(1.236)).toBe('1.24');
    expect(formatReading(12.5, 'W')).toBe('12.5 W');
    expect(formatReading(12, 'W')).toBe('12 W');
    expect(formatReading(3333.333333, 'RPM')).toBe('3,333.33 RPM');
    expect(formatReading(66.666667, '%')).toBe('66.67 %');
    expect(formatReading(126.126, 'W')).toBe('126.13 W');
    expect(formatReading(24.6662, '°C')).toBe('24.67 °C');
  });
  it('可用性和来源展示中英标签，未知机器码保留原值', () => {
    const chinese = (key: keyof typeof enUS) => zhCN[key];
    const english = (key: keyof typeof enUS) => enUS[key];
    expect(readingCodeLabel('availability', 'Available', chinese)).toBe('可用');
    expect(readingCodeLabel('availability', 'Unsupported', chinese)).toBe(
      '不支持',
    );
    expect(
      readingCodeLabel('availability', 'TemporarilyUnavailable', chinese),
    ).toBe('暂不可用');
    expect(
      readingCodeLabel('availability', 'TemporarilyUnavailable', english),
    ).toBe('Temporarily unavailable');
    expect(readingCodeLabel('source', 'Device', chinese)).toBe('设备原始读数');
    expect(readingCodeLabel('source', 'Derived', chinese)).toBe('换算值');
    expect(readingCodeLabel('source', 'Estimated', chinese)).toBe('估算值');
    expect(readingCodeLabel('source', 'Observed', chinese)).toBe('采样观测');
    expect(readingCodeLabel('source', 'Estimated', english)).toBe('Estimated');
    expect(readingCodeLabel('availability', 'VendorSpecific', chinese)).toBe(
      'VendorSpecific',
    );
    expect(readingCodeLabel('source', 'constructor', chinese)).toBe(
      'constructor',
    );
  });
  it.each([
    [
      'System Board Fan1APWM占空比',
      '转速百分比（%）',
      'System Board Fan1A · 转速百分比（%）',
    ],
    [
      'Fan1B PWM duty cycle (%)',
      'Speed percentage (%)',
      'Fan1B · Speed percentage (%)',
    ],
    ['Fan1 PWM 佔空比（%）', '轉速百分比（%）', 'Fan1 · 轉速百分比（%）'],
    ['PWM占空比', '转速百分比（%）', '转速百分比（%）'],
    ['Fan1 · 转速百分比（PWM/%）', '转速百分比（%）', 'Fan1 · 转速百分比（%）'],
    ['Fan 1转速百分比', '转速百分比（%）', 'Fan 1 · 转速百分比（%）'],
    [
      'Fan 1 · 设备转速百分比（%）',
      '转速百分比（%）',
      'Fan 1 · 转速百分比（%）',
    ],
    [
      'Fan 1 Speed percentage (%)',
      'Speed percentage (%)',
      'Fan 1 · Speed percentage (%)',
    ],
  ])('统一百分比名称保留物理风扇并清理旧尾缀：%s', (name, label, expected) => {
    const reading = { metricCode: 'fan.percent', name };
    expect(metricDisplayName(reading, () => label)).toBe(expected);
    expect(reading.name).toBe(name);
  });
  it('统一百分比命名不改变RPM等其他指标', () => {
    expect(
      metricDisplayName(
        { metricCode: 'fan.rpm', name: 'Fan1 RPM' },
        () => '转速百分比（%）',
      ),
    ).toBe('Fan1 RPM');
  });
});
