import {
  HttpTransportType,
  HubConnectionBuilder,
  LogLevel,
} from '@microsoft/signalr';
import { captureRuntimeException } from './browserExceptions';
import type { ServerTelemetryDto } from './models';

export type LiveConnectionState =
  | 'connecting'
  | 'connected'
  | 'reconnecting'
  | 'disconnected';

const activeStops = new Set<() => Promise<void>>();

/** 只使用带 Cookie 的同源 WebSocket；重连只恢复订阅，不查询或回放历史。 */
export function subscribeTelemetry(
  onTelemetry: (snapshot: ServerTelemetryDto) => void,
  onState: (state: LiveConnectionState) => void,
) {
  let disposed = false;
  let retryTimer: ReturnType<typeof setTimeout> | undefined;
  const connection = new HubConnectionBuilder()
    .withUrl('/api/oob/live', {
      transport: HttpTransportType.WebSockets,
      skipNegotiation: true,
      withCredentials: true,
    })
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: ({ previousRetryCount }) =>
        Math.min(30000, 1000 * 2 ** Math.min(previousRetryCount, 5)),
    })
    // 状态在页面明确展示，不将框架连接细节或消息正文输出到控制台。
    .configureLogging(LogLevel.None)
    .build();
  const state = (value: LiveConnectionState) => {
    if (!disposed) onState(value);
  };
  const receive = (value: ServerTelemetryDto) => {
    if (!disposed) onTelemetry(value);
  };
  connection.on('TelemetryUpdated', receive);
  connection.onreconnecting((error) => {
    if (error && !disposed) void captureRuntimeException(error);
    state('reconnecting');
  });
  connection.onreconnected(() => state('connected'));
  connection.onclose((error) => {
    if (error && !disposed) void captureRuntimeException(error);
    state('disconnected');
  });

  const start = async () => {
    state('connecting');
    try {
      await connection.start();
      if (!disposed) state('connected');
    } catch (error) {
      if (disposed) return;
      void captureRuntimeException(error);
      state('disconnected');
      // SignalR 自动重连不包含首次连接失败，单独重试握手，不发 HTTP 补取请求。
      retryTimer = setTimeout(start, 5000);
    }
  };
  const stop = async () => {
    if (disposed) return;
    disposed = true;
    clearTimeout(retryTimer);
    connection.off('TelemetryUpdated', receive);
    activeStops.delete(stop);
    try {
      await connection.stop();
    } catch (error) {
      void captureRuntimeException(error);
    }
  };
  activeStops.add(stop);
  void start();
  return () => {
    void stop();
  };
}

/** 退出前释放当前身份的全部订阅，旧连接回调不能写入下一账号的页面。 */
export async function stopTelemetryConnections() {
  await Promise.allSettled([...activeStops].map((stop) => stop()));
}
