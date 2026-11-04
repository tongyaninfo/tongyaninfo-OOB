export const gateCookieName = 'OobDesktop.Gate';

export interface HostReady {
  type: 'ready';
  url: string;
  initialized: boolean;
  instanceId: string;
}

/** 只接受本次子进程报告的 loopback 根地址，禁止将启动消息变成任意导航。 */
export function parseReady(value: unknown, instanceId: string): HostReady | null {
  if (typeof value !== 'object' || value === null) return null;
  const candidate = value as Record<string, unknown>;
  if (candidate.type !== 'ready' || candidate.instanceId !== instanceId
    || typeof candidate.url !== 'string' || typeof candidate.initialized !== 'boolean') return null;
  try {
    const url = new URL(candidate.url);
    if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port
      || url.username || url.password || url.pathname !== '/' || url.search || url.hash) return null;
    return { type: 'ready', url: url.origin, initialized: candidate.initialized, instanceId };
  } catch { return null; }
}

/** 页面只能在已绑定的本机来源内导航，外部页面不获得桌面权限。 */
export function isTrustedNavigation(target: string, origin: string): boolean {
  try {
    const url = new URL(target);
    return url.origin === origin && !url.username && !url.password;
  } catch { return false; }
}

/** 控制台仅获得必要的键鼠、全屏和剪贴板写入能力，主页面与请求 frame 均须同源。 */
export function isAllowedPermission(permission: string, pageUrl: string, requestingUrl: string, origin: string): boolean {
  return ['fullscreen', 'pointerLock', 'keyboardLock', 'clipboard-sanitized-write'].includes(permission)
    && isTrustedNavigation(pageUrl, origin) && isTrustedNavigation(requestingUrl, origin);
}
