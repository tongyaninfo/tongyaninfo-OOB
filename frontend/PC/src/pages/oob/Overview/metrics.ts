import type {
  HistorySeriesDto,
  MetricReading,
  ServerTelemetryDto,
} from '@/services/oob/models';

export type MonitorGroup =
  | 'cpu'
  | 'array'
  | 'arrayZone'
  | 'inlet'
  | 'exhaust'
  | 'fan'
  | 'power';
type SensorDescriptor = Pick<
  MetricReading,
  'sensorKey' | 'metricCode' | 'name' | 'unit'
>;

/** 设备标识可能包含竖线，使用整个元组生成身份，不反向拆分厂商键。 */
export function sensorIdentity(sensor: SensorDescriptor): string {
  return JSON.stringify([sensor.sensorKey, sensor.metricCode, sensor.unit]);
}

/** 依据正式指标码和设备名称分类；CPU供电/内存区域与阵列区域不能冒充芯片温度。 */
export function classifyMetric(sensor: SensorDescriptor): MonitorGroup | null {
  if (sensor.metricCode === 'power.watts' && sensor.sensorKey === 'system')
    return 'power';
  if (sensor.metricCode === 'fan.rpm' || sensor.metricCode === 'fan.percent')
    return 'fan';
  if (sensor.metricCode !== 'temperature.celsius') return null;
  const name = sensor.name;
  if (
    /SuperCAP|Battery|Capacitor|\b(?:Disk|Drive|HDD|SSD)\b|电池|电容|硬盘|盘温/i.test(
      name,
    )
  )
    return null;
  // 机箱气流与电源内部温度是不同测量对象，不能仅凭 Inlet/Exhaust 字样混算。
  if (!/P\s*\/\s*S|\bPSU?\b|Power\s*Supply|电源/i.test(name)) {
    const normalized = name.replace(/[\s_.:-]+/g, '');
    if (
      /InletAmbient|FrontAmbient|SystemBoardInlet/i.test(normalized) ||
      /^(?:\d+)?Inlet(?:Temp(?:erature)?)?$/i.test(normalized)
    )
      return 'inlet';
    if (
      /Sys(?:tem)?(?:Board)?Exhaust/i.test(normalized) ||
      /^(?:\d+)?Exhaust(?:Temp(?:erature)?)?$/i.test(normalized)
    )
      return 'exhaust';
  }
  if (
    /HD\s*(?:Controller|Cntlr)|\bRAID\b|\bPERC\b|Smart\s*Array|Array\s*Controller|阵列|存储控制器/i.test(
      name,
    )
  )
    return /\bZone\b|区域/i.test(name) ? 'arrayZone' : 'array';
  if (
    /\bCPU\s*[-_]?\s*\d+(?:\b|(?=Temp(?:erature)?\b))/i.test(name) &&
    !/\b(?:VR|DIMM|Memory|Zone|Power|Pwr)\b|区域|供电/i.test(name)
  )
    return 'cpu';
  return null;
}

/** 近期曲线仅保留总览关注的指标，容量和设备峰值仍作为当前值的补充信息。 */
export function isOverviewMetric(sensor: SensorDescriptor): boolean {
  return classifyMetric(sensor) !== null;
}

/** 只有设备明确可用的有限数字才作为当前读数；零值和负温度仍是有效数字。 */
export function availableValue(metric: MetricReading): number | null {
  return metric.availability === 'Available' &&
    typeof metric.value === 'number' &&
    Number.isFinite(metric.value)
    ? metric.value
    : null;
}

/** 推送中断时也按墙钟判断旧值，不等待下一次设备事件才标记过期。 */
export function isSnapshotStale(
  snapshot: ServerTelemetryDto,
  now: number,
): boolean {
  const collectedAt = snapshot.telemetry.collectedAt;
  const time = collectedAt ? Date.parse(collectedAt) : Number.NaN;
  return (
    snapshot.telemetry.stale ||
    !Number.isFinite(time) ||
    now - time >
      Math.max(snapshot.server.samplingIntervalSeconds * 3, 60) * 1000
  );
}

export type TrendPoint = { time: number; value: number | null; series: string };

/** 同名传感器仍沿用独立序列名称，图例与曲线统一从此生成展示标签。 */
export function trendSeriesLabel(
  item: HistorySeriesDto,
  series: HistorySeriesDto[],
): string {
  return series.some((other) => other !== item && other.name === item.name)
    ? `${item.name} (${item.sensorKey})`
    : item.name;
}

/** 保留没有有效样本的空桶，让图表显示真实断点；同名传感器仍保持独立序列。 */
export function trendPoints(series: HistorySeriesDto[]): TrendPoint[] {
  return series.flatMap((item) => {
    const label = trendSeriesLabel(item, series);
    return item.points
      .map((point) => ({
        time: Date.parse(point.time),
        value:
          point.count > 0 &&
          typeof point.average === 'number' &&
          Number.isFinite(point.average)
            ? point.average
            : null,
        series: label,
      }))
      .filter((point) => Number.isFinite(point.time));
  });
}
