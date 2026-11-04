export type FloatingWindowRect = {
  x: number;
  y: number;
  width: number;
  height: number;
};
export type WindowBounds = { width: number; height: number };

export const floatingTitleHeight = 40;
const margin = 8;

/** 视口缩小后仍保留完整标题栏；最小化只改变可见高度，不丢失原窗口尺寸。 */
export function fitFloatingWindow(
  rect: FloatingWindowRect,
  bounds: WindowBounds,
  minimized = false,
): FloatingWindowRect {
  const availableWidth = Math.max(1, bounds.width - margin * 2);
  const availableHeight = Math.max(
    floatingTitleHeight,
    bounds.height - margin * 2,
  );
  const width = Math.min(availableWidth, Math.max(320, rect.width));
  const height = Math.min(availableHeight, Math.max(220, rect.height));
  const visibleHeight = minimized ? floatingTitleHeight : height;
  return {
    x: Math.max(0, Math.min(rect.x, bounds.width - width - margin)),
    y: Math.max(0, Math.min(rect.y, bounds.height - visibleHeight - margin)),
    width,
    height,
  };
}

export function initialFloatingWindow(
  bounds: WindowBounds,
): FloatingWindowRect {
  return fitFloatingWindow(
    { x: bounds.width - 784, y: 80, width: 760, height: 480 },
    bounds,
  );
}

export function changeFloatingWindow(
  rect: FloatingWindowRect,
  deltaX: number,
  deltaY: number,
  operation: 'move' | 'resize',
  bounds: WindowBounds,
  minimized = false,
) {
  return fitFloatingWindow(
    operation === 'move'
      ? { ...rect, x: rect.x + deltaX, y: rect.y + deltaY }
      : {
          ...rect,
          // 右下角缩放固定左上角，不能把一次放大变成整窗平移。
          width: Math.min(rect.width + deltaX, bounds.width - rect.x - margin),
          height: Math.min(
            rect.height + deltaY,
            bounds.height - rect.y - margin,
          ),
        },
    bounds,
    minimized,
  );
}
