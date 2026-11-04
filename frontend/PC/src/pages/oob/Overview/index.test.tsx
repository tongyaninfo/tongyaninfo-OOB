import {
  focusManager,
  onlineManager,
  QueryClient,
  QueryClientProvider,
} from '@tanstack/react-query';
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { DashboardDto, ServerTelemetryDto } from '@/services/oob/models';
import type { LiveConnectionState } from '@/services/oob/realtime';
import { telemetryFixture } from '../../../../tests/oobFixtures';
import type { AggregateResult } from './aggregate.types';

const mock = vi.hoisted(() => ({
  dashboard: vi.fn(),
  history: vi.fn(),
  server: vi.fn(),
  stop: vi.fn(),
  subscribe: vi.fn(),
  receive: undefined as undefined | ((event: ServerTelemetryDto) => void),
  state: undefined as undefined | ((state: LiveConnectionState) => void),
}));
vi.mock('@/services/oob/api', () => ({
  getDashboard: mock.dashboard,
  getRecent: mock.history,
  getServer: mock.server,
}));
vi.mock('@/services/oob/realtime', () => ({
  subscribeTelemetry: (
    receive: typeof mock.receive,
    state: typeof mock.state,
  ) => {
    mock.subscribe();
    mock.receive = receive;
    mock.state = state;
    state?.('connected');
    return mock.stop;
  },
}));
vi.mock('@umijs/max', () => ({
  Link: ({ to, children }: { to: string; children: React.ReactNode }) => (
    <a href={to}>{children}</a>
  ),
}));
vi.mock('@ant-design/pro-components', () => ({
  PageContainer: ({
    children,
    title,
    extra,
  }: {
    children: React.ReactNode;
    title: React.ReactNode;
    extra: React.ReactNode;
  }) => (
    <div>
      {title}
      {extra}
      {children}
    </div>
  ),
}));
vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));
vi.mock('../components/ManagedTable', () => ({ default: () => null }));
vi.mock('../components/ServerStatus', () => ({ default: () => null }));
vi.mock('./ServerMonitor', () => ({
  default: ({ snapshot }: { snapshot: ServerTelemetryDto }) => (
    <div data-testid="monitor" data-server-id={snapshot.server.id}>
      {snapshot.telemetry.metrics[0].value}
    </div>
  ),
}));
vi.mock('./AggregateDashboard', () => ({
  default: ({ data }: { data: AggregateResult }) => (
    <output data-testid="aggregate">{data.power.stats.total ?? '-'}</output>
  ),
}));

import Overview from './index';

const base = Date.parse('2026-10-24T12:00:00Z');
const snapshot = (offset: number, value: number) =>
  telemetryFixture(new Date(base + offset).toISOString(), [
    {
      sensorKey: 'system',
      name: 'Power',
      metricCode: 'power.watts',
      unit: 'W',
      value,
      availability: 'Available',
      source: 'Device',
      health: null,
      observedAt: new Date(base + offset).toISOString(),
      deviceTime: null,
    },
  ]);
const dashboard = (item: ServerTelemetryDto): DashboardDto => ({
  totalServers: 1,
  onlineServers: 1,
  offlineServers: 0,
  pendingServers: 0,
  totalPowerWatts: item.telemetry.metrics[0].value,
  telemetrySnapshots: [item],
  servers: [item.server],
  generatedAt: new Date(base).toISOString(),
});

describe('总览实时推送与不补取约定', () => {
  let client: QueryClient;
  beforeEach(() => {
    vi.clearAllMocks();
    vi.spyOn(Date, 'now').mockReturnValue(base);
    client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    mock.history.mockResolvedValue({
      success: true,
      data: { series: [], gaps: [] },
      status: 200,
    });
    mock.server.mockResolvedValue({
      success: true,
      data: { inventory: null },
      status: 200,
    });
    onlineManager.setOnline(true);
  });
  afterEach(() => {
    cleanup();
    client.clear();
    vi.restoreAllMocks();
    focusManager.setFocused(undefined);
    onlineManager.setOnline(true);
  });
  const mount = () =>
    render(
      <QueryClientProvider client={client}>
        <Overview />
      </QueryClientProvider>,
    );

  it('推送直接改变数值，网络恢复、重连与切回页面均不补取快照或历史', async () => {
    mock.dashboard.mockResolvedValue({
      success: true,
      data: dashboard(snapshot(0, 100)),
      status: 200,
    });
    mount();
    await waitFor(() =>
      expect(screen.getByTestId('aggregate')).toHaveTextContent('100'),
    );
    await waitFor(() => expect(mock.history).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByRole('button', { name: 'oob.aggregate.next' }));
    expect(screen.getByTestId('monitor')).toHaveTextContent('100');
    expect(screen.queryByTestId('aggregate')).not.toBeInTheDocument();
    vi.mocked(Date.now).mockReturnValue(base + 10000);
    await act(async () => {
      mock.receive?.(snapshot(10000, 120));
    });
    expect(screen.getByTestId('monitor')).toHaveTextContent('120');
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.aggregate.previous' }),
    );
    expect(screen.getByTestId('aggregate')).toHaveTextContent('120');
    await act(async () => {
      mock.state?.('reconnecting');
      onlineManager.setOnline(false);
      focusManager.setFocused(false);
      onlineManager.setOnline(true);
      mock.state?.('connected');
      focusManager.setFocused(true);
      await Promise.resolve();
    });
    expect(mock.dashboard).toHaveBeenCalledTimes(1);
    expect(mock.history).toHaveBeenCalledTimes(1);
    expect(mock.server).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('aggregate')).toHaveTextContent('120');
    await act(async () => {
      mock.receive?.(snapshot(5000, 110));
    });
    expect(screen.getByTestId('aggregate')).toHaveTextContent('120');
    expect(mock.subscribe).toHaveBeenCalledTimes(1);
  });

  it('推送先于首次HTTP响应时，迟到旧响应不覆盖较新读数；卸载释放订阅', async () => {
    let resolve!: (value: DashboardDto) => void;
    mock.dashboard.mockReturnValue(
      new Promise((done) => {
        resolve = (data) => done({ success: true, data, status: 200 });
      }),
    );
    const view = mount();
    vi.mocked(Date.now).mockReturnValue(base + 10000);
    await act(async () => {
      mock.receive?.(snapshot(10000, 130));
    });
    expect(screen.getByTestId('aggregate')).toHaveTextContent('130');
    await act(async () => {
      resolve(dashboard(snapshot(0, 100)));
    });
    expect(screen.getByTestId('aggregate')).toHaveTextContent('130');
    view.unmount();
    expect(mock.stop).toHaveBeenCalledTimes(1);
  });

  it('N台服务器有N个独立明细页，翻页不重复读取；名称排序变化保持当前服务器身份', async () => {
    const first = snapshot(0, 100);
    first.server.id = '9007199254740993';
    first.server.name = 'A';
    const second = snapshot(0, 200);
    second.server.id = '9007199254740994';
    second.server.name = 'B';
    const initial = dashboard(first);
    initial.telemetrySnapshots = [first, second];
    initial.servers = [first.server, second.server];
    initial.totalServers = 2;
    mock.dashboard.mockResolvedValue({
      success: true,
      data: initial,
      status: 200,
    });
    mount();
    await waitFor(() =>
      expect(screen.getByTestId('aggregate')).toHaveTextContent('300'),
    );
    await waitFor(() => expect(mock.history).toHaveBeenCalledTimes(2));
    const next = screen.getByRole('button', { name: 'oob.aggregate.next' });
    const previous = screen.getByRole('button', {
      name: 'oob.aggregate.previous',
    });
    expect(previous).toBeDisabled();
    fireEvent.keyDown(next, { key: 'ArrowRight' });
    expect(screen.getAllByTestId('monitor')).toHaveLength(1);
    expect(screen.getByTestId('monitor')).toHaveAttribute(
      'data-server-id',
      first.server.id,
    );
    fireEvent.keyDown(next, { key: 'ArrowRight', repeat: true });
    expect(screen.getByTestId('monitor')).toHaveAttribute(
      'data-server-id',
      first.server.id,
    );
    fireEvent.click(next);
    expect(screen.getByTestId('monitor')).toHaveAttribute(
      'data-server-id',
      second.server.id,
    );
    expect(next).toBeDisabled();
    await act(async () => {
      mock.receive?.({
        ...second,
        server: { ...second.server, name: '0-B' },
        publishedAt: new Date(base + 1000).toISOString(),
      });
    });
    expect(screen.getByTestId('monitor')).toHaveAttribute(
      'data-server-id',
      second.server.id,
    );
    expect(screen.getByTestId('monitor')).toHaveTextContent('200');
    fireEvent.keyDown(previous, { key: 'ArrowLeft' });
    expect(screen.getByTestId('aggregate')).toHaveTextContent('300');
    expect(mock.history).toHaveBeenCalledTimes(2);
    expect(mock.server).toHaveBeenCalledTimes(2);
    expect(mock.dashboard).toHaveBeenCalledTimes(1);
    expect(mock.subscribe).toHaveBeenCalledTimes(1);
  });

  it('没有服务器时只保留聚合页，两侧切换均禁用且不读取逐台历史', async () => {
    const initial = dashboard(snapshot(0, 100));
    initial.telemetrySnapshots = [];
    initial.servers = [];
    initial.totalServers = 0;
    mock.dashboard.mockResolvedValue({
      success: true,
      data: initial,
      status: 200,
    });
    mount();
    await waitFor(() =>
      expect(screen.getByTestId('aggregate')).toHaveTextContent('-'),
    );
    expect(
      screen.getByRole('button', { name: 'oob.aggregate.previous' }),
    ).toBeDisabled();
    expect(
      screen.getByRole('button', { name: 'oob.aggregate.next' }),
    ).toBeDisabled();
    expect(screen.queryByTestId('monitor')).not.toBeInTheDocument();
    expect(mock.history).not.toHaveBeenCalled();
    expect(mock.server).not.toHaveBeenCalled();
  });
});
