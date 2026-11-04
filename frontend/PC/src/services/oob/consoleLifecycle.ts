const activeStops = new Set<(expired?: boolean) => Promise<void>>();

/** 会话随当前身份销毁；关闭控制台不触碰独立挂载的虚拟介质。 */
export function registerConsoleCleanup(
  stop: (expired?: boolean) => Promise<void>,
) {
  activeStops.add(stop);
  return () => activeStops.delete(stop);
}
export async function stopConsoleConnections(expired = false) {
  await Promise.allSettled([...activeStops].map((stop) => stop(expired)));
}
