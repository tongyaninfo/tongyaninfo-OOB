import { describe, expect, it, vi } from 'vitest';

const locale = vi.hoisted(() => ({ current: 'ja-JP', set: vi.fn() }));
vi.mock('@umijs/max', () => ({
  getLocale: () => locale.current,
  setLocale: locale.set,
}));

import en from './en-US/oob';
import { filterSupportedLocales, restoreSupportedLocale } from './supported';
import cn from './zh-CN/oob';
import tw from './zh-TW/oob';

describe('产品三语言边界', () => {
  it('登录与顶栏只允许简中、繁中和英文，旧语言缓存回退', () => {
    expect(
      filterSupportedLocales(
        ['en-US', 'zh-CN', 'ja-JP', 'zh-TW', 'fa-IR'].map((lang) => ({ lang })),
      ).map((item) => item.lang),
    ).toEqual(['en-US', 'zh-CN', 'zh-TW']);
    restoreSupportedLocale();
    expect(locale.set).toHaveBeenCalledWith('zh-CN', false);
    locale.set.mockClear();
    locale.current = 'zh-TW';
    restoreSupportedLocale();
    expect(locale.set).not.toHaveBeenCalled();
  });
  it('三语言OOB资源键完全一致，繁中不回退到英文或简中', () => {
    expect(Object.keys(tw).sort()).toEqual(Object.keys(en).sort());
    expect(Object.keys(cn).sort()).toEqual(Object.keys(en).sort());
    expect(tw['oob.inventory']).toBe('硬體信息');
    expect(tw['oob.monitor.averageSpeedPercent']).toBe('平均轉速百分比（%）');
  });
});
