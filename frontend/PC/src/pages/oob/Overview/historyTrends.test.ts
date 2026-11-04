import { describe, expect, it } from 'vitest';
import type { HistorySeriesDto } from '@/services/oob/models';
import {
  customHistoryWindow,
  historyMetricCodes,
  historyTick,
  historyTrendGroups,
  historyWindow,
} from './historyTrends';
import { sensorIdentity } from './metrics';

const names = {
  temperatures: 'Temperatures',
  power: 'Power',
  rpm: 'Mean RPM',
  speedPercent: 'Mean speed percentage',
  cpuSlot: (number: string) => `CPU ${number}`,
};
const sample = (
  sensorKey: string,
  name: string,
  metricCode: string,
  unit: string,
  value: number | null,
): HistorySeriesDto => ({
  sensorKey,
  name,
  metricCode,
  unit,
  points: [
    {
      time: '2026-10-25T00:00:00Z',
      average: value,
      minimum: value,
      maximum: value,
      count: value === null ? 0 : 1,
    },
  ],
});

describe('本地历史弹窗的范围和真实指标分组', () => {
  it('单机阵列卡历史仅显示明确控制器温度，缺少卡温时不拿CPU或区域替代', () => {
    const rows = [
      sample('cpu1', 'CPU1 Temp', 'temperature.celsius', '°C', 40),
      sample('array1', '24-HD Controller', 'temperature.celsius', '°C', 54),
      sample('array2', '31-HD Controller', 'temperature.celsius', '°C', 0),
      sample('zone', '34-HD Cntlr Zone', 'temperature.celsius', '°C', 31),
      sample('disk', 'RAID Disk Temp', 'temperature.celsius', '°C', 50),
      sample('cap', 'SuperCAP Max', 'temperature.celsius', '°C', 25),
    ];
    const group = historyTrendGroups('array', rows, null, names)[0];
    expect(group.series.map((row) => row.sensorKey)).toEqual([
      'array1',
      'array2',
    ]);
    expect(group.series.map((row) => row.name)).toEqual([
      '24-HD Controller',
      '31-HD Controller',
    ]);
    expect(group.series[1].points[0].average).toBe(0);
    expect(
      historyTrendGroups('array', [rows[0], ...rows.slice(3)], null, names)[0]
        .series,
    ).toEqual([]);
    expect(historyMetricCodes.array).toEqual(['temperature.celsius']);
  });

  it('单机风扇保留每个同名探头和零值/缺口，只展示当前单位的真实序列', () => {
    const rows = [
      sample('fan1', 'Fan', 'fan.rpm', 'RPM', 0),
      sample('fan2', 'Fan', 'fan.rpm', 'RPM', null),
      sample('fan1', 'Fan', 'fan.percent', '%', 20),
      sample('fan2', 'Fan', 'fan.percent', '%', 35),
      sample('bad', 'Wrong unit', 'fan.rpm', '%', 90),
    ];
    const rpm = historyTrendGroups('fanRpmDetails', rows, null, names)[0];
    const percent = historyTrendGroups(
      'fanPercentDetails',
      rows,
      null,
      names,
    )[0];
    expect(rpm.unit).toBe('RPM');
    expect(
      rpm.series.map((row) => [row.sensorKey, row.points[0].average]),
    ).toEqual([
      ['fan1', 0],
      ['fan2', null],
    ]);
    expect(percent.unit).toBe('%');
    expect(
      percent.series.map((row) => [row.sensorKey, row.points[0].average]),
    ).toEqual([
      ['fan1', 20],
      ['fan2', 35],
    ]);
    expect(historyMetricCodes.fanRpmDetails).toEqual(['fan.rpm']);
    expect(historyMetricCodes.fanPercentDetails).toEqual(['fan.percent']);
  });

  it.each([
    '2026-11-01T05:16:57.929Z',
    '2026-11-01T10:16:57.929Z',
    '2026-03-08T05:16:57.929Z',
    '2026-03-08T11:16:57.929Z',
  ])('浏览器真实夏令时切换前后，预设结束瞬间和24小时/7天跨度保持精确：%s', (stamp) => {
    const now = Date.parse(stamp);
    for (const [period, days] of [
      ['24h', 1],
      ['7d', 7],
    ] as const) {
      const range = historyWindow(period, now);
      expect(range.to).toBe(stamp);
      expect(Date.parse(range.to) - Date.parse(range.from)).toBe(
        days * 86400000,
      );
    }
  });
  it.each([
    ['2026-11-01T05:16:57.929Z', '2026-10-01T05:16:57.929Z'],
    ['2026-03-08T05:16:57.929Z', '2026-02-08T05:16:57.929Z'],
    ['2026-03-31T15:34:56.789Z', '2026-02-28T15:34:56.789Z'],
    ['2026-03-31T20:34:56.789Z', '2026-02-28T20:34:56.789Z'],
    ['2024-03-31T04:34:56.789Z', '2024-02-29T04:34:56.789Z'],
    ['2026-01-31T04:34:56.789Z', '2025-12-31T04:34:56.789Z'],
  ])('过去一月按上海日历处理月底、跨年和毫秒，不受浏览器DST影响：%s', (to, from) => {
    expect(historyWindow('month', Date.parse(to))).toEqual({
      period: 'month',
      from,
      to,
      bucketSeconds: 3600,
    });
  });
  it.each([
    ['2026-11-01 01:30:00', '2026-10-31T17:30:00.000Z'],
    ['2026-11-01 02:30:00', '2026-10-31T18:30:00.000Z'],
    ['2026-11-01 13:16:57', '2026-11-01T05:16:57.000Z'],
    ['2026-03-08 01:30:00', '2026-03-07T17:30:00.000Z'],
    ['2026-03-08 02:30:00', '2026-03-07T18:30:00.000Z'],
    ['2026-03-08 13:16:57', '2026-03-08T05:16:57.000Z'],
  ])('上海自选时间不使用浏览器缺失或重复的夏令时小时：%s', (from, expected) => {
    const range = customHistoryWindow(from, `${from.slice(0, 10)} 23:59:59`);
    expect(range?.from).toBe(expected);
    expect(range?.to).toBe(`${from.slice(0, 10)}T15:59:59.000Z`);
  });
  it('卡片CPU历史与机箱进排气历史严格分离，每个排气探头独立且仅进气用虚线', () => {
    const rows = [
      sample('cpu1', 'CPU1 Temp', 'temperature.celsius', '°C', 40),
      sample('cpu2', 'CPU2 Temp', 'temperature.celsius', '°C', 41),
      sample('array', '31-HD Controller', 'temperature.celsius', '°C', 53),
      sample('inlet', '01-Inlet Ambient', 'temperature.celsius', '°C', 20),
      sample('exhaust1', '02-Sys Exhaust', 'temperature.celsius', '°C', 32),
      sample('exhaust2', '03-Sys Exhaust', 'temperature.celsius', '°C', 34),
      sample('psu', 'P/S 1 Inlet', 'temperature.celsius', '°C', 28),
    ];
    const cpu = historyTrendGroups('cpu', rows, null, names)[0];
    expect(cpu.series.map((row) => row.sensorKey)).toEqual(['cpu1', 'cpu2']);
    expect(cpu.series.map((row) => row.name)).toEqual(['CPU 1', 'CPU 2']);
    const airflow = historyTrendGroups('airflow', rows, null, names)[0];
    expect(airflow.series.map((row) => row.sensorKey)).toEqual([
      'inlet',
      'exhaust1',
      'exhaust2',
    ]);
    expect(airflow.dashedSeries).toEqual([sensorIdentity(rows[3])]);
    expect(historyMetricCodes.cpu).toEqual(['temperature.celsius']);
    expect(historyMetricCodes.airflow).toEqual(['temperature.celsius']);
    expect(
      historyTrendGroups('temperatures', rows, null, names)[0].series[2].name,
    ).toBe('31-HD Controller');
  });

  it('自选上海墙钟精确转换UTC，并按跨度控制实际历史桶数', () => {
    expect(
      customHistoryWindow('2026-10-30 08:10:30', '2026-10-31 08:10:30'),
    ).toEqual({
      period: 'custom',
      from: '2026-10-30T00:10:30.000Z',
      to: '2026-10-31T00:10:30.000Z',
      bucketSeconds: 300,
    });
    expect(
      customHistoryWindow('2026-10-24 08:00:00', '2026-10-31 08:00:00')
        ?.bucketSeconds,
    ).toBe(900);
    expect(
      customHistoryWindow('2026-09-30 08:00:00', '2026-10-31 08:00:00')
        ?.bucketSeconds,
    ).toBe(3600);
    const longest = customHistoryWindow(
      '2025-10-30 08:00:00',
      '2026-10-31 08:00:00',
    );
    expect(longest?.bucketSeconds).toBe(31680);
  });
  it.each([
    ['', '2026-10-31 08:00:00'],
    ['2026-10-30 08:00:00', ''],
    ['2026-10-31 08:00:00', '2026-10-31 08:00:00'],
    ['2026-10-31 09:00:00', '2026-10-31 08:00:00'],
    ['2025-10-30 08:00:00', '2026-10-31 08:00:01'],
    ['2026-02-30 08:00:00', '2026-10-31 08:00:00'],
    ['2026-10-31 25:00:00', '2026-11-01 08:00:00'],
    ['invalid', '2026-11-01 08:00:00'],
  ])('无效、未选全、倒序、同刻或超366天不能查询：%s / %s', (from, to) => {
    expect(customHistoryWindow(from, to)).toBeNull();
  });
  it('风扇历史只查询RPM与统一百分比', () => {
    expect(historyMetricCodes.fans).toEqual(['fan.rpm', 'fan.percent']);
  });
  it('24小时、7天是固定跨度，过去一个月按上海自然月截断月底', () => {
    const now = Date.parse('2026-03-31T04:34:56Z');
    const day = historyWindow('24h', now);
    const week = historyWindow('7d', now);
    const month = historyWindow('month', now);
    expect(Date.parse(day.to) - Date.parse(day.from)).toBe(86400000);
    expect(Date.parse(week.to) - Date.parse(week.from)).toBe(7 * 86400000);
    expect(month.from).toBe('2026-02-28T04:34:56.000Z');
    expect(month.to).toBe('2026-03-31T04:34:56.000Z');
    expect([
      day.bucketSeconds,
      week.bucketSeconds,
      month.bucketSeconds,
    ]).toEqual([300, 900, 3600]);
    expect(historyTick(Date.parse('2026-10-24T16:00:00Z'))).toContain('10-25');
  });
  it('仅查询统一百分比与RPM，各风扇等权且不重复生成百分比曲线', () => {
    expect(historyMetricCodes.power).toEqual(['power.watts']);
    expect(historyMetricCodes.temperatures).toEqual(['temperature.celsius']);
    expect(historyMetricCodes.fans).toEqual(['fan.rpm', 'fan.percent']);
    const groups = historyTrendGroups(
      'fans',
      [
        sample('fan1', 'Fan 1', 'fan.rpm', 'RPM', 3000),
        sample('fan2', 'Fan 2', 'fan.rpm', 'RPM', 5000),
        sample('fan1', 'Fan 1', 'fan.percent', '%', 30),
        sample('fan2', 'Fan 2', 'fan.percent', '%', 60),
      ],
      null,
      names,
    );
    expect(
      groups.map((group) => [
        group.key,
        group.unit,
        group.series[0].points[0].average,
      ]),
    ).toEqual([
      ['fan.rpm', 'RPM', 4000],
      ['fan.percent', '%', 45],
    ]);
    expect(groups.map((group) => group.title)).toEqual([
      'Mean RPM',
      'Mean speed percentage',
    ]);
  });
  it('HP百分比保留真实零值和缺口，无样本的百分比不添加空图', () => {
    const percent = sample('fan1', 'Fan 1', 'fan.percent', '%', 0);
    percent.points.push({
      time: '2026-10-25T00:05:00Z',
      average: null,
      minimum: null,
      maximum: null,
      count: 0,
    });
    const groups = historyTrendGroups('fans', [percent], null, names);
    expect(groups).toHaveLength(1);
    expect(groups[0].series[0].points.map((point) => point.average)).toEqual([
      0,
      null,
    ]);
    expect(
      historyTrendGroups(
        'fans',
        [sample('fan1', 'Fan 1', 'fan.percent', '%', null)],
        null,
        names,
      ),
    ).toEqual([]);
  });
  it('CPU独立保留，阵列区域/入口温度不冒充芯片温度，零功率与缺测不互换', () => {
    const rows = [
      sample('cpu1', 'CPU1 Temp', 'temperature.celsius', '°C', 40),
      sample('cpu2', 'CPU2 Temp', 'temperature.celsius', '°C', 41),
      sample('array', 'PERC H730P Temp', 'temperature.celsius', '°C', 52),
      sample('zone', 'HD Controller Zone', 'temperature.celsius', '°C', 45),
      sample('inlet', 'Inlet Temp', 'temperature.celsius', '°C', 20),
    ];
    const group = historyTrendGroups('temperatures', rows, null, names)[0];
    expect(group.series.map((row) => row.sensorKey)).toEqual([
      'cpu1',
      'cpu2',
      'array',
    ]);
    expect(rows[0].name).toBe('CPU1 Temp');
    const power = historyTrendGroups(
      'power',
      [
        sample('system', 'System', 'power.watts', 'W', 0),
        sample('psu', 'PSU1', 'psu.output.watts', 'W', 10),
      ],
      null,
      names,
    )[0];
    expect(power.series).toHaveLength(1);
    expect(power.series[0].points[0].average).toBe(0);
    expect(
      historyTrendGroups(
        'power',
        [sample('system', 'System', 'power.watts', 'W', null)],
        null,
        names,
      )[0].series[0].points[0].average,
    ).toBeNull();
  });
});
