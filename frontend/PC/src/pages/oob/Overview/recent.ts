import type {
  HistoryPointDto,
  HistorySeriesDto,
  ServerTelemetryDto,
} from '@/services/oob/models';

export const RECENT_WINDOW_MS = 15 * 60 * 1000;
export type ConnectionGap = { from: number; to?: number };
export { isSnapshotStale } from './metrics';

/** 传感器标识可包含竖线，组合键用 JSON 编码，不能靠分隔符拆解。 */
const seriesKey = (sensorKey: string, metricCode: string) =>
  JSON.stringify([sensorKey, metricCode]);

/** 数据库版本保留微秒，不能仅用 Date.parse 的毫秒精度把相邻版本当成重复。 */
export function compareVersions(left: string, right: string): number {
  const ticks = (value: string) => {
    const millis = Date.parse(value);
    if (!Number.isFinite(millis)) return 0n;
    const fraction = value.match(/\.(\d+)(?:Z|[+-]\d{2}:\d{2})$/)?.[1] || '';
    return (
      BigInt(millis) * 10000n + BigInt(fraction.slice(3, 7).padEnd(4, '0'))
    );
  };
  const a = ticks(left),
    b = ticks(right);
  return a === b ? 0 : a > b ? 1 : -1;
}

/** HTTP 首次快照与推送按持久化版本合并，延迟响应不能覆盖较新的推送。 */
export function mergeSnapshots(
  base: ServerTelemetryDto[],
  live: ServerTelemetryDto[],
) {
  const result = new Map<string, ServerTelemetryDto>();
  for (const snapshot of [...base, ...live]) {
    const previous = result.get(snapshot.server.id);
    if (
      !previous ||
      compareVersions(snapshot.publishedAt, previous.publishedAt) > 0
    )
      result.set(snapshot.server.id, snapshot);
  }
  return [...result.values()].sort(
    (a, b) =>
      a.server.name.localeCompare(b.server.name) ||
      a.server.id.localeCompare(b.server.id),
  );
}

/** 首次近期历史与后续推送均使用实际采样时间；明确失败/断流保留空点，绝不补取。 */
export function buildRecentHistory(
  initial: HistorySeriesDto[],
  events: ServerTelemetryDto[],
  gaps: ConnectionGap[],
  now: number,
  initialTo: number,
): HistorySeriesDto[] {
  const from = now - RECENT_WINDOW_MS;
  const rows = new Map<
    string,
    { series: HistorySeriesDto; points: Map<number, HistoryPointDto> }
  >();
  const ensure = (series: HistorySeriesDto) => {
    const key = seriesKey(series.sensorKey, series.metricCode);
    let row = rows.get(key);
    if (!row) {
      row = { series, points: new Map() };
      rows.set(key, row);
    }
    return row;
  };
  for (const series of initial) {
    const row = ensure(series);
    for (const point of series.points)
      if (Date.parse(point.time) >= from)
        row.points.set(Date.parse(point.time), point);
  }
  for (const event of events) {
    const sampled = Date.parse(event.telemetry.collectedAt || '');
    const time =
      event.server.connectionStatus === 'Online'
        ? sampled
        : Date.parse(event.publishedAt);
    if (!Number.isFinite(time) || time < Math.max(from, initialTo)) continue;
    for (const metric of event.telemetry.metrics) {
      const row = ensure({
        sensorKey: metric.sensorKey,
        metricCode: metric.metricCode,
        name: metric.name,
        unit: metric.unit,
        points: [],
      });
      const value =
        event.server.connectionStatus === 'Online' &&
        metric.availability === 'Available' &&
        metric.value != null &&
        Number.isFinite(metric.value)
          ? metric.value
          : null;
      row.points.set(time, {
        time: new Date(time).toISOString(),
        average: value,
        minimum: value,
        maximum: value,
        count: value == null ? 0 : 1,
      });
    }
  }
  const gapPoint = (time: number): HistoryPointDto => ({
    time: new Date(time).toISOString(),
    average: null,
    minimum: null,
    maximum: null,
    count: 0,
  });
  return [...rows.values()].map(({ series, points }) => {
    for (const gap of gaps) {
      if (gap.from > now || (gap.to ?? now) < from) continue;
      // 移除迟到消息在断流区间的读数，重连不会回填这一段。
      for (const time of points.keys())
        if (time >= gap.from && time <= (gap.to ?? now)) points.delete(time);
      const start = Math.max(from, gap.from);
      points.set(start, gapPoint(start));
      if (gap.to != null && gap.to <= now) points.set(gap.to, gapPoint(gap.to));
    }
    return {
      ...series,
      points: [...points.entries()]
        .filter(([time]) => time >= from && time <= now)
        .sort(([a], [b]) => a - b)
        .map(([, point]) => point),
    };
  });
}
