import { describe, expect, it } from 'vitest';
import type {
  DeviceInventory,
  HardwareComponent,
  HistorySeriesDto,
  MetricReading,
} from '@/services/oob/models';
import { telemetryFixture } from '../../../../tests/oobFixtures';
import { aggregateDashboard, aggregateHistory } from './aggregate';
import type { OverviewServerData } from './overviewData.types';

const now = Date.UTC(2026, 9, 25, 3, 15);
const iso = (offset = 0) => new Date(now + offset).toISOString();
function metric(
  name: string,
  value: number | null,
  extra: Partial<MetricReading> = {},
): MetricReading {
  return {
    name,
    sensorKey: name,
    metricCode: 'temperature.celsius',
    value,
    unit: '°C',
    availability: value === null ? 'Unavailable' : 'Available',
    source: 'Device',
    health: null,
    observedAt: iso(),
    deviceTime: null,
    ...extra,
  };
}
function component(
  kind: string,
  key: string,
  model: string,
  attributes: HardwareComponent['attributes'] = {},
): HardwareComponent {
  return {
    kind,
    key,
    name: key,
    model,
    attributes,
    manufacturer: null,
    serialNumber: null,
    status: 'OK',
    capacityBytes: null,
    children: [],
  };
}
function row(
  id: string,
  metrics: MetricReading[],
  hardware: HardwareComponent[] = [],
): OverviewServerData {
  const snapshot = telemetryFixture(iso(), metrics);
  snapshot.server.id = id;
  snapshot.server.name = `Server ${id}`;
  const inventory: DeviceInventory = {
    components: hardware,
    identity: snapshot.server.identity,
    collectedAt: iso(),
    warnings: [],
  };
  return {
    snapshot,
    inventory,
    history: [],
    gaps: [],
    loading: false,
    issues: [],
  };
}
function history(
  sensor: MetricReading,
  points: [number, number | null][],
): HistorySeriesDto {
  return {
    sensorKey: sensor.sensorKey,
    name: sensor.name,
    metricCode: sensor.metricCode,
    unit: sensor.unit,
    points: points.map(([offset, value]) => ({
      time: iso(offset),
      average: value,
      minimum: value,
      maximum: value,
      count: value === null ? 0 : 1,
    })),
  };
}
const rpm = (
  key: string,
  value: number | null,
  extra: Partial<MetricReading> = {},
) => metric(key, value, { metricCode: 'fan.rpm', unit: 'RPM', ...extra });
const watts = (value: number | null, extra: Partial<MetricReading> = {}) =>
  metric('System power', value, {
    sensorKey: 'system',
    metricCode: 'power.watts',
    unit: 'W',
    ...extra,
  });
const capacity = (value: number | null, extra: Partial<MetricReading> = {}) =>
  metric('Power capacity', value, {
    sensorKey: 'system',
    metricCode: 'power.capacity.watts',
    unit: 'W',
    ...extra,
  });
function valueAt(
  rows: OverviewServerData[],
  kind: 'cpu' | 'fanRpm' | 'power',
  stat: string,
  offset: number,
) {
  return aggregateDashboard(rows, now)
    [kind].series.find((series) => series.name === stat)
    ?.points.find((point) => Date.parse(point.time) === now + offset);
}

describe('首页跨服务器聚合口径', () => {
  it.each([
    'stale',
    'offline',
    'disabled',
  ] as const)('Dell风扇百分比原始不支持在%s展示状态下仍保留', (state) => {
    const server = row('dell', [
      metric('Fan1', null, {
        metricCode: 'fan.percent',
        unit: '%',
        availability: 'Unsupported',
      }),
    ]);
    if (state === 'offline')
      server.snapshot.server.connectionStatus = 'Offline';
    if (state === 'disabled') server.snapshot.server.enabled = false;
    if (state === 'stale') server.snapshot.telemetry.stale = true;
    const result = aggregateDashboard([server], now).fanSpeedPercent;
    expect(result.validCount).toBe(0);
    expect(result.readings).toEqual([
      expect.objectContaining({ state, availability: 'Unsupported' }),
    ]);
  });

  it('按每颗CPU等权，跨服务器同名CPU保持独立，当前最高不是平均后的最高', () => {
    const result = aggregateDashboard(
      [
        row('a', [metric('CPU1 Temp', 20), metric('CPU2 Temp', 40)]),
        row('b', [metric('CPU1 Temp', 90)]),
      ],
      now,
    ).cpu;
    expect(result.stats.average).toBe(50);
    expect(result.stats.maximum).toBe(90);
    expect(result.validCount).toBe(3);
    expect(new Set(result.readings.map((item) => item.key)).size).toBe(3);
  });
  it('库存补出缺测CPU并显示型号，探头原始编号不当作CPU槽号', () => {
    const result = aggregateDashboard(
      [
        row(
          'a',
          [metric('02-CPU 1', 35, { sensorKey: 'temperature:2' })],
          [
            component('Processor', '/Processors/1', 'Xeon A', {
              Socket: 'Proc 1',
            }),
            component('Processor', '/Processors/2', 'Xeon B', {
              Socket: 'Proc 2',
            }),
          ],
        ),
      ],
      now,
    ).cpu;
    expect(result.totalCount).toBe(2);
    expect(result.validCount).toBe(1);
    expect(result.readings.find((item) => item.slot === '1')).toMatchObject({
      model: 'Xeon A',
      value: 35,
    });
    expect(result.readings.find((item) => item.slot === '2')).toMatchObject({
      model: 'Xeon B',
      value: null,
      state: 'missing',
    });
  });
  it('重复资源取最新微秒版本，同槽多个不同探头不双算、不随便选一个', () => {
    const first = metric('CPU1 Temp', 30, {
      observedAt: '2026-10-25T03:14:59.123455Z',
    });
    const latest = {
      ...first,
      value: 45,
      observedAt: '2026-10-25T03:14:59.123456Z',
    };
    expect(
      aggregateDashboard([row('a', [latest, first])], now).cpu.stats.average,
    ).toBe(45);
    const result = aggregateDashboard(
      [
        row('a', [
          first,
          metric('CPU1 Other', 90, { sensorKey: 'different-CPU1' }),
        ]),
      ],
      now,
    ).cpu;
    expect(result.totalCount).toBe(1);
    expect(result.stats.average).toBeNull();
    expect(result.readings[0].state).toBe('ambiguous');
  });
  it('缺测、禁用、离线、过期、错误单位及不可用非空值不参与，0和负温有效', () => {
    const good = row('good', [
      metric('CPU1 Temp', 0),
      metric('CPU2 Temp', -10),
    ]);
    const stale = row('stale', [metric('CPU1 Temp', 99)]);
    stale.snapshot.telemetry.collectedAt = iso(-91000);
    const offline = row('offline', [metric('CPU1 Temp', 99)]);
    offline.snapshot.server.connectionStatus = 'Offline';
    const disabled = row('disabled', [metric('CPU1 Temp', 99)]);
    disabled.snapshot.server.enabled = false;
    const result = aggregateDashboard(
      [
        good,
        stale,
        offline,
        disabled,
        row('unit', [metric('CPU1 Temp', 212, { unit: '°F' })]),
        row('quality', [
          metric('CPU1 Temp', 99, { availability: 'Unavailable' }),
        ]),
      ],
      now,
    ).cpu;
    expect(result.stats.average).toBe(-5);
    expect(result.stats.maximum).toBe(0);
    expect(result.validCount).toBe(2);
    expect(
      result.readings.find((item) => item.serverId === 'unit')?.state,
    ).toBe('invalid');
    expect(
      result.readings.find((item) => item.serverId === 'disabled')?.state,
    ).toBe('disabled');
  });
  it('快照新鲜仍排除指标自身过期、未来、非法时间及非有限数', () => {
    const result = aggregateDashboard(
      [
        row('a', [
          metric('CPU1 Temp', 20, { observedAt: iso(-91000) }),
          metric('CPU2 Temp', 30, { observedAt: iso(1) }),
          metric('CPU3 Temp', 40, { observedAt: 'not-a-date' }),
          metric('CPU4 Temp', Number.NaN),
        ]),
      ],
      now,
    ).cpu;
    expect(result.validCount).toBe(0);
    expect(result.stats.maximum).toBeNull();
  });
  it('阵列卡按唯一关联或设备明确控制器探头汇总，区域、电容不计入', () => {
    const a = row(
      'a',
      [
        metric('31-HD Controller', 52),
        metric('32-HD Cntlr Zone', 90),
        metric('SuperCAP', 80),
      ],
      [
        component(
          'StorageController',
          'card-p420',
          'Smart Array P420i Controller',
        ),
      ],
    );
    const b = row(
      'b',
      [metric('PERC H730P Temp', 40)],
      [component('StorageController', 'card-h730', 'PERC H730P Mini')],
    );
    const result = aggregateDashboard(
      [a, b, row('c', [metric('PERC H730P Temp', 100)])],
      now,
    ).array;
    expect(result.stats.average).toBe(64);
    expect(result.stats.maximum).toBe(100);
    expect(result.totalCount).toBe(3);
    expect(result.readings.find((item) => item.serverId === 'c')?.counted).toBe(
      true,
    );
  });
  it('CPU补齐最低趋势，进排气分别按实际探头计算最高平均最低且缺测不补零', () => {
    const a = row('a', [
      metric('CPU 1', 40),
      metric('CPU 2', 60),
      metric('01-Inlet Ambient', -2),
      metric('05-Sys Exhaust', 30),
      metric('06-Sys Exhaust', 50),
      metric('P/S 1 Inlet', 99),
    ]);
    const b = row('b', [
      metric('System Board Inlet Temp', 22),
      metric('System Board Exhaust Temp', null),
    ]);
    for (const server of [a, b])
      server.history = server.snapshot.telemetry.metrics.map((sensor) =>
        history(sensor, [[-30000, sensor.value]]),
      );
    const result = aggregateDashboard([a, b], now);
    expect(result.cpu.series.map((series) => series.name)).toEqual([
      'maximum',
      'average',
      'minimum',
    ]);
    expect(
      result.cpu.series
        .find((series) => series.name === 'minimum')
        ?.points.find((point) => Date.parse(point.time) === now - 30000)
        ?.average,
    ).toBe(40);
    expect(result.inlet.stats).toMatchObject({
      maximum: 22,
      average: 10,
      minimum: -2,
    });
    expect(result.inlet.totalCount).toBe(2);
    expect(result.exhaust.stats).toMatchObject({
      maximum: 50,
      average: 40,
      minimum: 30,
    });
    expect(result.exhaust.totalCount).toBe(3);
    expect(result.exhaust.validCount).toBe(2);
    expect(result.exhaust.readings.map((reading) => reading.label)).toEqual([
      '05-Sys Exhaust',
      '06-Sys Exhaust',
      'System Board Exhaust Temp',
    ]);
    const historical = aggregateHistory([a, b], 'exhaust', {
      from: iso(-60000),
      to: iso(),
      bucketSeconds: 30,
    });
    expect(historical.series.map((series) => series.name)).toEqual([
      'maximum',
      'average',
      'minimum',
    ]);
    expect(
      historical.series
        .find((series) => series.name === 'average')
        ?.points.find((point) => Date.parse(point.time) === now - 30000)
        ?.average,
    ).toBe(40);
  });
  it('相同型号多块阵列卡无法唯一匹配时不复制探头', () => {
    const result = aggregateDashboard(
      [
        row(
          'a',
          [metric('PERC H730P Temp', 40)],
          [
            component('StorageController', 'raid-a', 'PERC H730P Mini'),
            component('StorageController', 'raid-b', 'PERC H730P Mini'),
          ],
        ),
      ],
      now,
    ).array;
    expect(result.totalCount).toBe(2);
    expect(result.validCount).toBe(0);
  });
  it('每个风扇等权，所有服务器统一百分比，百分比不转换RPM', () => {
    const result = aggregateDashboard(
      [
        row('a', [rpm('Fan 1', 1000), rpm('Fan 2', 2000)]),
        row('b', [rpm('Fan 1', 6000)]),
        row('hp', [
          metric('Fan 1', 30, { metricCode: 'fan.percent', unit: '%' }),
          metric('Fan 2', 40, { metricCode: 'fan.percent', unit: '%' }),
        ]),
      ],
      now,
    );
    expect(result.fanRpm.stats.average).toBe(3000);
    expect(result.fanRpm.stats.minimum).toBe(1000);
    expect(result.fanRpm.stats.maximum).toBe(6000);
    expect(result.fanRpm.totalCount).toBe(5);
    expect(result.fanRpm.validCount).toBe(3);
    expect(result.fanSpeedPercent.stats.average).toBe(35);
    expect(result.fanSpeedPercent.validCount).toBe(2);
    expect(result).not.toHaveProperty('fanPwm');
  });
  it('六个当前风扇不会因旧的空系列、纯空值或无效样本元数据变成更多实体', () => {
    const s1 = row(
      's1',
      Array.from({ length: 6 }, (_, index) => rpm(`Fan ${index + 1}`, 5640)),
    );
    const empty = history(
      rpm('风扇转速', null, { sensorKey: 'legacy:aggregate' }),
      [],
    );
    const nullOnly = history(
      rpm('Retired fan', null, { sensorKey: 'legacy:retired' }),
      [[-40000, null]],
    );
    const invalid = history(rpm('Invalid fan', null), [
      [-40000, Number.POSITIVE_INFINITY],
    ]);
    const zeroCount = history(rpm('Zero count fan', null), [[-40000, 100]]);
    zeroCount.points[0].count = 0;
    const outsideWindow = history(rpm('Old fan', null), [[-900001, 5000]]);
    s1.history = [empty, nullOnly, invalid, zeroCount, outsideWindow];
    const result = aggregateDashboard([s1], now).fanRpm;
    expect(result.totalCount).toBe(6);
    expect(result.validCount).toBe(6);
    expect(result.readings).toHaveLength(6);
    expect(result.stats.average).toBe(5640);
    expect(
      result.readings.some((reading) => reading.label === '风扇转速'),
    ).toBe(false);
  });
  it('近期存在真实历史值的暂失风扇仍被发现，历史0也是有效的实体证据', () => {
    const s1 = row('s1', [rpm('Fan 1', 5640)]);
    s1.history = [
      history(rpm('Fan 2', 5500), [[-40000, 5500]]),
      history(rpm('Fan 3', 0), [[-40000, 0]]),
    ];
    const result = aggregateDashboard([s1], now).fanRpm;
    expect(result.totalCount).toBe(3);
    expect(result.validCount).toBe(1);
    expect(
      result.readings.filter((reading) => reading.state === 'missing'),
    ).toHaveLength(2);
    expect(result.stats.average).toBe(5640);
  });
  it('当前不支持或离线读数仍保留实体，不因没有有效历史样本而隐藏', () => {
    const unsupported = row('unsupported', [
      rpm('Fan 1', null, { availability: 'Unsupported' }),
    ]);
    const offline = row('offline', [rpm('Fan 1', 5000)]);
    offline.snapshot.server.connectionStatus = 'Offline';
    const result = aggregateDashboard([unsupported, offline], now).fanRpm;
    expect(result.totalCount).toBe(2);
    expect(result.validCount).toBe(0);
    expect(result.readings.map((reading) => reading.state)).toEqual([
      'unsupported',
      'offline',
    ]);
  });
  it('空历史探头不制造CPU槽位歧义或阻碍阵列卡唯一关联', () => {
    const source = row(
      'a',
      [metric('CPU1 Temp', 40), metric('31-HD Controller', 52)],
      [
        component(
          'StorageController',
          'card-p420',
          'Smart Array P420i Controller',
        ),
      ],
    );
    source.history = [
      history(metric('CPU1 Other', null, { sensorKey: 'retired:cpu' }), []),
      history(
        metric('HD Controller', null, { sensorKey: 'retired:controller' }),
        [[-40000, null]],
      ),
    ];
    const result = aggregateDashboard([source], now);
    expect(result.cpu.totalCount).toBe(1);
    expect(result.cpu.validCount).toBe(1);
    expect(result.cpu.stats.average).toBe(40);
    expect(result.array.totalCount).toBe(1);
    expect(result.array.validCount).toBe(1);
    expect(result.array.stats.average).toBe(52);
  });
  it('0 RPM与0 W有效，负转速/负功率/越界百分比排除', () => {
    const result = aggregateDashboard(
      [
        row('a', [
          rpm('fan0', 0),
          rpm('fan-negative', -1),
          watts(0),
          metric('fan-bad-percent', 101, {
            metricCode: 'fan.percent',
            unit: '%',
          }),
        ]),
        row('b', [watts(-2)]),
      ],
      now,
    );
    expect(result.fanRpm.stats.average).toBe(0);
    expect(result.fanSpeedPercent.validCount).toBe(0);
    expect(result.power.stats.total).toBe(0);
    expect(result.power.validCount).toBe(1);
  });
  it('功率每台只取system实际瓦数，容量、峰值、电源单元不混入，缺测保留分母', () => {
    const result = aggregateDashboard(
      [
        row('a', [
          watts(100),
          metric('capacity', 1000, {
            metricCode: 'power.capacity.watts',
            unit: 'W',
          }),
          watts(900, { sensorKey: 'PSU1' }),
        ]),
        row('b', [watts(200)]),
        row('c', []),
      ],
      now,
    ).power;
    expect(result.stats).toEqual({
      average: 150,
      minimum: 100,
      maximum: 200,
      total: 300,
    });
    expect(result.totalCount).toBe(3);
    expect(result.validCount).toBe(2);
  });
  it('逐台功率压力按设备容量计算，0 W有效，超出容量保留实际百分比', () => {
    const result = aggregateDashboard(
      [
        row('normal', [watts(150), capacity(500)]),
        row('idle', [watts(0), capacity(460)]),
        row('over', [watts(600), capacity(500)]),
      ],
      now,
    ).power;
    expect(result.stats.total).toBe(750);
    expect(result.pressureRows).toEqual([
      expect.objectContaining({
        serverId: 'normal',
        usedWatts: 150,
        capacityWatts: 500,
        percent: 30,
        reason: null,
      }),
      expect.objectContaining({
        serverId: 'idle',
        usedWatts: 0,
        capacityWatts: 460,
        percent: 0,
        reason: null,
      }),
      expect.objectContaining({
        serverId: 'over',
        usedWatts: 600,
        capacityWatts: 500,
        percent: 120,
        reason: null,
      }),
    ]);
  });
  it('容量缺测或无效时保留运行功率但不虚构压力或以PSU容量替代', () => {
    const result = aggregateDashboard(
      [
        row('missing', [watts(100)]),
        row('zero', [watts(100), capacity(0)]),
        row('negative', [watts(100), capacity(-500)]),
        row('unit', [watts(100), capacity(500, { unit: 'kW' })]),
        row('old', [watts(100), capacity(500, { observedAt: iso(-91000) })]),
        row('unavailable', [
          watts(100),
          capacity(500, { availability: 'TemporarilyUnavailable' }),
        ]),
        row('psu-only', [watts(100), capacity(500, { sensorKey: 'psu:1' })]),
      ],
      now,
    ).power;
    expect(result.stats.total).toBe(700);
    expect(result.pressureRows).toHaveLength(7);
    for (const pressure of result.pressureRows || [])
      expect(pressure).toMatchObject({
        usedWatts: 100,
        capacityWatts: null,
        percent: null,
        reason: 'capacity',
      });
  });
  it('离线、快照过期及运行功率不可用时不沿用旧压力', () => {
    const offline = row('offline', [watts(100), capacity(500)]);
    offline.snapshot.server.connectionStatus = 'Offline';
    const stale = row('stale', [watts(100), capacity(500)]);
    stale.snapshot.telemetry.collectedAt = iso(-91000);
    const missing = row('missing', [capacity(500)]);
    const result = aggregateDashboard([offline, stale, missing], now).power;
    expect(result.stats.total).toBeNull();
    expect(result.pressureRows).toHaveLength(3);
    for (const pressure of result.pressureRows || [])
      expect(pressure).toMatchObject({
        usedWatts: null,
        capacityWatts: null,
        percent: null,
        reason: 'power',
      });
  });
  it('未发现实体仅给提示行，不虚构设备分母和0读数', () => {
    const result = aggregateDashboard([row('a', [])], now);
    expect(result.cpu.totalCount).toBe(0);
    expect(result.cpu.readings).toHaveLength(1);
    expect(result.cpu.readings[0]).toMatchObject({
      counted: false,
      value: null,
    });
    expect(result.cpu.stats.average).toBeNull();
    expect(result.power.totalCount).toBe(1);
  });
});

describe('最近15分钟30秒真实采样聚合', () => {
  it('不同服务器异步样本对齐到同桶，每颗CPU取桶内最后真实值', () => {
    const a = row('a', [metric('CPU1 Temp', 20)]),
      b = row('b', [metric('CPU1 Temp', 60)]);
    a.history = [
      history(a.snapshot.telemetry.metrics[0], [
        [-59000, 10],
        [-40000, 20],
      ]),
    ];
    b.history = [history(b.snapshot.telemetry.metrics[0], [[-35000, 60]])];
    const result = aggregateDashboard([a, b], now).cpu;
    expect(valueAt([a, b], 'cpu', 'average', -60000)).toMatchObject({
      average: 40,
      count: 2,
    });
    expect(result.coverage[now - 60000]).toEqual({
      validCount: 2,
      totalCount: 2,
      serverNames: ['Server a', 'Server b'],
    });
  });
  it('桶内最后空值覆盖此前有效值，不用旧值掩盖失败', () => {
    const a = row('a', [metric('CPU1 Temp', 40)]);
    a.history = [
      history(a.snapshot.telemetry.metrics[0], [
        [-59000, 40],
        [-40000, null],
      ]),
    ];
    expect(valueAt([a], 'cpu', 'average', -60000)).toMatchObject({
      average: null,
      count: 0,
    });
  });
  it('不前值填充，当前离线不抹掉此前成功采样的历史', () => {
    const a = row('a', [metric('CPU1 Temp', 40)]);
    a.snapshot.server.connectionStatus = 'Offline';
    a.history = [history(a.snapshot.telemetry.metrics[0], [[-59000, 40]])];
    expect(aggregateDashboard([a], now).cpu.stats.average).toBeNull();
    expect(valueAt([a], 'cpu', 'average', -60000)?.average).toBe(40);
    expect(valueAt([a], 'cpu', 'average', -30000)?.average).toBeNull();
  });
  it('零时长故障也断开曲线，同桶已有有效点不能掩盖故障', () => {
    const a = row('a', [rpm('Fan1', 5000)]);
    a.history = [
      history(a.snapshot.telemetry.metrics[0], [
        [-59000, 5000],
        [-29000, 5100],
      ]),
    ];
    a.gaps = [{ from: now - 40000, to: now - 40000 }];
    const result = aggregateDashboard([a], now).fanRpm;
    expect(
      result.series[0].points.find(
        (point) => Date.parse(point.time) === now - 40000,
      )?.average,
    ).toBeNull();
    expect(valueAt([a], 'fanRpm', 'average', -60000)?.average).toBeNull();
    expect(valueAt([a], 'fanRpm', 'average', -30000)?.average).toBe(5100);
  });
  it('只有百分比的HP服务器故障不打断其他服务器RPM曲线', () => {
    const dell = row('dell', [rpm('Fan1', 5000)]);
    dell.history = [
      history(dell.snapshot.telemetry.metrics[0], [[-59000, 5000]]),
    ];
    const hp = row('hp', [
      metric('Fan1', 30, { metricCode: 'fan.percent', unit: '%' }),
    ]);
    hp.history = [history(hp.snapshot.telemetry.metrics[0], [[-59000, 30]])];
    hp.gaps = [{ from: now - 40000, to: now - 40000 }];
    const result = aggregateDashboard([dell, hp], now);
    expect(
      result.fanRpm.series[0].points.some(
        (point) => Date.parse(point.time) === now - 40000,
      ),
    ).toBe(false);
    expect(valueAt([dell, hp], 'fanRpm', 'average', -60000)?.average).toBe(
      5000,
    );
    expect(
      result.fanSpeedPercent.series[0].points.find(
        (point) => Date.parse(point.time) === now - 40000,
      )?.average,
    ).toBeNull();
  });
  it('功率来源服务器集合即使数量不变也断开，保留可用读数部分合计', () => {
    const a = row('a', [watts(100)]),
      b = row('b', [watts(200)]);
    a.history = [history(a.snapshot.telemetry.metrics[0], [[-59000, 100]])];
    b.history = [history(b.snapshot.telemetry.metrics[0], [[-29000, 200]])];
    const result = aggregateDashboard([a, b], now).power;
    expect(
      result.series
        .find((series) => series.name === 'total')
        ?.points.find((point) => Date.parse(point.time) === now - 30001)
        ?.average,
    ).toBeNull();
    expect(valueAt([a, b], 'power', 'total', -30000)?.average).toBe(200);
    expect(result.coverage[now - 30000]).toEqual({
      validCount: 1,
      totalCount: 2,
      serverNames: ['Server b'],
    });
  });
  it('忽略窗口外、未来、错误单位样本，不借用当前快照填历史', () => {
    const a = row('a', [metric('CPU1 Temp', 40)]);
    a.history = [
      history(a.snapshot.telemetry.metrics[0], [
        [-900001, 90],
        [1, 90],
      ]),
      history({ ...a.snapshot.telemetry.metrics[0], unit: '°F' }, [
        [-1000, 90],
      ]),
    ];
    expect(
      aggregateDashboard([a], now).cpu.series.every((series) =>
        series.points.every((point) => point.average === null),
      ),
    ).toBe(true);
  });
  it('同桶相邻微秒点按实际先后而非输入顺序选择最后点', () => {
    const a = row('a', [metric('CPU1 Temp', 40)]),
      series = history(metric('CPU1 Temp', 40), []);
    series.points = [
      {
        time: '2026-10-25T03:14:20.123456Z',
        average: 45,
        minimum: 45,
        maximum: 45,
        count: 1,
      },
      {
        time: '2026-10-25T03:14:20.123455Z',
        average: 20,
        minimum: 20,
        maximum: 20,
        count: 1,
      },
    ];
    a.history = [series];
    expect(valueAt([a], 'cpu', 'average', -60000)?.average).toBe(45);
  });
});

describe('自选区间历史桶聚合', () => {
  it('一月前的阵列卡温度沿用库存匹配，区域探头不混入且末尾部分桶保留', () => {
    const source = row(
      'array',
      [],
      [
        component(
          'StorageController',
          'card-p420',
          'Smart Array P420i Controller',
        ),
      ],
    );
    const first = -31 * 86400000 - 900000;
    source.history = [
      history(metric('31-HD Controller', 52), [
        [first, 52],
        [-900000, 40],
      ]),
      history(metric('32-HD Cntlr Zone', 90), [[first, 90]]),
    ];
    const result = aggregateHistory([source], 'array', {
      from: iso(-31 * 86400000),
      to: iso(),
      bucketSeconds: 3600,
    });
    expect(result.totalCount).toBe(1);
    expect(result.series[0].points).toHaveLength(745);
    expect(result.series[0].points[0]).toMatchObject({ average: 52 });
    expect(result.series[0].points.at(-1)).toMatchObject({ average: 40 });
    expect(result.series[0].points[1]).toMatchObject({ average: null });
  });

  it('自选长区间遵守后端桶数上限，非法范围不生成曲线', () => {
    const window = {
      from: iso(-366 * 86400000),
      to: iso(),
      bucketSeconds: 1,
    };
    const result = aggregateHistory([row('missing', [])], 'power', window);
    expect(result.series[0].points.length).toBeLessThanOrEqual(1001);
    expect(result.series[0].points.length).toBeGreaterThan(990);
    expect(
      result.series[0].points.every((point) => point.average === null),
    ).toBe(true);
    expect(
      aggregateHistory([], 'power', { ...window, to: window.from }).series,
    ).toEqual([]);
  });

  it('保留一天前的实体、第一部分桶和真实零，结束边界及区间外桶不计入', () => {
    const source = row('old', []);
    source.history = [
      history(rpm('Historical fan', null), [
        [-86700000, 9999],
        [-86400000, 0],
        [-86100000, 3000],
        [0, 9999],
      ]),
    ];
    const result = aggregateHistory([source], 'fanRpm', {
      from: iso(-86400000 + 90000),
      to: iso(),
      bucketSeconds: 300,
    });
    const points = result.series[0].points;
    expect(result.totalCount).toBe(1);
    expect(points).toHaveLength(288);
    expect(points[0]).toMatchObject({
      time: iso(-86400000),
      average: 0,
      count: 1,
    });
    expect(points[1]).toMatchObject({ average: 3000, count: 1 });
    expect(points.at(-1)).toMatchObject({ average: null, count: 0 });
    expect(points.some((point) => point.average === 9999)).toBe(false);
    expect(result.coverage[now - 86400000]).toEqual({
      validCount: 1,
      totalCount: 1,
      serverNames: ['Server old'],
    });
  });

  it('当前离线、禁用、过期和实时故障不抹掉历史，也不混入当前新探头', () => {
    const source = row('old', [
      metric('CPU1 Current', 999, { sensorKey: 'new-CPU1' }),
    ]);
    source.snapshot.server.connectionStatus = 'Offline';
    source.snapshot.server.enabled = false;
    source.snapshot.telemetry.stale = true;
    source.gaps = [{ from: now - 86400000, to: now }];
    source.history = [
      history(metric('CPU1 Historical', 40, { sensorKey: 'old-CPU1' }), [
        [-86400000, 40],
      ]),
    ];
    const result = aggregateHistory([source], 'cpu', {
      from: iso(-86400000),
      to: iso(),
      bucketSeconds: 300,
    });
    expect(result.totalCount).toBe(1);
    expect(result.series[0].points[0]).toMatchObject({
      average: 40,
      count: 1,
    });
    expect(result.series[0].points[1]).toMatchObject({
      average: null,
      count: 0,
    });
    expect(result.readings).toEqual([]);
    expect(result.stats.average).toBeNull();
  });

  it('空桶不前值填充，空元数据、错误单位及零数量不产生历史读数', () => {
    const source = row('empty', [watts(999)]);
    const emptyCount = history(watts(100), [[-600000, 100]]);
    emptyCount.points[0].count = 0;
    source.history = [
      history(watts(50), [[-900000, 50]]),
      emptyCount,
      history(watts(900, { unit: 'kW' }), [[-300000, 900]]),
    ];
    const result = aggregateHistory([source], 'power', {
      from: iso(-900000),
      to: iso(),
      bucketSeconds: 300,
    });
    const points = result.series[0].points;
    expect(points.find((point) => point.time === iso(-900000))).toMatchObject({
      average: 50,
      count: 1,
    });
    expect(points.find((point) => point.time === iso(-600000))).toMatchObject({
      average: null,
      count: 0,
    });
    expect(points.find((point) => point.time === iso(-300000))).toMatchObject({
      average: null,
      count: 0,
    });
  });

  it('总功率保留部分来源合计和覆盖数，参与服务器变化时插入断点', () => {
    const a = row('a', []),
      b = row('b', []);
    a.history = [
      history(watts(100), [
        [-900000, 100],
        [-600000, 0],
      ]),
    ];
    b.history = [
      history(watts(200), [
        [-900000, 200],
        [-300000, 200],
      ]),
    ];
    const result = aggregateHistory([a, b, row('missing', [])], 'power', {
      from: iso(-900000),
      to: iso(),
      bucketSeconds: 300,
    });
    const total = result.series.find((series) => series.name === 'total');
    expect(total?.points.map((point) => [point.time, point.average])).toEqual([
      [iso(-900000), 300],
      [iso(-600001), null],
      [iso(-600000), 0],
      [iso(-300001), null],
      [iso(-300000), 200],
    ]);
    expect(result.coverage[now - 600000]).toEqual({
      validCount: 1,
      totalCount: 3,
      serverNames: ['Server a'],
    });
    expect(result.coverage[now - 300000].serverNames).toEqual(['Server b']);
  });
});
