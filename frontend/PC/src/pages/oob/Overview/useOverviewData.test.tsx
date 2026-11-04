import {
  focusManager,
  onlineManager,
  QueryClient,
  QueryClientProvider,
} from '@tanstack/react-query';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { RequestResult } from '@/services/oob/http';
import { requestFailure } from '@/services/oob/http';
import type {
  DeviceInventory,
  HistorySeriesDto,
  RecentTelemetryDto,
  ServerDetailDto,
  ServerTelemetryDto,
} from '@/services/oob/models';
import { telemetryFixture } from '../../../../tests/oobFixtures';
import type { ConnectionGap } from './recent';
import useOverviewData from './useOverviewData';

const mock = vi.hoisted(() => ({ server: vi.fn(), recent: vi.fn() }));
vi.mock('@/services/oob/api', () => ({
  getServer: mock.server,
  getRecent: mock.recent,
}));

const base = Date.parse('2026-10-25T02:00:00Z');
const iso = (offset = 0) => new Date(base + offset).toISOString();
const success = <T,>(data: T): RequestResult<T> => ({
  success: true,
  data,
  status: 200,
});
function snapshot(id = '9007199254740993', offset = 0, value = 100) {
  const result = telemetryFixture(iso(offset), [
    {
      sensorKey: 'system',
      name: 'Power',
      metricCode: 'power.watts',
      unit: 'W',
      value,
      availability: 'Available',
      source: 'Device',
      health: null,
      observedAt: iso(offset),
      deviceTime: null,
    },
  ]);
  result.server.id = id;
  result.server.name = `Server ${id}`;
  return result;
}
function detail(
  item: ServerTelemetryDto,
  inventory: DeviceInventory | null = null,
): ServerDetailDto {
  return {
    server: item.server,
    inventory,
    telemetry: item.telemetry,
    capabilities: {
      protocol: 'fixture',
      supportsInventory: true,
      supportedMetrics: [],
      powerActions: [],
      warnings: [],
    },
    powerOperations: [],
  };
}
function series(value = 100): HistorySeriesDto {
  return {
    sensorKey: 'system',
    name: 'Power',
    metricCode: 'power.watts',
    unit: 'W',
    points: [
      {
        time: iso(),
        average: value,
        minimum: value,
        maximum: value,
        count: 1,
      },
    ],
  };
}
type HookProps = {
  snapshots: ServerTelemetryDto[];
  events: Record<string, ServerTelemetryDto[]>;
  gaps: ConnectionGap[];
  now: number;
};

describe('聚合与单机共用本地库存和近期数据', () => {
  let client: QueryClient;
  beforeEach(() => {
    vi.clearAllMocks();
    vi.spyOn(Date, 'now').mockReturnValue(base);
    client = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    mock.server.mockImplementation(async (id: string) =>
      success(detail(snapshot(id))),
    );
    mock.recent.mockResolvedValue(success({ series: [], gaps: [] }));
    onlineManager.setOnline(true);
  });
  afterEach(() => {
    cleanup();
    client.clear();
    vi.restoreAllMocks();
    focusManager.setFocused(undefined);
    onlineManager.setOnline(true);
  });
  const mount = (initialProps: HookProps) =>
    renderHook(
      ({ snapshots, events, gaps, now }: HookProps) =>
        useOverviewData(snapshots, events, gaps, now),
      {
        initialProps,
        wrapper: ({ children }: { children: React.ReactNode }) => (
          <QueryClientProvider client={client}>{children}</QueryClientProvider>
        ),
      },
    );
  const props = (snapshots = [snapshot()]): HookProps => ({
    snapshots,
    events: {},
    gaps: [],
    now: base,
  });

  it('多服务器各读取一次，视图重渲染、顺序变化及新推送不发起重复请求', async () => {
    const first = snapshot('9007199254740993');
    const second = snapshot('9007199254740994');
    const view = mount(props([first, second]));
    await waitFor(() =>
      expect(view.result.current.every((row) => !row.loading)).toBe(true),
    );
    expect(mock.server).toHaveBeenCalledTimes(2);
    expect(mock.recent).toHaveBeenCalledTimes(2);
    const windows = mock.recent.mock.calls.map(([, window]) => window);
    expect(windows).toEqual([
      { from: iso(-15 * 60000), to: iso() },
      { from: iso(-15 * 60000), to: iso() },
    ]);
    const updated = snapshot(first.server.id, 10000, 125);
    view.rerender({
      snapshots: [second, updated],
      events: { [first.server.id]: [updated] },
      gaps: [],
      now: base + 10000,
    });
    expect(view.result.current.map((row) => row.snapshot.server.id)).toEqual([
      second.server.id,
      first.server.id,
    ]);
    expect(view.result.current[1].history[0].points.at(-1)?.average).toBe(125);
    expect(mock.server).toHaveBeenCalledTimes(2);
    expect(mock.recent).toHaveBeenCalledTimes(2);
  });

  it('切回窗口和网络恢复不重新读取，固定窗口不随墙钟或视图变化移动', async () => {
    const initialProps = props();
    const view = mount(initialProps);
    await waitFor(() => expect(view.result.current[0].loading).toBe(false));
    await act(async () => {
      onlineManager.setOnline(false);
      focusManager.setFocused(false);
      onlineManager.setOnline(true);
      focusManager.setFocused(true);
      await Promise.resolve();
    });
    view.rerender({ ...initialProps, now: base + 120000 });
    expect(mock.server).toHaveBeenCalledTimes(1);
    expect(mock.recent).toHaveBeenCalledTimes(1);
    expect(mock.recent.mock.calls[0][1]).toEqual({
      from: iso(-15 * 60000),
      to: iso(),
    });
  });

  it('较新推送先到，迟到首次历史仅合并旧点，不覆盖当前快照或新点', async () => {
    let complete!: (value: RequestResult<RecentTelemetryDto>) => void;
    mock.recent.mockReturnValue(
      new Promise<RequestResult<RecentTelemetryDto>>((done) => {
        complete = done;
      }),
    );
    const initial = snapshot();
    const view = mount(props([initial]));
    await waitFor(() => expect(mock.recent).toHaveBeenCalledTimes(1));
    const updated = snapshot(initial.server.id, 10000, 130);
    view.rerender({
      snapshots: [updated],
      events: { [updated.server.id]: [updated] },
      gaps: [],
      now: base + 10000,
    });
    expect(view.result.current[0].history[0].points.at(-1)?.average).toBe(130);
    await act(async () => {
      complete(success({ series: [series(100)], gaps: [] }));
    });
    await waitFor(() => expect(view.result.current[0].loading).toBe(false));
    expect(view.result.current[0].snapshot.telemetry.metrics[0].value).toBe(
      130,
    );
    expect(
      view.result.current[0].history[0].points.map((point) => point.average),
    ).toEqual([100, 130]);
    expect(mock.recent).toHaveBeenCalledTimes(1);
  });

  it('读取中的库存和近期请求在页面卸载时都收到取消信号', async () => {
    const signals: AbortSignal[] = [];
    mock.server.mockImplementation((_id: string, signal: AbortSignal) => {
      signals.push(signal);
      return new Promise(() => {});
    });
    mock.recent.mockImplementation(
      (_id: string, _window: unknown, signal: AbortSignal) => {
        signals.push(signal);
        return new Promise(() => {});
      },
    );
    const view = mount(props());
    await waitFor(() => expect(signals).toHaveLength(2));
    expect(signals.every((signal) => !signal.aborted)).toBe(true);
    view.unmount();
    expect(signals.every((signal) => signal.aborted)).toBe(true);
  });

  it('保留本地失败与推送断流缺口，断流期间的迟到点不填回曲线', async () => {
    const initial = snapshot();
    mock.recent.mockResolvedValue(
      success({
        series: [series()],
        gaps: [{ from: iso(-1000), to: iso(-1000) }],
      }),
    );
    const event = snapshot(initial.server.id, 10000, 140);
    const view = mount({
      snapshots: [event],
      events: { [initial.server.id]: [event] },
      gaps: [{ from: base + 5000, to: base + 15000 }],
      now: base + 20000,
    });
    await waitFor(() => expect(view.result.current[0].loading).toBe(false));
    expect(view.result.current[0].gaps).toEqual([
      { from: base + 5000, to: base + 15000 },
      { from: base - 1000, to: base - 1000 },
    ]);
    expect(
      view.result.current[0].history[0].points.some(
        (point) => point.time === iso(10000),
      ),
    ).toBe(false);
    expect(
      view.result.current[0].history[0].points.find(
        (point) => point.time === iso(5000),
      )?.average,
    ).toBeNull();
  });

  it('库存读取失败与历史读取失败分别返回问题，正常取消不显示异常', async () => {
    mock.server.mockResolvedValue(requestFailure('FORBIDDEN', '无权读取库存'));
    mock.recent.mockResolvedValue(
      requestFailure('NETWORK_ERROR', '本地历史读取失败'),
    );
    const view = mount(props());
    await waitFor(() => expect(view.result.current[0].issues).toHaveLength(2));
    expect(view.result.current[0]).toMatchObject({
      inventory: null,
      history: [],
      issues: [
        { kind: 'inventory', message: '无权读取库存' },
        { kind: 'history', message: '本地历史读取失败' },
      ],
    });
    view.unmount();
    client.clear();
    mock.server.mockResolvedValue(requestFailure('CANCELED', '请求已取消'));
    mock.recent.mockResolvedValue(requestFailure('CANCELED', '请求已取消'));
    const canceled = mount(props());
    await waitFor(() => expect(canceled.result.current[0].loading).toBe(false));
    expect(canceled.result.current[0].issues).toEqual([]);
  });

  it('库存中可唯一关联的阵列历史在当前快照缺探头时仍保留，其他无关指标不进入总览', async () => {
    const initial = snapshot();
    const inventory: DeviceInventory = {
      identity: initial.server.identity,
      collectedAt: iso(-60000),
      warnings: [],
      components: [
        {
          key: '/StorageControllers/RAID.Integrated.1-1',
          kind: 'StorageController',
          name: 'PERC H730P Mini',
          model: 'PERC H730P Mini',
          manufacturer: null,
          serialNumber: null,
          status: null,
          capacityBytes: null,
          attributes: {},
          children: [],
        },
      ],
    };
    const controller = {
      ...series(45),
      sensorKey: 'temperature:/StorageControllers/RAID.Integrated.1-1',
      name: 'Card temperature',
      metricCode: 'temperature.celsius',
      unit: '°C',
    };
    const unrelated = {
      ...series(200),
      sensorKey: 'capacity',
      name: 'Capacity',
      metricCode: 'power.capacity.watts',
    };
    mock.server.mockResolvedValue(success(detail(initial, inventory)));
    mock.recent.mockResolvedValue(
      success({ series: [controller, unrelated], gaps: [] }),
    );
    const view = mount(props([initial]));
    await waitFor(() => expect(view.result.current[0].loading).toBe(false));
    expect(view.result.current[0].inventory?.collectedAt).toBe(iso(-60000));
    expect(view.result.current[0].history).toEqual([controller]);
  });
});
