import { describe, expect, it, vi } from 'vitest';
import { createKeyTracker, keyboardRows, numpadRows } from './keyboard';
import { appendTraffic, formatBytes } from './traffic';

describe('控制台输入与流量的真实边界', () => {
  it('失焦时释放所有物理修饰键，后到达的 keyup 不重复发送', () => {
    const send = vi.fn();
    const tracker = createKeyTracker(send);
    tracker.down('ControlLeft', 'Control');
    tracker.down('KeyA', 'a');
    tracker.release();
    tracker.up('KeyA', 'a');
    expect(send.mock.calls.map(([event]) => event)).toEqual([
      { type: 'key', code: 'ControlLeft', key: 'Control', down: true },
      { type: 'key', code: 'KeyA', key: 'a', down: true },
      { type: 'key', code: 'ControlLeft', key: 'Control', down: false },
      { type: 'key', code: 'KeyA', key: 'a', down: false },
      { type: 'release-all' },
    ]);
  });
  it('包含功能、导航、组合修饰键与独立数字小键盘，不重复 code', () => {
    const codes = [...keyboardRows, ...numpadRows]
      .flat()
      .map((item) => item.code);
    expect(new Set(codes).size).toBe(codes.length);
    for (const code of [
      'F12',
      'Delete',
      'Escape',
      'ControlLeft',
      'ArrowDown',
      'NumpadEnter',
      'NumpadDecimal',
    ])
      expect(codes).toContain(code);
    expect(codes).toContain('MetaRight');
    expect(codes).toHaveLength(104);
  });
  it('累计计数回退/断线/不可用保留缺口，再次连接不补零或跨断点计算', () => {
    let points = appendTraffic([], 1000, 100, 20);
    points = appendTraffic(points, 3000, 300, 30);
    expect(points.at(-1)?.receiveRate).toBe(100);
    points = appendTraffic(points, 4000, null, null);
    points = appendTraffic(points, 6000, 600, 40);
    expect(points.at(-1)?.receiveRate).toBeNull();
    points = appendTraffic(points, 7000, 10, 0);
    expect(points.at(-1)?.receiveRate).toBeNull();
    points = appendTraffic(points, 30000, 500, 50);
    expect(points.at(-1)?.receiveRate).toBeNull();
    expect(formatBytes(null)).toBe('—');
    expect(formatBytes(1024, true)).toBe('1.0 KiB/s');
  });
});
