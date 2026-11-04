vi.mock('./browserExceptions', () => ({ captureRuntimeException: vi.fn() }));

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ServerTelemetryDto } from './models';

const fake = vi.hoisted(() => ({
  url: vi.fn(),
  reconnect: vi.fn(),
  on: vi.fn(),
  off: vi.fn(),
  start: vi.fn(),
  stop: vi.fn(),
  reconnecting: undefined as undefined | (() => void),
  reconnected: undefined as undefined | (() => void),
  close: undefined as undefined | (() => void),
}));
vi.mock('@microsoft/signalr', () => ({
  HttpTransportType: { WebSockets: 1 },
  LogLevel: { None: 6 },
  HubConnectionBuilder: class {
    withUrl(...args: unknown[]) {
      fake.url(...args);
      return this;
    }
    withAutomaticReconnect(...args: unknown[]) {
      fake.reconnect(...args);
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return {
        start: fake.start,
        stop: fake.stop,
        on: fake.on,
        off: fake.off,
        onreconnecting: (fn: () => void) => {
          fake.reconnecting = fn;
        },
        onreconnected: (fn: () => void) => {
          fake.reconnected = fn;
        },
        onclose: (fn: () => void) => {
          fake.close = fn;
        },
      };
    }
  },
}));

import { stopTelemetryConnections, subscribeTelemetry } from './realtime';

describe('SignalR 仅WebSocket订阅生命周期', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    fake.start.mockResolvedValue(undefined);
    fake.stop.mockResolvedValue(undefined);
  });
  afterEach(async () => {
    await stopTelemetryConnections();
    vi.useRealTimers();
  });

  it('直接使用同源Cookie的WebSocket，订阅数据并在卸载后隔离迟到回调', async () => {
    const receive = vi.fn();
    const state = vi.fn();
    const unsubscribe = subscribeTelemetry(receive, state);
    await Promise.resolve();
    expect(fake.url).toHaveBeenCalledWith('/api/oob/live', {
      transport: 1,
      skipNegotiation: true,
      withCredentials: true,
    });
    expect(state).toHaveBeenLastCalledWith('connected');
    const callback = fake.on.mock.calls[0][1];
    const event = { publishedAt: '2026-10-24T10:00:00Z' } as ServerTelemetryDto;
    callback(event);
    expect(receive).toHaveBeenCalledWith(event);
    unsubscribe();
    callback(event);
    expect(receive).toHaveBeenCalledTimes(1);
    expect(fake.off).toHaveBeenCalledWith('TelemetryUpdated', callback);
    expect(fake.stop).toHaveBeenCalledTimes(1);
  });

  it('重连只改变状态，不重启连接或发起快照与历史请求', async () => {
    const fetch = vi.spyOn(globalThis, 'fetch');
    const state = vi.fn();
    subscribeTelemetry(vi.fn(), state);
    await Promise.resolve();
    fake.reconnecting?.();
    fake.reconnected?.();
    expect(state.mock.calls.map(([value]) => value)).toEqual([
      'connecting',
      'connected',
      'reconnecting',
      'connected',
    ]);
    expect(fake.start).toHaveBeenCalledTimes(1);
    expect(fetch).not.toHaveBeenCalled();
    fetch.mockRestore();
  });

  it('首次握手失败会重试，退出会取消待重试和所有订阅', async () => {
    vi.useFakeTimers();
    fake.start.mockRejectedValueOnce(new Error('offline'));
    const state = vi.fn();
    subscribeTelemetry(vi.fn(), state);
    await Promise.resolve();
    expect(state).toHaveBeenLastCalledWith('disconnected');
    await vi.advanceTimersByTimeAsync(5000);
    expect(fake.start).toHaveBeenCalledTimes(2);
    expect(state).toHaveBeenLastCalledWith('connected');
    await stopTelemetryConnections();
    await vi.advanceTimersByTimeAsync(10000);
    expect(fake.start).toHaveBeenCalledTimes(2);
    expect(fake.stop).toHaveBeenCalledTimes(1);
  });
});
