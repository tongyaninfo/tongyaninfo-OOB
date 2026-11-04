import type {
  HardwareComponent,
  HistoryPointDto,
  MetricReading,
} from '@/services/oob/models';
import type {
  AggregateKind,
  AggregatePanel,
  AggregateReading,
  AggregateReadingState,
  AggregateResult,
  AggregateStat,
  PowerPressureRow,
} from './aggregate.types';
import { controllerTemperatures } from './controllers';
import { cpuTemperatureLabels } from './cpus';
import { summarizeFans } from './fans';
import {
  availableValue,
  classifyMetric,
  isSnapshotStale,
  sensorIdentity,
} from './metrics';
import type { OverviewServerData } from './overviewData.types';
import { compareVersions, RECENT_WINDOW_MS } from './recent';

type Sensor = Pick<MetricReading, 'sensorKey' | 'metricCode' | 'name' | 'unit'>;
type Entity = {
  row: OverviewServerData;
  kind: AggregateKind;
  key: string;
  label: string;
  model?: string;
  slot?: string;
  descriptorIds: Set<string>;
  metric?: MetricReading;
  capacityMetric?: MetricReading;
  ambiguous?: boolean;
  counted: boolean;
};
type HistoryRange = { from: number; to: number; bucketMs: number };
const BUCKET_MS = 30000;
const kinds: AggregateKind[] = [
  'cpu',
  'array',
  'inlet',
  'exhaust',
  'fanRpm',
  'fanSpeedPercent',
  'power',
];
const units: Record<AggregateKind, string> = {
  cpu: '°C',
  array: '°C',
  inlet: '°C',
  exhaust: '°C',
  fanRpm: 'RPM',
  fanSpeedPercent: '%',
  power: 'W',
};
const codes: Partial<Record<AggregateKind, string>> = {
  fanRpm: 'fan.rpm',
  fanSpeedPercent: 'fan.percent',
  power: 'power.watts',
};
const statsForKind = (kind: AggregateKind): AggregateStat[] =>
  kind === 'power'
    ? ['total', 'average', 'minimum', 'maximum']
    : kind === 'array'
      ? ['average', 'maximum']
      : kind === 'cpu' || kind === 'inlet' || kind === 'exhaust'
        ? ['maximum', 'average', 'minimum']
        : ['average', 'minimum', 'maximum'];

/** 当前重复资源只保留时间最新的那一条，不能让重复返回增加设备权重。 */
function latestMetrics(metrics: MetricReading[]): MetricReading[] {
  const unique = new Map<string, MetricReading>();
  for (const metric of metrics) {
    const key = sensorIdentity(metric);
    const previous = unique.get(key);
    const time = Date.parse(metric.observedAt);
    const previousTime = previous
      ? Date.parse(previous.observedAt)
      : Number.NEGATIVE_INFINITY;
    if (
      !previous ||
      (!Number.isNaN(time) &&
        (Number.isNaN(previousTime) ||
          compareVersions(metric.observedAt, previous.observedAt) >= 0))
    )
      unique.set(key, metric);
  }
  return [...unique.values()];
}
function mostRecent(metrics: MetricReading[]): MetricReading | undefined {
  return metrics.reduce<MetricReading | undefined>((latest, metric) => {
    if (!latest) return metric;
    const time = Date.parse(metric.observedAt),
      before = Date.parse(latest.observedAt);
    return Number.isFinite(time) &&
      (!Number.isFinite(before) ||
        compareVersions(metric.observedAt, latest.observedAt) >= 0)
      ? metric
      : latest;
  }, undefined);
}
function components(
  items: HardwareComponent[],
  kind: string,
): HardwareComponent[] {
  return items.flatMap((item) => [
    ...(item.kind === kind ? [item] : []),
    ...components(item.children, kind),
  ]);
}

/** 这里只为发现缺测的库存CPU确定明确槽位；型号与探头关联仍复用正式映射。 */
function inventorySlot(item: HardwareComponent): string | undefined {
  const found: string[] = [];
  for (const value of [
    item.attributes.Socket,
    item.key,
    item.name,
    item.attributes.Id,
    item.attributes.MemberId,
  ]) {
    if (!value) continue;
    if (value === item.attributes.Socket && /^\d+$/.test(value.trim()))
      found.push(value.trim());
    for (const match of value.matchAll(
      /(?:^|[^a-z0-9])(?:CPU|Proc(?:essor)?)(?:[\s._-]*Socket)?[\s._-]*(\d+)(?=$|[^a-z0-9])/gi,
    ))
      found.push(match[1]);
  }
  const resource = item.key.match(/\/Processors\/(\d+)\/?$/i)?.[1];
  if (resource) found.push(resource);
  const slots = [
    ...new Set(
      found
        .map(Number)
        .filter((value) => Number.isSafeInteger(value) && value > 0),
    ),
  ];
  return slots.length === 1 ? String(slots[0]) : undefined;
}
function descriptors(
  row: OverviewServerData,
  metrics: MetricReading[],
  now: number,
  range?: HistoryRange,
): Sensor[] {
  // 历史接口保留旧系列元数据；只有窗口内真实有效样本才能作为发现实体的证据。
  // 当前指标中的缺测/不支持项仍全部保留，不能用此过滤掩盖设备本次缺测。
  const observedHistory = row.history.filter((series) =>
    series.points.some((point) => {
      const time = Date.parse(point.time);
      return (
        point.count > 0 &&
        typeof point.average === 'number' &&
        Number.isFinite(point.average) &&
        Number.isFinite(time) &&
        (range
          ? time >= Math.floor(range.from / range.bucketMs) * range.bucketMs &&
            time < range.to
          : time >= now - RECENT_WINDOW_MS && time <= now)
      );
    }),
  );
  return [
    ...new Map<Sensor['sensorKey'], Sensor>(
      [...observedHistory, ...metrics].map((sensor) => [
        sensorIdentity(sensor),
        sensor,
      ]),
    ).values(),
  ];
}
function baseEntity(
  row: OverviewServerData,
  kind: AggregateKind,
  key: string,
  label: string,
): Entity {
  return {
    row,
    kind,
    key: JSON.stringify([row.snapshot.server.id, kind, key]),
    label,
    descriptorIds: new Set(),
    counted: true,
  };
}
function missingEntity(row: OverviewServerData, kind: AggregateKind): Entity {
  return { ...baseEntity(row, kind, 'not-discovered', ''), counted: false };
}

function cpuEntities(
  row: OverviewServerData,
  sensors: Sensor[],
  metrics: MetricReading[],
): Entity[] {
  const candidates = sensors.filter(
    (sensor) => classifyMetric(sensor) === 'cpu',
  );
  const labels = cpuTemperatureLabels(row.inventory, candidates);
  const entities = new Map<string, Entity>();
  const processors = [
    ...new Map(
      components(row.inventory?.components || [], 'Processor').map((item) => [
        item.key,
        item,
      ]),
    ).values(),
  ];
  const inventorySlots = new Map<string, number>();
  for (const processor of processors) {
    const slot = inventorySlot(processor);
    const id = slot ? `slot:${slot}` : `inventory:${processor.key}`;
    if (slot) inventorySlots.set(slot, (inventorySlots.get(slot) || 0) + 1);
    const entity =
      entities.get(id) ||
      baseEntity(row, 'cpu', id, slot ? `CPU ${slot}` : processor.name);
    entity.slot = slot;
    entity.model =
      processor.model?.trim() ||
      processor.attributes.Model?.trim() ||
      undefined;
    entities.set(id, entity);
  }
  for (const sensor of candidates) {
    const label = labels.get(sensorIdentity(sensor));
    const id = label?.slot
      ? `slot:${label.slot}`
      : `sensor:${sensorIdentity(sensor)}`;
    const entity =
      entities.get(id) ||
      baseEntity(
        row,
        'cpu',
        id,
        label?.slot ? `CPU ${label.slot}` : sensor.name,
      );
    entity.slot = label?.slot;
    if (label?.slot)
      entity.model =
        label.label !== `CPU ${label.slot}` ? label.label : undefined;
    entity.descriptorIds.add(sensorIdentity(sensor));
    entities.set(id, entity);
  }
  // 同一槽位有多个探头或多个库存实体时，不任意挑选或重复累计。
  for (const entity of entities.values()) {
    entity.ambiguous =
      entity.descriptorIds.size > 1 ||
      Boolean(entity.slot && (inventorySlots.get(entity.slot) || 0) > 1);
    entity.metric = mostRecent(
      metrics.filter((metric) =>
        entity.descriptorIds.has(sensorIdentity(metric)),
      ),
    );
  }
  return entities.size ? [...entities.values()] : [missingEntity(row, 'cpu')];
}
function arrayEntities(
  row: OverviewServerData,
  sensors: Sensor[],
  metrics: MetricReading[],
): Entity[] {
  const cards = controllerTemperatures(row.inventory, sensors);
  return cards.length
    ? cards.map((card) => {
        const entity = baseEntity(row, 'array', card.key, card.name);
        entity.model = card.model === '-' ? undefined : card.model;
        if (card.temperature) {
          entity.descriptorIds.add(sensorIdentity(card.temperature));
          entity.metric = metrics.find(
            (metric) =>
              sensorIdentity(metric) ===
              sensorIdentity(card.temperature as Sensor),
          );
        }
        return entity;
      })
    : [missingEntity(row, 'array')];
}
/** 机箱多探头分别计数，保留厂商标识；不能把排气探头合并成一个虚构读数。 */
function airflowEntities(
  row: OverviewServerData,
  sensors: Sensor[],
  metrics: MetricReading[],
): Entity[] {
  return (['inlet', 'exhaust'] as const).flatMap((kind) => {
    const selected = sensors.filter(
      (sensor) => classifyMetric(sensor) === kind,
    );
    return selected.length
      ? selected.map((sensor) => {
          const identity = sensorIdentity(sensor);
          const entity = baseEntity(row, kind, identity, sensor.name);
          entity.descriptorIds.add(identity);
          entity.metric = metrics.find(
            (metric) => sensorIdentity(metric) === identity,
          );
          return entity;
        })
      : [missingEntity(row, kind)];
  });
}
function fanEntities(
  row: OverviewServerData,
  sensors: Sensor[],
  metrics: MetricReading[],
): Entity[] {
  const fanKeys = [
    ...new Set(
      sensors
        .filter((sensor) => classifyMetric(sensor) === 'fan')
        .map((sensor) => sensor.sensorKey),
    ),
  ];
  const current = new Map(
    summarizeFans(metrics).fans.map((fan) => [fan.sensorKey, fan]),
  );
  return (['fanRpm', 'fanSpeedPercent'] as const).flatMap((kind) =>
    fanKeys.length
      ? fanKeys.map((key) => {
          const related = sensors.filter(
            (sensor) =>
              sensor.sensorKey === key && sensor.metricCode === codes[kind],
          );
          const name =
            current.get(key)?.name ||
            sensors.find((sensor) => sensor.sensorKey === key)?.name ||
            key;
          const entity = baseEntity(row, kind, key, name);
          entity.descriptorIds = new Set(
            related
              .filter((sensor) => sensor.unit === units[kind])
              .map(sensorIdentity),
          );
          const fan = current.get(key);
          entity.metric = kind === 'fanRpm' ? fan?.rpm : fan?.speedPercent;
          return entity;
        })
      : [missingEntity(row, kind)],
  );
}
function powerEntity(
  row: OverviewServerData,
  sensors: Sensor[],
  metrics: MetricReading[],
): Entity {
  const entity = baseEntity(row, 'power', 'system', row.snapshot.server.name);
  entity.descriptorIds = new Set(
    sensors
      .filter(
        (sensor) =>
          sensor.metricCode === 'power.watts' &&
          sensor.sensorKey === 'system' &&
          sensor.unit === 'W',
      )
      .map(sensorIdentity),
  );
  entity.metric = mostRecent(
    metrics.filter(
      (metric) =>
        metric.sensorKey === 'system' && metric.metricCode === 'power.watts',
    ),
  );
  entity.capacityMetric = mostRecent(
    metrics.filter(
      (metric) =>
        metric.sensorKey === 'system' &&
        metric.metricCode === 'power.capacity.watts' &&
        metric.unit === 'W',
    ),
  );
  return entity;
}
function validNumber(
  kind: AggregateKind,
  value: number | null,
): value is number {
  return (
    value !== null &&
    Number.isFinite(value) &&
    (units[kind] === '°C' || value >= 0) &&
    (kind !== 'fanSpeedPercent' || value <= 100)
  );
}
function reading(entity: Entity, now: number): AggregateReading {
  const { snapshot } = entity.row;
  const metric = entity.metric;
  const observed = metric ? Date.parse(metric.observedAt) : Number.NaN;
  const threshold =
    Math.max(3 * snapshot.server.samplingIntervalSeconds, 60) * 1000;
  let state: AggregateReadingState = !snapshot.server.enabled
    ? 'disabled'
    : snapshot.server.connectionStatus !== 'Online'
      ? 'offline'
      : isSnapshotStale(snapshot, now)
        ? 'stale'
        : entity.ambiguous
          ? 'ambiguous'
          : !metric
            ? 'missing'
            : metric.unit !== units[entity.kind]
              ? 'invalid'
              : metric.availability === 'Unsupported'
                ? 'unsupported'
                : metric.availability !== 'Available' || metric.value === null
                  ? 'missing'
                  : !Number.isFinite(observed) ||
                      observed > now ||
                      now - observed > threshold
                    ? 'stale'
                    : !validNumber(entity.kind, availableValue(metric))
                      ? 'invalid'
                      : 'available';
  if (!entity.counted && state === 'available') state = 'missing';
  return {
    key: entity.key,
    serverId: snapshot.server.id,
    serverName: snapshot.server.name,
    label: entity.label,
    model: entity.model,
    slot: entity.slot,
    value: state === 'available' && metric ? availableValue(metric) : null,
    unit: units[entity.kind],
    observedAt: metric?.observedAt || null,
    availability: metric?.availability,
    state,
    counted: entity.counted,
    health: metric?.health,
  };
}
/** 容量与当前功率各自校验新鲜度；离线或旧快照不能继续显示历史压力。 */
function powerPressureRow(
  entity: Entity,
  current: AggregateReading,
  now: number,
): PowerPressureRow {
  const capacityMetric = entity.capacityMetric;
  const capacityTime = capacityMetric
    ? Date.parse(capacityMetric.observedAt)
    : Number.NaN;
  const threshold =
    Math.max(3 * entity.row.snapshot.server.samplingIntervalSeconds, 60) * 1000;
  const capacityValue = capacityMetric ? availableValue(capacityMetric) : null;
  const capacityWatts =
    current.value !== null &&
    capacityValue !== null &&
    capacityValue > 0 &&
    Number.isFinite(capacityTime) &&
    capacityTime <= now &&
    now - capacityTime <= threshold
      ? capacityValue
      : null;
  const computed =
    current.value !== null && capacityWatts !== null
      ? (current.value / capacityWatts) * 100
      : null;
  const percent =
    computed !== null && Number.isFinite(computed) ? computed : null;
  return {
    serverId: current.serverId,
    serverName: current.serverName,
    usedWatts: current.value,
    capacityWatts,
    percent,
    reason:
      current.value === null ? 'power' : percent === null ? 'capacity' : null,
    powerState: current.state,
  };
}
function statistics(values: number[]): AggregatePanel['stats'] {
  if (!values.length)
    return { average: null, minimum: null, maximum: null, total: null };
  const sum = values.reduce((total, value) => total + value, 0);
  return {
    average: values.reduce((total, value) => total + value / values.length, 0),
    minimum: Math.min(...values),
    maximum: Math.max(...values),
    total: Number.isFinite(sum) ? sum : null,
  };
}
function gapAt(entity: Entity, start: number, end: number, now: number) {
  return entity.row.gaps.some(
    (gap) =>
      Number.isFinite(gap.from) && gap.from < end && (gap.to ?? now) >= start,
  );
}
function nullPoint(time: number): HistoryPointDto {
  return {
    time: new Date(time).toISOString(),
    average: null,
    minimum: null,
    maximum: null,
    count: 0,
  };
}

/** 近期取桶内最后实际点，长历史沿用后端桶均值；两者都不沿用空桶前值。 */
function historyPanel(
  kind: AggregateKind,
  entities: Entity[],
  panel: AggregatePanel,
  now: number,
  range?: HistoryRange,
) {
  const from = range?.from ?? now - RECENT_WINDOW_MS;
  const bucketMs = range?.bucketMs ?? BUCKET_MS;
  const first = Math.floor(from / bucketMs) * bucketMs;
  const last = range
    ? (Math.ceil(range.to / bucketMs) - 1) * bucketMs
    : Math.floor(now / bucketMs) * bucketMs;
  const buckets = new Map<
    number,
    Map<
      string,
      { entity: Entity; time: number; version: string; value: number | null }
    >
  >();
  for (const entity of entities) {
    if (!entity.counted || entity.ambiguous) continue;
    for (const series of entity.row.history) {
      if (
        !entity.descriptorIds.has(sensorIdentity(series)) ||
        series.unit !== units[kind]
      )
        continue;
      for (const point of series.points) {
        const time = Date.parse(point.time);
        // 历史首桶虽早于from，其值已由后端按[from,to)筛选，不能丢掉部分桶。
        if (
          !Number.isFinite(time) ||
          (range ? time < first || time >= range.to : time < from || time > now)
        )
          continue;
        const bucket = Math.floor(time / bucketMs) * bucketMs;
        let readings = buckets.get(bucket);
        if (!readings) {
          readings = new Map();
          buckets.set(bucket, readings);
        }
        const previous = readings.get(entity.key);
        if (previous && compareVersions(previous.version, point.time) > 0)
          continue;
        const value =
          point.count > 0 && validNumber(kind, point.average)
            ? point.average
            : null;
        readings.set(entity.key, { entity, time, version: point.time, value });
      }
    }
  }
  const chosen = statsForKind(kind);
  panel.series = chosen.map((stat) => ({
    sensorKey: `aggregate:${kind}:${stat}`,
    metricCode: `aggregate.${kind}.${stat}`,
    name: stat,
    unit: units[kind],
    points: [],
  }));
  let previousParticipants = '';
  for (let bucket = first; bucket <= last; bucket += bucketMs) {
    const samples = [...(buckets.get(bucket)?.values() || [])].filter(
      (sample) =>
        sample.value !== null &&
        (range || !gapAt(sample.entity, bucket, bucket + bucketMs, now)),
    );
    const values = samples.map((sample) => sample.value as number);
    const stats = statistics(values);
    const serverNames = [
      ...new Set(
        samples.map((sample) => sample.entity.row.snapshot.server.name),
      ),
    ];
    const participants = JSON.stringify(
      samples.map((sample) => sample.entity.row.snapshot.server.id).sort(),
    );
    panel.coverage[bucket] = {
      validCount: values.length,
      totalCount: panel.totalCount,
      serverNames,
    };
    // 总功率来源变化时断开连接，避免把缺一台服务器误读为节能或突然降载。
    if (
      kind === 'power' &&
      previousParticipants &&
      participants !== previousParticipants
    )
      for (const series of panel.series)
        series.points.push(nullPoint(bucket - 1));
    for (let index = 0; index < chosen.length; index++) {
      const value = stats[chosen[index]];
      panel.series[index].points.push({
        time: new Date(bucket).toISOString(),
        average: value,
        minimum: value,
        maximum: value,
        count: value === null ? 0 : values.length,
      });
    }
    previousParticipants = participants;
  }
  // 实时推送缺口只描述近期连接状态，不能覆盖数据库已经返回的历史桶。
  if (range) return;
  // 即使一台设备失败时其余设备仍有值，明确的故障时刻也必须在整体曲线上可辨认。
  const gaps = new Set<number>();
  for (const entity of entities.filter(
    (item) => item.counted && !item.ambiguous && item.descriptorIds.size > 0,
  ))
    for (const gap of entity.row.gaps)
      for (const edge of [gap.from, gap.to])
        if (
          edge !== undefined &&
          Number.isFinite(edge) &&
          edge >= from &&
          edge <= now
        )
          gaps.add(edge);
  for (const series of panel.series) {
    const points = new Map(
      series.points.map((point) => [Date.parse(point.time), point]),
    );
    for (const gap of gaps) points.set(gap, nullPoint(gap));
    series.points = [...points.entries()]
      .sort(([left], [right]) => left - right)
      .map(([, point]) => point);
  }
}

function dashboardEntities(
  servers: OverviewServerData[],
  now: number,
  range?: HistoryRange,
): Entity[] {
  return servers.flatMap((row) => {
    // 历史身份来自选定区间，当前新增探头或离线快照不能改变过去的参与实体。
    const metrics = range ? [] : latestMetrics(row.snapshot.telemetry.metrics);
    const known = descriptors(row, metrics, now, range);
    return [
      ...cpuEntities(row, known, metrics),
      ...arrayEntities(row, known, metrics),
      ...airflowEntities(row, known, metrics),
      ...fanEntities(row, known, metrics),
      powerEntity(row, known, metrics),
    ];
  });
}

/** 只汇总已加载的本地快照与真实历史；不发请求，不改变采集或设备状态。 */
export function aggregateDashboard(
  servers: OverviewServerData[],
  now: number,
): AggregateResult {
  const entities = dashboardEntities(servers, now);
  return Object.fromEntries(
    kinds.map((kind) => {
      const selected = entities.filter((entity) => entity.kind === kind);
      const readings = selected.map((entity) => reading(entity, now));
      const values = readings.flatMap((item) =>
        item.value === null ? [] : [item.value],
      );
      const panel: AggregatePanel = {
        kind,
        unit: units[kind],
        readings,
        stats: statistics(values),
        validCount: values.length,
        totalCount: readings.filter((item) => item.counted).length,
        series: [],
        coverage: {},
        pressureRows:
          kind === 'power'
            ? selected.map((entity, index) =>
                powerPressureRow(entity, readings[index], now),
              )
            : undefined,
      };
      historyPanel(kind, selected, panel, now);
      return [kind, panel];
    }),
  ) as AggregateResult;
}

/** 仅生成指定区间的曲线和覆盖数，不把当前读数或设备状态包装成历史摘要。 */
export function aggregateHistory(
  servers: OverviewServerData[],
  kind: AggregateKind,
  window: { from: string; to: string; bucketSeconds: number },
): AggregatePanel {
  const panel: AggregatePanel = {
    kind,
    unit: units[kind],
    readings: [],
    stats: statistics([]),
    validCount: 0,
    totalCount: 0,
    series: [],
    coverage: {},
  };
  const from = Date.parse(window.from);
  const to = Date.parse(window.to);
  if (
    !Number.isFinite(from) ||
    !Number.isFinite(to) ||
    from >= to ||
    !Number.isFinite(window.bucketSeconds) ||
    window.bucketSeconds <= 0
  )
    return panel;
  // 与后端约1000桶的下限一致，避免自选长区间被误拆成细桶或制造大量空点。
  const bucketMs =
    Math.max(window.bucketSeconds, Math.ceil((to - from) / 1000000), 1) * 1000;
  const range = { from, to, bucketMs };
  const selected = dashboardEntities(servers, to, range).filter(
    (entity) => entity.kind === kind,
  );
  panel.totalCount = selected.filter((entity) => entity.counted).length;
  historyPanel(kind, selected, panel, to, range);
  return panel;
}
