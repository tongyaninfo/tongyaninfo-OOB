import { describe, expect, it } from 'vitest';
import enHardware from '@/locales/en-US/hardware';
import cnHardware from '@/locales/zh-CN/hardware';
import twHardware from '@/locales/zh-TW/hardware';
import {
  inventoryAttributeKeys,
  inventoryAttributeLabel,
  inventoryKindLabel,
  inventoryStatusLabel,
  visibleInventoryAttributes,
} from './inventoryLabels';

describe('硬件缓存的三语言展示映射', () => {
  it('97个已核对业务字段在三种语言均有展示名，完整OEM路径独立映射', () => {
    expect(inventoryAttributeKeys).toHaveLength(97);
    for (const key of inventoryAttributeKeys)
      for (const locale of ['zh-CN', 'zh-TW', 'en-US'])
        expect(inventoryAttributeLabel(key, locale)).toBeTruthy();
    expect(inventoryAttributeLabel('Oem.Hp.ConfigStatus.State', 'zh-CN')).toBe(
      'HP 处理器配置状态',
    );
    expect(
      inventoryAttributeLabel('Oem.Hp.PowerSupplyStatus.State', 'zh-CN'),
    ).toBe('HP 电源状态');
    expect(inventoryAttributeLabel('Manufacturer', 'zh-TW')).toBe('製造商');
  });
  it('6个类型、5种状态已映射；未来未知属性和状态保留原文', () => {
    for (const kind of [
      'Battery',
      'Drive',
      'Memory',
      'PowerSupply',
      'Processor',
      'StorageController',
    ])
      expect(inventoryKindLabel(kind, 'zh-CN')).not.toBe(kind);
    for (const status of ['Enabled', 'GoodInUse', 'OK', 'Present', 'Warning'])
      expect(inventoryStatusLabel(status, 'zh-CN')).not.toBe(status);
    expect(inventoryAttributeLabel('Future.VendorField', 'zh-CN')).toBe(
      'Future.VendorField',
    );
    expect(inventoryStatusLabel('VendorState', 'zh-TW')).toBe('VendorState');
  });
  it('仅隐藏协议数组计数，不丢弃未知业务字段或更改值', () => {
    expect(
      visibleInventoryAttributes({
        'AllowedSpeedsMHz@odata.count': '4',
        SerialNumber: 'KEEP-123',
        'Future.Value': 'unknown',
      }),
    ).toEqual([
      ['SerialNumber', 'KEEP-123'],
      ['Future.Value', 'unknown'],
    ]);
  });
});

it('硬件资源按标准语言目录维护且三语言稳定ID完整一致', () => {
  expect(Object.keys(cnHardware).sort()).toEqual(
    Object.keys(enHardware).sort(),
  );
  expect(Object.keys(twHardware).sort()).toEqual(
    Object.keys(enHardware).sort(),
  );
  expect(Object.keys(enHardware)).toHaveLength(108);
  expect(twHardware['oob.hardware.attribute.Model']).toBe('型號');
});
