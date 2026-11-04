import { describe, expect, it } from 'vitest';
import {
  changeFloatingWindow,
  fitFloatingWindow,
  initialFloatingWindow,
} from './floatingWindow';

describe('控制台悬浮窗边界', () => {
  it('初始窗口和缩小后的窗口都留在视口中', () => {
    const rect = initialFloatingWindow({ width: 1280, height: 800 });
    expect(rect).toEqual({ x: 496, y: 80, width: 760, height: 480 });
    expect(fitFloatingWindow(rect, { width: 300, height: 200 })).toEqual({
      x: 8,
      y: 8,
      width: 284,
      height: 184,
    });
  });

  it('拖动不能把标题栏带出屏幕，最小化保留展开高度', () => {
    const rect = { x: 100, y: 100, width: 600, height: 400 };
    const bounds = { width: 1000, height: 800 };
    expect(changeFloatingWindow(rect, -999, -999, 'move', bounds)).toEqual({
      ...rect,
      x: 0,
      y: 0,
    });
    const minimized = changeFloatingWindow(
      rect,
      999,
      999,
      'move',
      bounds,
      true,
    );
    expect(minimized).toEqual({ ...rect, x: 392, y: 752 });
    expect(fitFloatingWindow(minimized, bounds)).toEqual({
      ...rect,
      x: 392,
      y: 392,
    });
  });

  it('缩放受最小尺寸与视口尺寸共同约束', () => {
    const rect = { x: 100, y: 100, width: 600, height: 400 };
    const bounds = { width: 1000, height: 800 };
    expect(changeFloatingWindow(rect, -999, -999, 'resize', bounds)).toEqual({
      ...rect,
      width: 320,
      height: 220,
    });
    expect(changeFloatingWindow(rect, 999, 999, 'resize', bounds)).toEqual({
      x: 100,
      y: 100,
      width: 892,
      height: 692,
    });
  });
});
