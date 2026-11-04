import dayjs from 'dayjs';
import timezone from 'dayjs/plugin/timezone';
import utc from 'dayjs/plugin/utc';
import type { DeviceInventory, HistorySeriesDto } from '@/services/oob/models';
import { formatTime } from '../format';
import { controllerTemperatures } from './controllers';
import { cpuTemperatureLabels } from './cpus';
import { averageFanHistory } from './fans';
import { classifyMetric, sensorIdentity } from './metrics';

dayjs.extend(utc);
dayjs.extend(timezone);

export type HistoryPanelKind =
  | 'temperatures'
  | 'cpu'
  | 'array'
  | 'airflow'
  | 'fans'
  | 'fanRpmDetails'
  | 'fanPercentDetails'
  | 'power';
export type HistoryPeriod = '24h' | '7d' | 'month' | 'custom';
export type HistoryWindow = {
  period: HistoryPeriod;
  from: string;
  to: string;
  bucketSeconds: number;
};
export const HISTORY_TIME_ZONE = 'Asia/Shanghai';
export const HISTORY_INPUT_FORMAT = 'YYYY-MM-DD HH:mm:ss';

/** 时间范围只在用户打开/切换/查询时固定；自然月按上海日历减月，不固定当作30天。 */
export function historyWindow(
  period: Exclude<HistoryPeriod, 'custom'>,
  now: number,
): HistoryWindow {
  // 实例 .tz() 和带偏移实例的日历运算会经过浏览器本地时间，DST日可能偏移一小时。
  // 固定跨度始终在UTC瞬间上计算；自然月只在UTC承载的上海墙钟上做日历运算。
  const end = dayjs.utc(now);
  let start = end.subtract(period === '7d' ? 7 * 24 : 24, 'hour');
  if (period === 'month') {
    const previousMonth = dayjs
      .utc(formatTime(end.toISOString()))
      .millisecond(end.millisecond())
      .subtract(1, 'month');
    start = dayjs
      .tz(
        previousMonth.format(`${HISTORY_INPUT_FORMAT}.SSS`),
        HISTORY_TIME_ZONE,
      )
      .utc();
  }
  return {
    period,
    from: start.toISOString(),
    to: end.toISOString(),
    bucketSeconds: period === 'month' ? 3600 : period === '7d' ? 900 : 300,
  };
}

/** 日期框使用上海墙钟输入，提交时才转 UTC；与后端366天和最多约1000桶限制保持一致。 */
export function customHistoryWindow(
  from: string,
  to: string,
): HistoryWindow | null {
  const pattern = /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/;
  if (!pattern.test(from) || !pattern.test(to)) return null;
  try {
    const start = dayjs.tz(from, HISTORY_TIME_ZONE).utc();
    const end = dayjs.tz(to, HISTORY_TIME_ZONE).utc();
    // Day.js会将2月30日等输入归一化；回比原文以阻止无效日期被静默改写。
    if (
      dayjs.utc(from).format(HISTORY_INPUT_FORMAT) !== from ||
      dayjs.utc(to).format(HISTORY_INPUT_FORMAT) !== to
    )
      return null;
    const seconds = end.diff(start, 'second', true);
    if (!Number.isFinite(seconds) || seconds <= 0 || seconds > 366 * 86400)
      return null;
    const preferred =
      seconds <= 86400 ? 300 : seconds <= 7 * 86400 ? 900 : 3600;
    return {
      period: 'custom',
      from: start.toISOString(),
      to: end.toISOString(),
      bucketSeconds: Math.max(preferred, Math.ceil(seconds / 1000 / 60) * 60),
    };
  } catch {
    return null;
  }
}

/** 仅查询本地已入库指标；风扇各类单位分别读取，避免下载无关传感器历史。 */
export const historyMetricCodes: Record<HistoryPanelKind, string[]> = {
  temperatures: ['temperature.celsius'],
  cpu: ['temperature.celsius'],
  array: ['temperature.celsius'],
  airflow: ['temperature.celsius'],
  fans: ['fan.rpm', 'fan.percent'],
  fanRpmDetails: ['fan.rpm'],
  fanPercentDetails: ['fan.percent'],
  power: ['power.watts'],
};

export type HistoryTrendGroup = {
  key: string;
  title: string;
  unit: string;
  series: HistorySeriesDto[];
  /** 以传感器稳定标识定义线型，显示名称变化不会改变进气语义。 */
  dashedSeries?: readonly string[];
};

/** 只转换展示名称和分组，数值全部沿用本地查询返回；空桶保留，风扇均值按同桶等权。 */
export function historyTrendGroups(
  kind: HistoryPanelKind,
  series: HistorySeriesDto[],
  inventory: DeviceInventory | null | undefined,
  labels: {
    temperatures: string;
    cpu?: string;
    array?: string;
    airflow?: string;
    fans?: string;
    power: string;
    rpm: string;
    speedPercent: string;
    cpuSlot: (number: string) => string;
  },
): HistoryTrendGroup[] {
  if (kind === 'fanRpmDetails' || kind === 'fanPercentDetails') {
    const rpm = kind === 'fanRpmDetails';
    const code = rpm ? 'fan.rpm' : 'fan.percent';
    const unit = rpm ? 'RPM' : '%';
    // 单机卡保留每个物理风扇的原始序列，单位选择不触发换算或跨风扇平均。
    return [
      {
        key: code,
        title: labels.fans ? `${labels.fans} · ${unit}` : unit,
        unit,
        series: series.filter(
          (row) => row.metricCode === code && row.unit === unit,
        ),
      },
    ];
  }
  if (kind === 'fans')
    return averageFanHistory(series, labels)
      .filter(
        (row) =>
          row.metricCode !== 'fan.percent' ||
          row.points.some(
            (point) =>
              point.count > 0 &&
              point.average !== null &&
              Number.isFinite(point.average),
          ),
      )
      .map((row) => ({
        key: row.metricCode,
        title: row.name,
        unit: row.unit,
        series: [row],
      }));
  if (kind === 'power')
    return [
      {
        key: 'power',
        title: labels.power,
        unit: 'W',
        series: series.filter((row) => classifyMetric(row) === 'power'),
      },
    ];
  const temperatures = series.filter(
    (row) => row.metricCode === 'temperature.celsius',
  );
  if (kind === 'array') {
    // 与近期卡片共用库存关联；无库存时只接受明确卡温，区域/CPU/盘温不混入。
    return [
      {
        key: 'array',
        title: labels.array || labels.temperatures,
        unit: '°C',
        series: controllerTemperatures(inventory, temperatures).flatMap(
          (row) =>
            row.temperature
              ? [
                  {
                    ...row.temperature,
                    name: row.model === '-' ? row.name : row.model,
                  },
                ]
              : [],
        ),
      },
    ];
  }
  if (kind === 'airflow') {
    const selected = temperatures.filter((row) =>
      ['inlet', 'exhaust'].includes(classifyMetric(row) || ''),
    );
    return [
      {
        key: 'airflow',
        title: labels.airflow || labels.temperatures,
        unit: '°C',
        series: selected,
        dashedSeries: selected
          .filter((row) => classifyMetric(row) === 'inlet')
          .map(sensorIdentity),
      },
    ];
  }
  const cpus = cpuTemperatureLabels(
    inventory,
    temperatures.filter((row) => classifyMetric(row) === 'cpu'),
    labels.cpuSlot,
  );
  const models = new Map(
    controllerTemperatures(inventory, temperatures).flatMap((row) =>
      row.temperature
        ? [
            [
              sensorIdentity(row.temperature),
              row.model === '-' ? row.name : row.model,
            ] as const,
          ]
        : [],
    ),
  );
  const selected = temperatures
    .filter(
      (row) =>
        classifyMetric(row) === 'cpu' ||
        (kind !== 'cpu' &&
          (classifyMetric(row) === 'array' || models.has(sensorIdentity(row)))),
    )
    .map((row) => ({
      ...row,
      name:
        cpus.get(sensorIdentity(row))?.legend ||
        models.get(sensorIdentity(row)) ||
        row.name,
    }));
  return [
    {
      key: kind,
      title:
        kind === 'cpu'
          ? labels.cpu || labels.temperatures
          : labels.temperatures,
      unit: '°C',
      series: selected,
    },
  ];
}

const historyClock = new Intl.DateTimeFormat('zh-CN', {
  timeZone: 'Asia/Shanghai',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
});
export function historyTick(value: number | string) {
  const time =
    typeof value === 'number'
      ? value
      : /^\d+$/.test(value)
        ? Number(value)
        : Date.parse(value);
  return Number.isFinite(time)
    ? historyClock.format(new Date(time)).replaceAll('/', '-')
    : String(value);
}
