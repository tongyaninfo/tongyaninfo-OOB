import { describe, expect, it } from 'vitest';
import type {
  DeviceInventory,
  HardwareComponent,
  MetricReading,
} from '@/services/oob/models';
import { controllerTemperatures } from './controllers';

function card(
  key: string,
  model: string | null,
  extra: Partial<HardwareComponent> = {},
): HardwareComponent {
  return {
    key,
    kind: 'StorageController',
    name: model || '阵列控制器',
    model,
    manufacturer: null,
    serialNumber: null,
    status: null,
    capacityBytes: null,
    attributes: {},
    children: [],
    ...extra,
  };
}
function inventory(components: HardwareComponent[]): DeviceInventory {
  return {
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
    collectedAt: '2026-10-25T00:00:00Z',
    components,
    warnings: [],
  };
}
function temperature(
  name: string,
  value: number | null = 52,
  sensorKey = name,
): MetricReading {
  return {
    sensorKey,
    name,
    metricCode: 'temperature.celsius',
    value,
    unit: '°C',
    availability: value === null ? 'Unsupported' : 'Available',
    source: 'Device',
    health: null,
    observedAt: '2026-10-25T00:00:00Z',
    deviceTime: null,
  };
}

describe('阵列型号与温度的可核对关联', () => {
  it('真实HP单卡关联实体温度，以库存型号展示且不误用区域或电容', () => {
    const metric = temperature('31-HD Controller');
    const result = controllerTemperatures(
      inventory([
        card(
          '/redfish/v1/Systems/1/SmartStorage/ArrayControllers/0/',
          'Smart Array P420i Controller',
        ),
      ]),
      [
        temperature('02-CPU 1', 40),
        metric,
        temperature('32-HD Cntlr Zone', 34),
        temperature('42-SuperCAP Max', 19),
      ],
    );
    expect(result).toEqual([
      {
        key: '/redfish/v1/Systems/1/SmartStorage/ArrayControllers/0/',
        model: 'Smart Array P420i Controller',
        name: 'Smart Array P420i Controller',
        temperature: metric,
      },
    ]);
  });
  it('真实Dell仅显示PERC，排除两个AHCI，缺少温度保持空值', () => {
    const result = controllerTemperatures(
      inventory([
        card('/StorageControllers/RAID.Integrated.1-1', 'PERC H730P Mini'),
        card(
          '/StorageControllers/AHCI.Embedded.1-1',
          'C610/X99 series chipset sSATA Controller [AHCI mode]',
        ),
        card(
          '/StorageControllers/AHCI.Embedded.2-1',
          'C610/X99 series chipset 6-Port SATA Controller [AHCI mode]',
        ),
      ]),
      [temperature('CPU1 Temp', 30)],
    );
    expect(result).toHaveLength(1);
    expect(result[0]).toMatchObject({
      model: 'PERC H730P Mini',
      temperature: undefined,
    });
  });
  it('多卡分别按明确型号码关联，顺序与传感器输入顺序无关', () => {
    const p420 = temperature('P420i RAID Temp', 0),
      h730 = temperature('PERC H730P Temperature', 41);
    const result = controllerTemperatures(
      inventory([
        card('raid-h730', 'PERC H730P Mini'),
        card('raid-p420', 'Smart Array P420i Controller'),
      ]),
      [p420, h730],
    );
    expect(result.find((item) => item.key === 'raid-p420')?.temperature).toBe(
      p420,
    );
    expect(result.find((item) => item.key === 'raid-h730')?.temperature).toBe(
      h730,
    );
  });
  it('相同型号的多卡按完整资源引用分别关联，不因1-1前缀误匹配1-10', () => {
    const first = temperature(
      'RAID Temperature',
      39,
      'temperature:/StorageControllers/RAID.Integrated.1-1',
    );
    const tenth = temperature(
      'RAID Temperature',
      43,
      'temperature:/StorageControllers/RAID.Integrated.1-10',
    );
    const result = controllerTemperatures(
      inventory([
        card('/StorageControllers/RAID.Integrated.1-10', 'PERC H730P Mini'),
        card('/StorageControllers/RAID.Integrated.1-1', 'PERC H730P Mini'),
      ]),
      [first, tenth],
    );
    expect(result.find((item) => item.key.endsWith('1-1'))?.temperature).toBe(
      first,
    );
    expect(result.find((item) => item.key.endsWith('1-10'))?.temperature).toBe(
      tenth,
    );
  });
  it('多卡对匿名温度、重复型号温度不猜对应，不把同一个读数复制给每张卡', () => {
    const cards = inventory([
      card('card-1', 'PERC H730P Mini'),
      card('card-2', 'PERC H730P Mini'),
    ]);
    expect(
      controllerTemperatures(cards, [temperature('31-HD Controller')]).every(
        (item) => item.temperature === undefined,
      ),
    ).toBe(true);
    expect(
      controllerTemperatures(cards, [
        temperature('PERC H730P Temperature'),
      ]).every((item) => item.temperature === undefined),
    ).toBe(true);
  });
  it('单卡出现多个实体探头时不随意挑一个，区域不能充当卡实体温度', () => {
    const cards = inventory([card('card-1', 'Smart Array P420i Controller')]);
    expect(
      controllerTemperatures(cards, [
        temperature('HD Controller 1'),
        temperature('HD Controller 2'),
      ])[0].temperature,
    ).toBeUndefined();
    expect(
      controllerTemperatures(cards, [temperature('HD Cntlr Zone')])[0]
        .temperature,
    ).toBeUndefined();
  });
  it('单卡回退不采用明确其它型号的探头，也不把子资源路径当成卡资源引用', () => {
    const cards = inventory([
      card('/StorageControllers/RAID.1', 'Smart Array P420i Controller'),
    ]);
    expect(
      controllerTemperatures(cards, [temperature('PERC H740 Temperature')])[0]
        .temperature,
    ).toBeUndefined();
    expect(
      controllerTemperatures(cards, [
        temperature(
          'HD Controller',
          42,
          'temperature:/StorageControllers/RAID.1/OtherSensor',
        ),
      ])[0].temperature,
    ).toBeUndefined();
  });
  it('递归查找控制器并按资源键去重，型号缺失显示短横线而不把通用名称当型号', () => {
    const controller = card('card-1', null);
    const group = card('group', 'Group', {
      kind: 'Storage',
      children: [controller],
    });
    expect(controllerTemperatures(inventory([group, controller]), [])).toEqual([
      { key: 'card-1', model: '-', name: '阵列控制器', temperature: undefined },
    ]);
  });
  it('无库存时保留明确控制器探头的原名与身份，不猜型号，不混入区域盘温电容', () => {
    const sensor = temperature('31-HD Controller', 53, 'temperature:31');
    const result = controllerTemperatures(undefined, [
      sensor,
      sensor,
      temperature('32-HD Cntlr Zone', 35),
      temperature('RAID Disk 1', 40),
      temperature('PERC Battery', 32),
      temperature('RAID SuperCAP', 35),
      temperature('CPU1 Temp', 50),
    ]);
    expect(result).toEqual([
      {
        key: JSON.stringify(['temperature:31', 'temperature.celsius', '°C']),
        name: '31-HD Controller',
        model: '-',
        temperature: sensor,
      },
    ]);
  });
});
