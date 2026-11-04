import { reportClientException } from './api';
import { setUnexpectedErrorHandler } from './http';
import type { ClientExceptionRequest } from './models';

let reportingQueue = Promise.resolve();
let reported = new WeakSet<object>();
let identity: string | undefined;
let generation = 0;
const activeReports = new Set<AbortController>();
const recent = new Map<string, number>();

/** 身份切换立即作废旧队列、去重缓存和在途上报，禁止归属到下一账户。 */
export function setExceptionCaptureIdentity(userId?: string) {
  if (identity === userId) return;
  identity = userId;
  generation++;
  for (const controller of activeReports) controller.abort();
  activeReports.clear();
  recent.clear();
  reported = new WeakSet<object>();
  reportingQueue = Promise.resolve();
}
export function getExceptionCaptureIdentity() {
  return identity;
}

/** 只发送代码帧，不发送异常消息、页面查询串、表单、设备凭据或用户数据。 */
export function safeStackFrames(stack?: string): string | undefined {
  const safe = (stack || '')
    .split('\n')
    .flatMap((line) => {
      if (!/^\s*at\s|^[^\s@]+@/.test(line)) return [];
      // 只保留文件基名和位置，函数名、消息、URL、参数及任意尾文均不能离开浏览器。
      const frame = line.match(
        /(?:^|[/\\(\s])([A-Za-z0-9_.-]+\.(?:[cm]?js|jsx|tsx?)):(\d+):(\d+)(?=[)\s?#]|$)/,
      );
      return frame ? [`at ${frame[1]}:${frame[2]}:${frame[3]}`] : [];
    })
    .join('\n')
    .slice(0, 20000);
  return safe || undefined;
}

/** 去重与递归保护只针对异常上报通道；上报失败不重试、不再次生成异常记录。 */
export async function captureRuntimeException(
  error: unknown,
  eventType: ClientExceptionRequest['eventType'] = 'error',
) {
  if (
    error &&
    typeof error === 'object' &&
    'success' in error &&
    error.success === false &&
    'errorCode' in error
  )
    return;
  if (!identity || (error instanceof Error && error.name === 'AbortError'))
    return;
  const expectedGeneration = generation;
  const expectedIdentity = identity;
  if (error && typeof error === 'object') {
    if (reported.has(error)) return;
    reported.add(error);
  }
  const exceptionType =
    error instanceof Error
      ? error.name.replace(/[^A-Za-z0-9_.]/g, '').slice(0, 300) || 'Error'
      : 'UnhandledRejection';
  const stackTrace =
    error instanceof Error ? safeStackFrames(error.stack) : undefined;
  const path =
    typeof window === 'undefined'
      ? undefined
      : window.location.pathname.slice(0, 512);
  const key = `${eventType}:${exceptionType}:${stackTrace || ''}:${path || ''}`;
  const now = Date.now();
  for (const [fingerprint, time] of recent)
    if (now - time > 60000) recent.delete(fingerprint);
  if (recent.has(key)) return;
  recent.set(key, now);
  // 独立异常串行排队，不能因为另一个异常正在上传就丢弃；上报请求自身禁用异常上报。
  reportingQueue = reportingQueue.then(async () => {
    if (expectedGeneration !== generation || expectedIdentity !== identity)
      return;
    const controller = new AbortController();
    activeReports.add(controller);
    try {
      await reportClientException(
        {
          eventType,
          exceptionType,
          stackTrace,
          path,
        },
        controller.signal,
      );
    } catch {
      /* 末端保护：上报模块自身异常不得递归。 */
    } finally {
      activeReports.delete(controller);
    }
  });
  await reportingQueue;
}

/** 注册一次且可卸载，业务失败结果与主动取消不会触发全局异常事件。 */
export function installBrowserExceptionCapture(userId?: string) {
  setExceptionCaptureIdentity(userId);
  if (!userId) return () => undefined;
  const error = (event: ErrorEvent) => {
    void captureRuntimeException(event.error || new Error(), 'error');
  };
  const rejection = (event: PromiseRejectionEvent) => {
    void captureRuntimeException(event.reason, 'unhandledrejection');
  };
  window.addEventListener('error', error);
  window.addEventListener('unhandledrejection', rejection);
  setUnexpectedErrorHandler((value) => {
    void captureRuntimeException(value);
  });
  return () => {
    window.removeEventListener('error', error);
    window.removeEventListener('unhandledrejection', rejection);
    setUnexpectedErrorHandler(undefined);
    setExceptionCaptureIdentity(undefined);
  };
}
