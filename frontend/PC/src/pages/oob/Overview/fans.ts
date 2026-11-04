import type {
  HistoryPointDto,
  HistorySeriesDto,
  MetricReading,
} from '@/services/oob/models';
import { availableValue } from './metrics';

export type FanDetailReading = {
  sensorKey: string;
  name: string;
  rpm?: MetricReading;
  speedPercent?: MetricReading;
};

export type FanSummary = {
  fans: FanDetailReading[];
  averageRpm: number | null;
  averageSpeedPercent: number | null;
  rpmCount: number;
  speedPercentCount: number;
  hasSpeedPercent: boolean;
  totalFans: number;
};

/** 缺测不进入分母，先除后加也避免多个极大有限值求和后溢出。 */
function mean(values: number[]): number | null {
  return values.length
    ? values.reduce((sum, value) => sum + value / values.length, 0)
    : null;
}

function latestReading(
  previous: MetricReading | undefined,
  incoming: MetricReading,
): MetricReading {
  if (!previous) return incoming;
  const previousAt = Date.parse(previous.observedAt);
  const incomingAt = Date.parse(incoming.observedAt);
  return Number.isFinite(previousAt) && incomingAt < previousAt
    ? previous
    : incoming;
}

/** 后端已统一百分比指标；按完整资源键分组，每个物理风扇只计一次。 */
export function summarizeFans(metrics: MetricReading[]): FanSummary {
  const grouped = new Map<string, FanDetailReading>();
  for (const metric of metrics) {
    if (!['fan.rpm', 'fan.percent'].includes(metric.metricCode)) continue;
    let fan = grouped.get(metric.sensorKey);
    if (!fan) {
      fan = { sensorKey: metric.sensorKey, name: metric.name };
      grouped.set(metric.sensorKey, fan);
    }
    if (metric.metricCode === 'fan.rpm')
      fan.rpm = latestReading(fan.rpm, metric);
    else if (metric.metricCode === 'fan.percent')
      fan.speedPercent = latestReading(fan.speedPercent, metric);
  }
  const fans = [...grouped.values()]
    .map((fan) => ({
      ...fan,
      name: fan.rpm?.name || fan.speedPercent?.name || fan.name,
    }))
    .sort(
      (left, right) =>
        left.name.localeCompare(right.name, undefined, { numeric: true }) ||
        left.sensorKey.localeCompare(right.sensorKey),
    );
  const values = (key: 'rpm' | 'speedPercent', unit: string) =>
    fans.flatMap((fan) => {
      const metric = fan[key];
      const value = metric?.unit === unit ? availableValue(metric) : null;
      return value === null ? [] : [value];
    });
  const rpms = values('rpm', 'RPM');
  const percentages = values('speedPercent', '%');
  return {
    fans,
    averageRpm: mean(rpms),
    averageSpeedPercent: mean(percentages),
    rpmCount: rpms.length,
    speedPercentCount: percentages.length,
    hasSpeedPercent: fans.some(
      (fan) =>
        fan.speedPercent?.unit === '%' &&
        !['Unsupported', 'NotInstalled'].includes(
          fan.speedPercent.availability,
        ),
    ),
    totalFans: fans.length,
  };
}

/** 同一时刻只对实际存在且有效的风扇读数求均值，不携带前值、不跨缺口插值。 */
export function averageFanHistory(
  series: HistorySeriesDto[],
  labels: { rpm: string; speedPercent: string } = {
    rpm: 'Average RPM',
    speedPercent: 'Average speed percentage',
  },
): HistorySeriesDto[] {
  const units = [
    { code: 'fan.rpm', unit: 'RPM', name: labels.rpm },
    { code: 'fan.percent', unit: '%', name: labels.speedPercent },
  ];
  return units.flatMap(({ code, unit, name }) => {
    const matching = series.filter(
      (item) => item.metricCode === code && item.unit === unit,
    );
    if (!matching.length) return [];
    const moments = new Map<number, Map<string, number | null>>();
    for (const item of matching) {
      for (const point of item.points) {
        const time = Date.parse(point.time);
        if (!Number.isFinite(time)) continue;
        let readings = moments.get(time);
        if (!readings) {
          readings = new Map();
          moments.set(time, readings);
        }
        readings.set(
          item.sensorKey,
          point.count > 0 &&
            point.average !== null &&
            Number.isFinite(point.average)
            ? point.average
            : null,
        );
      }
    }
    const points: HistoryPointDto[] = [...moments.entries()]
      .sort(([left], [right]) => left - right)
      .map(([time, readings]) => {
        const values = [...readings.values()].filter(
          (value): value is number => value !== null,
        );
        // 每个风扇等权，历史桶内样本数量不同不会让某个风扇获得额外权重。
        return {
          time: new Date(time).toISOString(),
          average: mean(values),
          minimum: values.length ? Math.min(...values) : null,
          maximum: values.length ? Math.max(...values) : null,
          count: values.length,
        };
      });
    return [
      {
        sensorKey: 'aggregate:fan-average',
        metricCode: code,
        unit,
        name,
        points,
      },
    ];
  });
}
