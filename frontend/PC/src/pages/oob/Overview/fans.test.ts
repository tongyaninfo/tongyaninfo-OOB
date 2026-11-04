import { describe, expect, it } from 'vitest';
import type { HistorySeriesDto, MetricReading } from '@/services/oob/models';
import { averageFanHistory, summarizeFans } from './fans';

const at = '2026-10-25T00:00:00Z';
const later = '2026-10-25T00:00:30Z';
function reading(
  sensorKey: string,
  value: number | null,
  metricCode = 'fan.rpm',
  extra: Partial<MetricReading> = {},
): MetricReading {
  return {
    sensorKey,
    name: sensorKey,
    value,
    metricCode,
    unit: metricCode === 'fan.rpm' ? 'RPM' : '%',
    availability: 'Available',
    source: 'Device',
    observedAt: at,
    deviceTime: null,
    health: null,
    ...extra,
  };
}
function history(
  sensorKey: string,
  values: Array<[string, number | null, number?]>,
  metricCode = 'fan.rpm',
): HistorySeriesDto {
  return {
    sensorKey,
    name: sensorKey,
    metricCode,
    unit: metricCode === 'fan.rpm' ? 'RPM' : '%',
    points: values.map(([time, average, count]) => ({
      time,
      average,
      minimum: average,
      maximum: average,
      count: count ?? (average === null ? 0 : 1),
    })),
  };
}

describe('风扇当前均值和详情分组', () => {
  it('HP与Dell统一为一个百分比字段、均值和有效数量', () => {
    const result = summarizeFans([
      reading('hp:fan:1', 19, 'fan.percent'),
      reading('dell:fan:1', 25, 'fan.percent'),
    ]);
    expect(result).toMatchObject({
      totalFans: 2,
      averageSpeedPercent: 22,
      speedPercentCount: 2,
    });
    expect(result.fans[0]).toHaveProperty('speedPercent');
    expect(result).not.toHaveProperty('averagePwm');
    expect(result.fans[0]).not.toHaveProperty('pwm');
  });
  it('按完整资源键合并两种指标，RPM与百分比各自平均，0是有效读数', () => {
    const key = 'fan:0x17||Fan.Embedded.1A';
    const summary = summarizeFans([
      reading(key, 0),
      reading(key, 40, 'fan.percent'),
      reading('fan:2', 6000),
      reading('fan:2', null, 'fan.percent', { availability: 'Unsupported' }),
      reading('fan:3', null),
      reading('fan:3', 60, 'fan.percent'),
    ]);
    expect(summary.totalFans).toBe(3);
    expect(summary.fans.find((fan) => fan.sensorKey === key)).toMatchObject({
      sensorKey: key,
      rpm: { value: 0 },
      speedPercent: { value: 40 },
    });
    expect(summary.averageRpm).toBe(3000);
    expect(summary.rpmCount).toBe(2);
    expect(summary.averageSpeedPercent).toBe(50);
    expect(summary.speedPercentCount).toBe(2);
  });

  it('不支持、无效数字和错配单位不算有效分母，也不伪装零值', () => {
    const summary = summarizeFans([
      reading('fan:1', 1000, 'fan.rpm', { availability: 'Unsupported' }),
      reading('fan:2', Number.NaN),
      reading('fan:3', Number.POSITIVE_INFINITY),
      reading('fan:4', 1234, 'fan.rpm', { unit: '%' }),
      reading('cpu', 40, 'temperature.celsius'),
    ]);
    expect(summary.totalFans).toBe(4);
    expect(summary.averageRpm).toBeNull();
    expect(summary.averageSpeedPercent).toBeNull();
    expect(summary.rpmCount).toBe(0);
    expect(summary.speedPercentCount).toBe(0);
  });

  it('同风扇重复指标只保留最后观测，不增加数量或平均权重', () => {
    const summary = summarizeFans([
      reading('fan:1', 2000, 'fan.rpm', { observedAt: later }),
      reading('fan:1', 1000),
      reading('fan:2', 4000),
    ]);
    expect(summary.totalFans).toBe(2);
    expect(summary.averageRpm).toBe(3000);
  });

  it('空集合或全部缺测保持空均值，只有百分比时不制造转速', () => {
    expect(summarizeFans([])).toMatchObject({
      totalFans: 0,
      averageRpm: null,
      averageSpeedPercent: null,
    });
    expect(summarizeFans([reading('fan:1', 39, 'fan.percent')])).toMatchObject({
      averageRpm: null,
      averageSpeedPercent: 39,
      rpmCount: 0,
      speedPercentCount: 1,
      hasSpeedPercent: true,
    });
  });

  it('百分比临时缺测保留能力标记，不支持或未安装不计有效分母', () => {
    expect(
      summarizeFans([
        reading('fan:1', null, 'fan.percent', {
          availability: 'TemporarilyUnavailable',
        }),
      ]),
    ).toMatchObject({
      hasSpeedPercent: true,
      averageSpeedPercent: null,
      speedPercentCount: 0,
    });
    expect(
      summarizeFans([
        reading('fan:1', null, 'fan.percent', { availability: 'Unsupported' }),
        reading('fan:2', null, 'fan.percent', { availability: 'NotInstalled' }),
      ]),
    ).toMatchObject({
      hasSpeedPercent: false,
      averageSpeedPercent: null,
      speedPercentCount: 0,
    });
  });
});

describe('风扇近期均值趋势', () => {
  it('同桶各风扇等权平均，RPM和百分比分开，所有缺测保留null', () => {
    const result = averageFanHistory(
      [
        history('fan:1', [
          [at, 1000, 10],
          [later, null],
        ]),
        history('fan:2', [
          [at, 3000, 1],
          [later, null],
        ]),
        history(
          'fan:1',
          [
            [at, 40],
            [later, null],
          ],
          'fan.percent',
        ),
        history(
          'fan:2',
          [
            [at, 60],
            [later, null],
          ],
          'fan.percent',
        ),
      ],
      { rpm: '平均RPM', speedPercent: '平均转速百分比（%）' },
    );
    expect(result).toHaveLength(2);
    expect(result[0]).toMatchObject({ name: '平均RPM', unit: 'RPM' });
    expect(result[0].points).toEqual([
      {
        time: new Date(at).toISOString(),
        average: 2000,
        minimum: 1000,
        maximum: 3000,
        count: 2,
      },
      {
        time: new Date(later).toISOString(),
        average: null,
        minimum: null,
        maximum: null,
        count: 0,
      },
    ]);
    expect(result[1]).toMatchObject({ name: '平均转速百分比（%）', unit: '%' });
    expect(result[1].points[0].average).toBe(50);
    expect(result[1].points[1].average).toBeNull();
  });

  it('不同观测时刻不前填或插值，真实零值保留', () => {
    const result = averageFanHistory([
      history('fan:1', [[at, 0]]),
      history('fan:2', [[later, 3000]]),
    ]);
    expect(
      result[0].points.map((point) => [point.average, point.count]),
    ).toEqual([
      [0, 1],
      [3000, 1],
    ]);
  });

  it('重复风扇键不重复计权，含竖线键不被拆分，坏时间和无有效样本不计值', () => {
    const key = 'fan:0x17||Fan.Embedded.1A';
    const result = averageFanHistory([
      history(key, [
        [at, 1000],
        ['invalid', 6000],
        [later, 6000, 0],
      ]),
      history(key, [[at, 3000]]),
      history('fan:other', [[at, Number.NaN]]),
    ]);
    expect(
      result[0].points.map((point) => [point.average, point.count]),
    ).toEqual([
      [3000, 1],
      [null, 0],
    ]);
  });

  it('不对非风扇或错配单位的序列生成均值', () => {
    const wrong = history('fan:1', [[at, 1000]]);
    wrong.unit = '%';
    expect(
      averageFanHistory([
        wrong,
        history('cpu', [[at, 40]], 'temperature.celsius'),
      ]),
    ).toEqual([]);
  });
});

describe('真实S1读数组合与统一百分比', () => {
  it('S1五个5640、第四个5520，详情与均值同轮且平均5620', () => {
    const metrics = [5640, 5640, 5640, 5520, 5640, 5640].map((rpm, index) =>
      reading(`fan:0x17||Fan.Embedded.${index + 1}`, rpm),
    );
    const result = summarizeFans(metrics);
    expect(result.totalFans).toBe(6);
    expect(result.rpmCount).toBe(6);
    expect(result.averageRpm).toBe(5620);
    expect(
      result.fans.find((fan) => fan.sensorKey.endsWith('.4'))?.rpm?.value,
    ).toBe(5520);
    expect(new Set(result.fans.map((fan) => fan.rpm?.observedAt))).toEqual(
      new Set([at]),
    );
  });
  it('后端统一百分比仅计一次，废弃指标不参与前端重复统计', () => {
    const result = summarizeFans([
      reading('fan:1', 5000),
      reading('fan:1', 50, 'fan.percent'),
      reading('fan:1', 50, 'fan.pwm.percent'),
      reading('fan:2', 70, 'fan.percent'),
    ]);
    expect(result.averageRpm).toBe(5000);
    expect(result.averageSpeedPercent).toBe(60);
    expect(result.speedPercentCount).toBe(2);
    expect(result.totalFans).toBe(2);
  });
});
