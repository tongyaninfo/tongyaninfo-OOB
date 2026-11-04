import { describe, expect, it } from 'vitest';
import type { HistorySeriesDto } from '@/services/oob/models';
import { telemetryFixture } from '../../../../tests/oobFixtures';
import {
  buildRecentHistory,
  compareVersions,
  mergeSnapshots,
  RECENT_WINDOW_MS,
} from './recent';

const at = Date.parse('2026-10-24T12:00:00Z');
const iso = (offset: number) => new Date(at + offset).toISOString();
function event(offset: number, value: number | null = 0, status = 'Online') {
  return telemetryFixture(
    iso(offset),
    [
      {
        sensorKey: 'fan:0x17||Fan.Embedded.1A',
        name: 'Fan 1A',
        metricCode: 'fan.rpm',
        unit: 'RPM',
        value,
        availability: 'Available',
        source: 'Device',
        health: null,
        observedAt: iso(offset),
        deviceTime: null,
      },
    ],
    status,
  );
}
const initial = (value: number | null = 5000): HistorySeriesDto[] => [
  {
    sensorKey: 'fan:0x17||Fan.Embedded.1A',
    name: 'Fan 1A',
    metricCode: 'fan.rpm',
    unit: 'RPM',
    points: [
      {
        time: iso(-30000),
        average: value,
        minimum: value,
        maximum: value,
        count: value === null ? 0 : 1,
      },
    ],
  },
];

describe('实时快照顺序与近期窗口', () => {
  it('版本比较保留数据库微秒，不因同一毫秒而遗漏更新', () => {
    expect(
      compareVersions(
        '2026-10-24T12:00:00.123002+00:00',
        '2026-10-24T12:00:00.123001+00:00',
      ),
    ).toBe(1);
    expect(
      compareVersions(
        '2026-10-24T12:00:00Z',
        '2026-10-24T12:00:00.000000+00:00',
      ),
    ).toBe(0);
  });
  it('延迟HTTP快照、重复和旧推送均不能覆盖新版本，long标识保持字符串', () => {
    const newer = event(2000, 5200);
    const old = event(0, 5000);
    expect(mergeSnapshots([old], [newer, old, newer])).toEqual([newer]);
    expect(mergeSnapshots([newer], [old])[0].server.id).toBe(
      '9007199254740993',
    );
  });
  it('保留历史和真实0转速，不拆分厂商key，也不重复追加已在首次查询范围内的事件', () => {
    const result = buildRecentHistory(
      initial(),
      [event(-5000, 5100), event(1000, 0), event(1000, 0)],
      [],
      at + 2000,
      at,
    );
    expect(result).toHaveLength(1);
    expect(result[0].sensorKey).toBe('fan:0x17||Fan.Embedded.1A');
    expect(result[0].points.map((point) => point.average)).toEqual([5000, 0]);
  });
  it('断线期间的迟到事件被排除，重连后只保留新采样并插入空点', () => {
    const result = buildRecentHistory(
      initial(),
      [event(1000, 5100), event(5000, 5200), event(12000, 5300)],
      [{ from: at + 2000, to: at + 10000 }],
      at + 15000,
      at,
    );
    expect(result[0].points.map((point) => point.average)).toEqual([
      5000,
      5100,
      null,
      null,
      5300,
    ]);
    expect(result[0].points.some((point) => point.average === 5200)).toBe(
      false,
    );
  });
  it('采集失败显示空点而不将保留快照作为新读数重复绘图', () => {
    const failed = event(5000, 5000, 'Offline');
    failed.telemetry.collectedAt = iso(-30000);
    expect(
      buildRecentHistory(initial(), [failed], [], at + 10000, at)[0].points.at(
        -1,
      )?.average,
    ).toBeNull();
  });
  it('滑出15分钟的点移除，未结束断线区间不产生后续虚假读数', () => {
    const result = buildRecentHistory(
      initial(),
      [event(1000, 5100)],
      [{ from: at, to: undefined }],
      at + RECENT_WINDOW_MS,
      at,
    );
    expect(result[0].points.map((point) => point.average)).toEqual([null]);
    expect(result[0].points[0].count).toBe(0);
  });
});

it('实际相邻采样跨过30秒边界时不补造空桶，明确失败区间才保留断点', () => {
  const source = initial(120);
  source[0].points = [
    { time: iso(-34117), average: 120, minimum: 120, maximum: 120, count: 1 },
    { time: iso(649), average: 123, minimum: 123, maximum: 123, count: 1 },
  ];
  const good = buildRecentHistory(source, [], [], at + 1000, at + 1000);
  expect(good[0].points).toHaveLength(2);
  expect(good[0].points.every((point) => point.average !== null)).toBe(true);
  const failed = buildRecentHistory(
    source,
    [],
    [{ from: at - 30000, to: at - 20000 }],
    at + 1000,
    at + 1000,
  );
  expect(failed[0].points.map((point) => point.average)).toEqual([
    120,
    null,
    null,
    123,
  ]);
});
