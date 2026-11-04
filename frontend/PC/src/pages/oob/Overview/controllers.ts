import type {
  DeviceInventory,
  HardwareComponent,
  MetricReading,
} from '@/services/oob/models';
import { classifyMetric, sensorIdentity } from './metrics';

type TemperatureSensor = Pick<
  MetricReading,
  'sensorKey' | 'name' | 'metricCode' | 'unit'
>;
export type ControllerTemperature<T extends TemperatureSensor = MetricReading> =
  {
    key: string;
    model: string;
    name: string;
    temperature: T | undefined;
  };

/** 仅展示有阵列能力依据的存储控制器，AHCI 芯片组不能冒充阵列卡。 */
function isArrayController(component: HardwareComponent): boolean {
  if (component.kind !== 'StorageController') return false;
  const description = [
    component.key,
    component.name,
    component.model,
    component.attributes.AdapterType,
    component.attributes.ControllerType,
    component.attributes.CurrentOperatingMode,
  ]
    .filter(Boolean)
    .join(' ');
  return (
    !/\bAHCI\b/i.test(description) &&
    /\bRAID\b|\bPERC\b|Smart\s*Array|ArrayControllers?|阵列/i.test(description)
  );
}

function allControllers(components: HardwareComponent[]): HardwareComponent[] {
  return components.flatMap((component) => [
    ...(isArrayController(component) ? [component] : []),
    ...allControllers(component.children),
  ]);
}

function modelName(component: HardwareComponent): string {
  return component.model?.trim() || component.attributes.Model?.trim() || '-';
}

/** 匹配完整资源/标识或有边界的型号名，避免 1-1 与 1-10 等前缀误关联。 */
function hasReference(text: string, reference: string | null | undefined) {
  const value = reference?.trim().replace(/\/+$/, '');
  if (!value || value.length < 4) return false;
  const escaped = value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return new RegExp(`(?:^|[^a-z0-9])${escaped}(?=$|[^a-z0-9])`, 'i').test(text);
}

function matchScore(metric: TemperatureSensor, controller: HardwareComponent) {
  const text = `${metric.sensorKey} ${metric.name}`;
  const resource = controller.key.replace(/\/+$/, '').toLowerCase();
  const sensorResource = metric.sensorKey
    .replace(/^temperature:/i, '')
    .replace(/\/+$/, '')
    .toLowerCase();
  // 链接只接受完整相等；子资源探头不因父路径相同就归为卡本体。
  if (resource.includes('/') && sensorResource === resource) return 3;
  if (
    [
      resource.includes('/') ? undefined : controller.key,
      controller.attributes.MemberId,
      controller.attributes.Id,
    ].some((reference) => hasReference(text, reference))
  )
    return 3;
  const model = modelName(controller);
  if (model !== '-' && hasReference(text, model)) return 2;
  // 厂商有时只在传感器名返回 P420i/H730P 等型号码，仍须在全部卡中唯一匹配。
  const codes = model
    .split(/[^a-z0-9]+/i)
    .filter(
      (part) => part.length >= 4 && /[a-z]/i.test(part) && /\d/.test(part),
    );
  if (codes.some((code) => hasReference(text, code))) return 2;
  return hasReference(text, controller.name) ? 1 : 0;
}

/**
 * 型号来自同一设备库存；多卡只接受唯一资源/型号匹配，不能把匿名温度按数组顺序复制。
 * 没有库存时仍保留设备明确报告的控制器探头，但不推断型号或与其他实体关联。
 */
export function controllerTemperatures<T extends TemperatureSensor>(
  inventory: DeviceInventory | null | undefined,
  metrics: T[],
): ControllerTemperature<T>[] {
  const controllers = [
    ...new Map(
      allControllers(inventory?.components || []).map((component) => [
        component.key,
        component,
      ]),
    ).values(),
  ];
  const candidates = metrics.filter(
    (metric) =>
      metric.metricCode === 'temperature.celsius' &&
      classifyMetric(metric) !== 'arrayZone' &&
      classifyMetric(metric) !== 'cpu' &&
      !/SuperCAP|Battery|Capacitor|\b(?:Disk|Drive|HDD|SSD)\b|电池|电容|硬盘|盘温/i.test(
        metric.name,
      ),
  );
  if (controllers.length === 0)
    return [
      ...new Map(
        candidates
          .filter((metric) => classifyMetric(metric) === 'array')
          .map((metric) => [sensorIdentity(metric), metric]),
      ).values(),
    ]
      .map((metric) => ({
        key: sensorIdentity(metric),
        model: '-',
        name: metric.name,
        temperature: metric,
      }))
      .sort((left, right) => left.key.localeCompare(right.key));
  const matches = new Map<string, T[]>();
  for (const metric of candidates) {
    const scores = controllers.map((controller) => ({
      controller,
      score: matchScore(metric, controller),
    }));
    const best = Math.max(0, ...scores.map((item) => item.score));
    const owners = scores.filter((item) => item.score === best && best > 0);
    if (owners.length !== 1) continue;
    const key = owners[0].controller.key;
    matches.set(key, [...(matches.get(key) || []), metric]);
  }
  const anonymous = candidates.filter(
    (metric) =>
      !metric.sensorKey.includes('/') &&
      /^(?:\d+\s*[-:.]\s*)?(?:HD\s*Controller|(?:RAID|Array)\s*Controller)(?:\s*Temp(?:erature)?)?$/i.test(
        metric.name.trim(),
      ),
  );
  return controllers
    .map((controller) => {
      const linked = matches.get(controller.key) || [];
      const temperature =
        linked.length === 1
          ? linked[0]
          : linked.length === 0 &&
              controllers.length === 1 &&
              anonymous.length === 1
            ? anonymous[0]
            : undefined;
      return {
        key: controller.key,
        model: modelName(controller),
        name: controller.name,
        temperature,
      };
    })
    .sort(
      (a, b) => a.model.localeCompare(b.model) || a.key.localeCompare(b.key),
    );
}
