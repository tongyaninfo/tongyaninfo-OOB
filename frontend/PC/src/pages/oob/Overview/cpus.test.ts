import { describe, expect, it } from 'vitest';
import type {
  DeviceInventory,
  HardwareComponent,
  MetricReading,
} from '@/services/oob/models';
import { cpuTemperatureLabels } from './cpus';
import { sensorIdentity } from './metrics';

function processor(
  key: string,
  socket: string | undefined,
  model: string | null,
  extra: Partial<HardwareComponent> = {},
): HardwareComponent {
  return {
    key,
    kind: 'Processor',
    name: 'Processors',
    model,
    manufacturer: null,
    serialNumber: null,
    status: 'OK',
    capacityBytes: null,
    attributes: socket ? { Socket: socket } : {},
    children: [],
    ...extra,
  };
}
function inventory(components: HardwareComponent[]): DeviceInventory {
  return {
    components,
    collectedAt: '2026-10-25T00:00:00Z',
    warnings: [],
    identity: {
      manufacturer: null,
      model: null,
      serialNumber: null,
      hostName: null,
      controllerHostName: null,
      controllerType: null,
      firmwareVersion: null,
      powerState: null,
    },
  };
}
function sensor(name: string, sensorKey = name): MetricReading {
  return {
    name,
    sensorKey,
    metricCode: 'temperature.celsius',
    unit: '°C',
    value: 40,
    availability: 'Available',
    source: 'Device',
    health: null,
    observedAt: '2026-10-25T00:00:00Z',
    deviceTime: null,
  };
}
const model = 'Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz';
describe('CPU库存型号与温度探头的明确槽号映射', () => {
  it('HP按Proc槽号和CPU名称匹配，忽略temperature:2传感器编号并修剪型号空白', () => {
    const first = sensor('02-CPU 1', 'temperature:2');
    const second = sensor('03-CPU 2', 'temperature:3');
    const saved = inventory([
      processor('/redfish/v1/Systems/1/Processors/2/', 'Proc 2', ` ${model} `),
      processor('/redfish/v1/Systems/1/Processors/1/', 'Proc 1', ` ${model} `),
    ]);
    const labels = cpuTemperatureLabels(saved, [second, first]);
    expect(labels.get(sensorIdentity(first))).toEqual({
      label: model,
      legend: `${model} (CPU 1)`,
      slot: '1',
      order: 0,
    });
    expect(labels.get(sensorIdentity(second))).toEqual({
      label: model,
      legend: `${model} (CPU 2)`,
      slot: '2',
      order: 1,
    });
    expect(
      cpuTemperatureLabels(inventory([...saved.components].reverse()), [
        first,
        second,
      ]),
    ).toEqual(labels);
  });
  it('不同槽型号明确对应，绝不按BMC返回顺序或型号排序猜测', () => {
    const first = sensor('CPU1 Temp'),
      second = sensor('CPU2 Temp');
    const labels = cpuTemperatureLabels(
      inventory([
        processor('/Processors/CPU.Socket.2', 'CPU.Socket.2', 'Model A'),
        processor('/Processors/CPU.Socket.1', 'CPU.Socket.1', 'Model Z'),
      ]),
      [second, first],
    );
    expect(labels.get(sensorIdentity(first))?.label).toBe('Model Z');
    expect(labels.get(sensorIdentity(second))?.label).toBe('Model A');
    expect(labels.get(sensorIdentity(first))?.order).toBe(0);
  });
  it('Dell完整CPU.Socket资源与#CPU1Temp匹配，同型号仍保留独立图例且不暴露资源键', () => {
    const first = sensor('CPU1 Temp', 'temperature:iDRAC.Embedded.1#CPU1Temp');
    const second = sensor('CPU2 Temp', 'temperature:iDRAC.Embedded.1#CPU2Temp');
    const labels = cpuTemperatureLabels(
      inventory([
        processor(
          '/redfish/v1/Systems/System.Embedded.1/Processors/CPU.Socket.2',
          'CPU.Socket.2',
          model,
          { name: 'CPU 2' },
        ),
        processor(
          '/redfish/v1/Systems/System.Embedded.1/Processors/CPU.Socket.1',
          'CPU.Socket.1',
          model,
          { name: 'CPU 1' },
        ),
      ]),
      [second, first],
    );
    expect(
      new Set([...labels.values()].map((value) => value.legend)).size,
    ).toBe(2);
    expect(labels.get(sensorIdentity(first))?.legend).toBe(`${model} (CPU 1)`);
    expect(
      [...labels.values()].every(
        (value) =>
          !value.legend.includes('temperature:') &&
          !value.legend.includes('/redfish'),
      ),
    ).toBe(true);
  });
  it('S1单CPU仅显示完整型号，CPU1Temp连续名称也能确定槽号', () => {
    const first = sensor('CPU1Temp', 'temperature:iDRAC.Embedded.1#CPU1Temp');
    const labels = cpuTemperatureLabels(
      inventory([processor('/Processors/CPU.Socket.1', 'CPU.Socket.1', model)]),
      [first],
    );
    expect(labels.get(sensorIdentity(first))).toMatchObject({
      label: model,
      legend: model,
      slot: '1',
    });
  });
  it('缺库存、缺型号、冲突槽号、重复槽位、汇总记录均回退通用CPU序号，不拿名称冒充型号', () => {
    const first = sensor('02-CPU 1', 'temperature:2');
    const cases = [
      undefined,
      inventory([processor('/Processors/1', 'Proc 1', null)]),
      inventory([processor('/Processors/2', 'Proc 1', model)]),
      inventory([
        processor('resource-a', 'Proc 1', model),
        processor('resource-b', 'Proc 1', null),
      ]),
      inventory([
        processor('/Processors/1', 'Proc 1', model, {
          kind: 'ProcessorSummary',
        }),
      ]),
    ];
    for (const saved of cases)
      expect(
        cpuTemperatureLabels(saved, [first]).get(sensorIdentity(first))?.label,
      ).toBe('CPU 1');
  });
  it('CPU1与CPU10不做前缀匹配，递归库存和重复资源不会增加CPU权重', () => {
    const tenth = sensor(
      'CPU10 Temp',
      'temperature:iDRAC.Embedded.1#CPU10Temp',
    );
    const child = processor(
      '/Processors/CPU.Socket.10',
      'CPU.Socket.10',
      'Model 10',
    );
    const saved = inventory([
      processor('group', undefined, null, {
        kind: 'Chassis',
        children: [child],
      }),
      child,
      processor('/Processors/CPU.Socket.1', 'CPU.Socket.1', 'Model 1'),
    ]);
    expect(
      cpuTemperatureLabels(saved, [tenth]).get(sensorIdentity(tenth))?.label,
    ).toBe('Model 10');
  });
  it('矛盾探头只使用稳定通用序号，不采纳任一冲突编号对应型号', () => {
    const first = sensor('CPU1 Temp', 'temperature:#CPU2Temp');
    const labels = cpuTemperatureLabels(
      inventory([
        processor('/Processors/1', 'Proc 1', model),
        processor('/Processors/2', 'Proc 2', model),
      ]),
      [first],
    );
    expect(labels.get(sensorIdentity(first))).toMatchObject({
      label: 'CPU 1',
      slot: undefined,
    });
  });
});
