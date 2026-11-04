import type enUS from '@/locales/en-US/oob';
import type { MetricReading } from '@/services/oob/models';

/** 所有时间只在展示边界转换一次，后端 ISO 时间始终代表 UTC 时点。 */
export function formatTime(value?: string | null): string {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.valueOf())) return '—';
  return new Intl.DateTimeFormat('zh-CN', {
    timeZone: 'Asia/Shanghai',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  })
    .format(date)
    .replaceAll('/', '-');
}

/** long 容量不先转 number，使用 BigInt 保留原始整数精度。 */
export function formatCapacity(value?: string | null): string {
  if (!value) return '—';
  // 容量字符串校验属于预期数据处理，不借助异常表达无效业务值。
  if (!/^[+-]?\d+$/.test(value)) return value;
  const bytes = BigInt(value);
  const gib = 1024n ** 3n;
  return `${Number((bytes * 10n) / gib) / 10} GiB`;
}

export function formatReading(value?: number | null, unit = '') {
  return value === null || value === undefined
    ? '—'
    : `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(value)}${unit ? ` ${unit}` : ''}`;
}

/** 数值单位决定图表分组，RPM、百分比和温度不能画在同一数值轴上。 */
export function metricGroup(unit: string): string {
  return unit || 'value';
}

/** 能耗沿用统一的最多两位展示精度；原始计算和存储值不在此舍入。 */
export function formatEnergyNumber(value?: number | null): string {
  return formatReading(value);
}

type MessageId = keyof typeof enUS;

/** 百分比使用统一业务名称，保留物理风扇标识；历史名称不影响正式指标码。 */
export function metricDisplayName(
  metric: Pick<MetricReading, 'metricCode' | 'name'>,
  translate: (key: MessageId) => string,
): string {
  if (metric.metricCode !== 'fan.percent') return metric.name;
  const label = translate('oob.monitor.fanDetails.speedPercent');
  if (metric.name === label || metric.name.endsWith(` · ${label}`))
    return metric.name;
  const sensor = metric.name
    .replace(
      /\s*(?:(?:设备|設備|裝置)?(?:转速百分比|轉速百分比)|(?:Device\s+)?speed\s+percentage|PWM\s*(?:占空比|佔空比|duty\s*cycle))(?:\s*[（(]\s*(?:PWM\s*\/\s*)?%\s*[）)])?\s*$/i,
      '',
    )
    .replace(/\s*·\s*$/, '')
    .trim();
  return sensor ? `${sensor} · ${label}` : label;
}

const availabilityLabels: Record<string, MessageId> = {
  Available: 'oob.availability.Available',
  Unsupported: 'oob.availability.Unsupported',
  TemporarilyUnavailable: 'oob.availability.TemporarilyUnavailable',
  NotInstalled: 'oob.availability.NotInstalled',
  PermissionDenied: 'oob.availability.PermissionDenied',
  ReadFailed: 'oob.availability.ReadFailed',
  Failed: 'oob.availability.Failed',
  Stale: 'oob.availability.Stale',
  Unknown: 'oob.availability.Unknown',
};
const sourceLabels: Record<string, MessageId> = {
  Device: 'oob.source.Device',
  Derived: 'oob.source.Derived',
  Estimated: 'oob.source.Estimated',
  Observed: 'oob.source.Observed',
};

/** 只翻译展示标签，所有判断仍使用原机器码；未知厂商值原样保留。 */
export function readingCodeLabel(
  kind: 'availability' | 'source',
  code: string,
  translate: (key: MessageId) => string,
): string {
  const labels = kind === 'availability' ? availabilityLabels : sourceLabels;
  return Object.hasOwn(labels, code) ? translate(labels[code]) : code;
}
