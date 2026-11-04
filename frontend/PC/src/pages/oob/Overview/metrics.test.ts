import { describe, expect, it } from 'vitest';
import type {
  HistorySeriesDto,
  MetricReading,
  ServerTelemetryDto,
} from '@/services/oob/models';
import {
  availableValue,
  classifyMetric,
  isOverviewMetric,
  isSnapshotStale,
  sensorIdentity,
  trendPoints,
} from './metrics';

function metric(
  name: string,
  code = 'temperature.celsius',
  extra: Partial<MetricReading> = {},
): MetricReading {
  return {
    sensorKey: name,
    name,
    metricCode: code,
    value: 0,
    unit: '°C',
    availability: 'Available',
    source: 'Device',
    health: null,
    observedAt: '2026-10-24T00:00:00Z',
    deviceTime: null,
    ...extra,
  };
}

describe('总览真实硬件指标分类', () => {
  it('区分机箱进排气探头，保留多个排气探头且排除电源自身进气', () => {
    for (const name of [
      '01-Inlet Ambient',
      '27-Front Ambient',
      'System Board Inlet Temp',
      'SystemBoardInletTemp',
      'Inlet Temp',
    ])
      expect(classifyMetric(metric(name))).toBe('inlet');
    for (const name of [
      '05-Sys Exhaust',
      '06-Sys Exhaust',
      'System Board Exhaust Temp',
    ])
      expect(classifyMetric(metric(name))).toBe('exhaust');
    for (const name of [
      'P/S 1 Inlet',
      '11-PS 1 Inlet',
      '12-PS 2 Inlet',
      'PS 1 Inlet Ambient',
      'P/S Inlet Ambient',
      'PSU Inlet Temp',
      'Power Supply Inlet Temp',
      'Ambient',
      'CPU 1 Inlet',
    ])
      expect(classifyMetric(metric(name))).not.toBe('inlet');
  });
  it('识别两类控制器的CPU，并排除内存、供电与周边区域', () => {
    expect(classifyMetric(metric('02-CPU 1'))).toBe('cpu');
    expect(classifyMetric(metric('CPU1Temp'))).toBe('cpu');
    expect(classifyMetric(metric('System Board CPU2 Temp'))).toBe('cpu');
    for (const name of [
      'CPU 1 VR',
      'CPU 1 Memory Zone',
      'CPU 1 DIMM',
      'CPU 1 Power',
      'CPU 1 区域',
    ])
      expect(classifyMetric(metric(name))).toBeNull();
  });
  it('阵列卡实体与阵列区域分别分类，不用区域补造实体温度', () => {
    expect(classifyMetric(metric('31-HD Controller'))).toBe('array');
    expect(classifyMetric(metric('PERC H730 Temp'))).toBe('array');
    expect(classifyMetric(metric('32-HD Cntlr Zone'))).toBe('arrayZone');
    expect(classifyMetric(metric('RAID 周边区域'))).toBe('arrayZone');
    expect(classifyMetric(metric('PCI Riser'))).toBeNull();
  });
  it('PERC和RAID只按独立词匹配，SuperCAP温度不冒充阵列卡温度', () => {
    expect(classifyMetric(metric('42-SuperCAP Max'))).toBeNull();
    expect(classifyMetric(metric('SuperCAP Backup'))).toBeNull();
    expect(classifyMetric(metric('nonRAID sensor'))).toBeNull();
    expect(classifyMetric(metric('PERC H730P Mini'))).toBe('array');
    expect(classifyMetric(metric('RAID Controller'))).toBe('array');
  });
  it('仅整机当前功率画功率趋势，容量、峰值与电源槽读数不混入总功率', () => {
    expect(
      isOverviewMetric(
        metric('Power', 'power.watts', { sensorKey: 'system', unit: 'W' }),
      ),
    ).toBe(true);
    expect(
      isOverviewMetric(
        metric('Power', 'power.watts', { sensorKey: 'psu:1', unit: 'W' }),
      ),
    ).toBe(false);
    expect(
      isOverviewMetric(
        metric('Peak', 'power.peak.watts', { sensorKey: 'system', unit: 'W' }),
      ),
    ).toBe(false);
    expect(classifyMetric(metric('Fan1', 'fan.rpm', { unit: 'RPM' }))).toBe(
      'fan',
    );
    expect(classifyMetric(metric('Fan1', 'fan.percent', { unit: '%' }))).toBe(
      'fan',
    );
  });
  it('含竖线的原始传感器键按完整元组保留身份', () => {
    const rpm = metric('Fan1', 'fan.rpm', {
      sensorKey: 'fan:0x17||Fan.Embedded.1A',
      unit: 'RPM',
    });
    const percent = { ...rpm, metricCode: 'fan.percent', unit: '%' };
    expect(JSON.parse(sensorIdentity(rpm))).toEqual([
      'fan:0x17||Fan.Embedded.1A',
      'fan.rpm',
      'RPM',
    ]);
    expect(sensorIdentity(rpm)).not.toBe(sensorIdentity(percent));
  });
});

describe('总览读数质量和真实时间缺口', () => {
  it('零转速与负温度有效，不支持、缺测与非有限数字不能显示为零', () => {
    expect(
      availableValue(metric('CPU 1', 'temperature.celsius', { value: -2 })),
    ).toBe(-2);
    expect(availableValue(metric('Fan', 'fan.rpm', { value: 0 }))).toBe(0);
    for (const value of [null, Number.NaN, Number.POSITIVE_INFINITY])
      expect(
        availableValue(metric('CPU 1', 'temperature.celsius', { value })),
      ).toBeNull();
    expect(
      availableValue(
        metric('Fan', 'fan.rpm', { value: 0, availability: 'Unsupported' }),
      ),
    ).toBeNull();
  });
  it('推送停止后仅墙钟推进也能标记旧读数，边界沿用后端采样语义', () => {
    const snapshot = {
      server: { samplingIntervalSeconds: 30 },
      telemetry: { stale: false, collectedAt: '2026-10-24T00:00:00Z' },
    } as ServerTelemetryDto;
    const at = Date.parse('2026-10-24T00:00:00Z');
    expect(isSnapshotStale(snapshot, at + 90_000)).toBe(false);
    expect(isSnapshotStale(snapshot, at + 90_001)).toBe(true);
    expect(
      isSnapshotStale(
        {
          ...snapshot,
          telemetry: { ...snapshot.telemetry, collectedAt: null },
        },
        at,
      ),
    ).toBe(true);
    expect(
      isSnapshotStale(
        { ...snapshot, telemetry: { ...snapshot.telemetry, stale: true } },
        at,
      ),
    ).toBe(true);
  });
  it('同名不同传感器曲线不合并，空桶与明确断线保留null，零值保留', () => {
    const series: HistorySeriesDto = {
      name: 'Fan',
      sensorKey: 'fan:1|A',
      metricCode: 'fan.rpm',
      unit: 'RPM',
      points: [
        {
          time: '2026-10-24T00:00:00Z',
          average: 0,
          minimum: 0,
          maximum: 0,
          count: 1,
        },
        {
          time: '2026-10-24T00:00:30Z',
          average: 5000,
          minimum: 5000,
          maximum: 5000,
          count: 0,
        },
        {
          time: '2026-10-24T00:01:00Z',
          average: null,
          minimum: null,
          maximum: null,
          count: 0,
        },
      ],
    };
    const points = trendPoints([series, { ...series, sensorKey: 'fan:2|B' }]);
    expect(points.slice(0, 3).map((point) => point.value)).toEqual([
      0,
      null,
      null,
    ]);
    expect(new Set(points.map((point) => point.series)).size).toBe(2);
    expect(points[0].series).toContain('fan:1|A');
  });
});
