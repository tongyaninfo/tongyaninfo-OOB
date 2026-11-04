import type {
  DeviceInventory,
  HardwareComponent,
  MetricReading,
} from '@/services/oob/models';
import { sensorIdentity } from './metrics';

type CpuSensor = Pick<
  MetricReading,
  'sensorKey' | 'metricCode' | 'name' | 'unit'
>;
export type CpuTemperatureLabel = {
  label: string;
  legend: string;
  slot?: string;
  order: number;
};

function normalizeSlot(value: string): string | undefined {
  const slot = Number(value);
  return Number.isSafeInteger(slot) && slot > 0 ? String(slot) : undefined;
}

/** 只认CPU/Proc后的槽号；温度资源自身的temperature:2属于传感器编号，不能当CPU2。 */
function explicitSlots(
  value: string | null | undefined,
  processor = false,
): string[] {
  const marker = processor ? '(?:CPU|Proc(?:essor)?)' : 'CPU';
  const pattern = new RegExp(
    `(?:^|[^a-z0-9])${marker}(?:[\\s._-]*Socket)?[\\s._-]*(\\d+)(?=$|[^a-z0-9]|Temp(?:erature)?(?:$|[^a-z0-9]))`,
    'gi',
  );
  return [...(value || '').matchAll(pattern)].flatMap((match) => {
    const slot = normalizeSlot(match[1]);
    return slot ? [slot] : [];
  });
}
function onlySlot(values: string[]): string | undefined {
  const unique = [...new Set(values)];
  return unique.length === 1 ? unique[0] : undefined;
}
function allProcessors(components: HardwareComponent[]): HardwareComponent[] {
  return components.flatMap((component) => [
    ...(component.kind === 'Processor' ? [component] : []),
    ...allProcessors(component.children),
  ]);
}
function processorSlot(component: HardwareComponent): string | undefined {
  const socket = component.attributes.Socket?.trim();
  const socketSlot =
    socket && /^\d+$/.test(socket) ? normalizeSlot(socket) : undefined;
  const resourceSlot = component.key.match(/\/Processors\/(\d+)\/?$/i)?.[1];
  return onlySlot([
    ...(socketSlot ? [socketSlot] : []),
    ...explicitSlots(socket, true),
    ...explicitSlots(component.key, true),
    ...explicitSlots(component.attributes.Id, true),
    ...explicitSlots(component.attributes.MemberId, true),
    ...explicitSlots(component.name, true),
    ...(resourceSlot && normalizeSlot(resourceSlot)
      ? [normalizeSlot(resourceSlot) as string]
      : []),
  ]);
}

/** 型号只来自已保存的Processor库存；槽号冲突、重复槽位或缺失型号均不猜测对应关系。 */
function processorModels(
  inventory: DeviceInventory | null | undefined,
): Map<string, string> {
  const resources = new Map<string, HardwareComponent[]>();
  for (const processor of allProcessors(inventory?.components || []))
    resources.set(processor.key, [
      ...(resources.get(processor.key) || []),
      processor,
    ]);
  const bySlot = new Map<string, string[]>();
  for (const copies of resources.values()) {
    const slots = copies.map(processorSlot);
    const models = copies.map(
      (component) =>
        component.model?.trim() || component.attributes.Model?.trim() || '',
    );
    if (slots.some((slot) => !slot || slot !== slots[0])) continue;
    const slot = slots[0];
    const model = models.some((value) => !value || value !== models[0])
      ? ''
      : models[0];
    if (slot) bySlot.set(slot, [...(bySlot.get(slot) || []), model]);
  }
  return new Map(
    [...bySlot].flatMap(([slot, models]) =>
      models.length === 1 && models[0] ? [[slot, models[0]]] : [],
    ),
  );
}

/**
 * 当前读数与历史图例复用同一槽号映射，输入顺序不参与硬件关联。
 * 同型号CPU主标签保留完整型号，图例以槽号消歧；不能匹配时仅显示通用CPU序号。
 */
export function cpuTemperatureLabels(
  inventory: DeviceInventory | null | undefined,
  sensors: CpuSensor[],
  genericLabel: (number: string) => string = (number) => `CPU ${number}`,
): Map<string, CpuTemperatureLabel> {
  const models = processorModels(inventory);
  const unique = new Map<string, CpuSensor>();
  for (const sensor of sensors)
    if (!unique.has(sensorIdentity(sensor)))
      unique.set(sensorIdentity(sensor), sensor);
  const rows = [...unique]
    .map(([key, sensor]) => ({
      key,
      slot: onlySlot([
        ...explicitSlots(sensor.name),
        ...explicitSlots(sensor.sensorKey),
      ]),
    }))
    .sort(
      (left, right) =>
        (left.slot && right.slot
          ? Number(left.slot) - Number(right.slot)
          : left.slot
            ? -1
            : right.slot
              ? 1
              : 0) ||
        left.key.localeCompare(right.key, undefined, { numeric: true }),
    );
  const labeled = rows.map((row, index) => {
    const number = row.slot || String(index + 1);
    const model = row.slot ? models.get(row.slot) : undefined;
    return {
      ...row,
      model,
      number,
      label: model || genericLabel(number),
      order: index,
    };
  });
  const duplicates = new Map<string, number>();
  for (const row of labeled)
    duplicates.set(row.label, (duplicates.get(row.label) || 0) + 1);
  const legends = labeled.map((row) => ({
    ...row,
    legend:
      (duplicates.get(row.label) || 0) > 1 && row.model
        ? `${row.label} (${genericLabel(row.number)})`
        : row.label,
  }));
  const occurrences = new Map<string, number>();
  return new Map(
    legends.map((row) => {
      const repeated =
        legends.filter((other) => other.legend === row.legend).length > 1;
      const occurrence = (occurrences.get(row.legend) || 0) + 1;
      occurrences.set(row.legend, occurrence);
      return [
        row.key,
        {
          label: row.label,
          legend: repeated ? `${row.legend} [${occurrence}]` : row.legend,
          slot: row.slot,
          order: row.order,
        },
      ];
    }),
  );
}
