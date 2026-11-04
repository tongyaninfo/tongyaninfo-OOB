import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import React from 'react';
import { beforeEach, expect, it, vi } from 'vitest';
import type { ServerDetailDto } from '@/services/oob/models';
import { telemetryFixture } from '../../../../tests/oobFixtures';

const api = vi.hoisted(() => ({
  get: vi.fn(),
  inventory: vi.fn(),
  sample: vi.fn(),
  log: vi.fn(),
  canAdmin: true,
  canManageServers: true,
  canOperatePower: true,
  canUseConsole: true,
}));
vi.mock('@/services/oob/api', () => ({
  getServer: api.get,
  refreshInventory: api.inventory,
  sampleTelemetry: api.sample,
}));
vi.mock('@/services/oob/browserExceptions', () => ({
  captureRuntimeException: api.log,
}));
vi.mock('@umijs/max', () => ({
  useAccess: () => ({
    canAdmin: api.canAdmin,
    canManageServers: api.canManageServers,
    canOperatePower: api.canOperatePower,
    canUseConsole: api.canUseConsole,
    canManageUsers: true,
  }),
  useParams: () => ({ id: '9007199254740993' }),
  Link: ({ children }: { children: React.ReactNode }) => (
    <span>{children}</span>
  ),
}));
vi.mock('@ant-design/pro-components', () => ({
  PageContainer: ({
    children,
    extra,
  }: {
    children: React.ReactNode;
    extra?: React.ReactNode;
  }) => (
    <div>
      {extra}
      {children}
    </div>
  ),
}));
vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));
vi.mock('../useTelemetryStream', () => ({
  useTelemetryStream: () => ({ state: 'connected', gaps: [] }),
}));
vi.mock('../components/InventoryPanel', () => ({
  default: () => <div>stored inventory snapshot</div>,
}));
vi.mock('../components/TelemetryPanel', () => ({
  default: () => <div>stored telemetry snapshot</div>,
}));
vi.mock('../components/PowerPanel', () => ({
  default: () => <div>power operations panel</div>,
}));
vi.mock('../components/UidPanel', () => ({
  default: () => <div>uid operations panel</div>,
}));
vi.mock('../components/HistoryPanel', () => ({ default: () => null }));
vi.mock('../components/ServerEditor', () => ({ default: () => null }));
vi.mock('../components/ServerStatus', () => ({ default: () => null }));

import ServerDetail from './index';

beforeEach(() => {
  vi.clearAllMocks();
  api.canAdmin = true;
  api.canManageServers = true;
  api.canOperatePower = true;
  api.canUseConsole = true;
});

it('普通只读用户不挂载电源和UID请求面板，也不能触发采样、刷新库存', async () => {
  api.canAdmin = false;
  api.canManageServers = false;
  api.canOperatePower = false;
  api.canUseConsole = false;
  const snapshot = telemetryFixture('2026-10-25T00:00:00Z', []);
  api.get.mockResolvedValue({
    success: true,
    status: 200,
    data: {
      server: snapshot.server,
      telemetry: snapshot.telemetry,
      inventory: null,
      capabilities: {
        protocol: 'Redfish',
        supportsInventory: true,
        supportedMetrics: [],
        powerActions: [],
        warnings: [],
      },
      powerOperations: [],
    },
  });
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const view = render(
    <App>
      <QueryClientProvider client={client}>
        <ServerDetail />
      </QueryClientProvider>
    </App>,
  );
  await screen.findByText('stored inventory snapshot');
  expect(screen.queryByText('uid operations panel')).toBeNull();
  expect(screen.queryByRole('tab', { name: 'oob.power' })).toBeNull();
  expect(
    screen.queryByRole('button', { name: 'oob.inventory.refresh' }),
  ).toBeNull();
  fireEvent.click(screen.getByRole('tab', { name: 'oob.realtime' }));
  await screen.findByText('stored telemetry snapshot');
  expect(
    screen.queryByRole('button', { name: 'oob.telemetry.sample' }),
  ).toBeNull();
  view.unmount();
  client.clear();
  api.canAdmin = true;
  api.canUseConsole = true;
});

it('详情读取缓存不刷新硬件；立即采样业务失败显示失败且不走成功失效回调', async () => {
  const snapshot = telemetryFixture('2026-10-25T00:00:00Z', []);
  const fixture: ServerDetailDto = {
    server: snapshot.server,
    telemetry: snapshot.telemetry,
    inventory: {
      identity: snapshot.server.identity,
      collectedAt: '2026-10-24T00:00:00Z',
      components: [],
      warnings: [],
    },
    capabilities: {
      protocol: 'Redfish',
      supportsInventory: true,
      supportedMetrics: [],
      powerActions: [],
      warnings: [],
    },
    powerOperations: [],
  };
  api.get.mockResolvedValue({ success: true, data: fixture, status: 200 });
  api.sample.mockResolvedValue({
    success: false,
    data: null,
    errorCode: 'SERVER_BUSY',
    errorMessage: 'fixture sample unavailable',
    status: 409,
    kind: 'business',
    dispatched: true,
  });
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const invalidate = vi.spyOn(client, 'invalidateQueries');
  render(
    <App>
      <QueryClientProvider client={client}>
        <ServerDetail />
      </QueryClientProvider>
    </App>,
  );
  await screen.findByText('stored inventory snapshot');
  expect(api.inventory).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('tab', { name: 'oob.realtime' }));
  fireEvent.click(
    await screen.findByRole('button', { name: 'oob.telemetry.sample' }),
  );
  await screen.findByText('fixture sample unavailable');
  await waitFor(() => expect(api.sample).toHaveBeenCalledTimes(1));
  expect(api.get).toHaveBeenCalledTimes(1);
  expect(api.inventory).not.toHaveBeenCalled();
  expect(invalidate).not.toHaveBeenCalled();
  expect(api.log).not.toHaveBeenCalled();
});

it.each([
  [false, true, false],
  [false, false, true],
  [false, true, true],
  [true, false, false],
])('详情按独立权限展示：admin=%s，服务器=%s，电源=%s', async (admin, manageServers, operatePower) => {
  api.canAdmin = admin;
  api.canManageServers = manageServers;
  api.canOperatePower = operatePower;
  const snapshot = telemetryFixture('2026-10-25T00:00:00Z', []);
  api.get.mockResolvedValue({
    success: true,
    status: 200,
    data: {
      server: snapshot.server,
      telemetry: snapshot.telemetry,
      inventory: null,
      capabilities: {
        protocol: 'Redfish',
        supportsInventory: true,
        supportedMetrics: [],
        powerActions: [],
        warnings: [],
      },
      powerOperations: [],
    },
  });
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const view = render(
    <App>
      <QueryClientProvider client={client}>
        <ServerDetail />
      </QueryClientProvider>
    </App>,
  );
  await screen.findByText('stored inventory snapshot');
  expect(Boolean(screen.queryByText('uid operations panel'))).toBe(
    manageServers,
  );
  expect(Boolean(screen.queryByRole('button', { name: /oob.edit/ }))).toBe(
    manageServers,
  );
  expect(
    Boolean(screen.queryByRole('button', { name: 'oob.inventory.refresh' })),
  ).toBe(manageServers);
  expect(Boolean(screen.queryByRole('tab', { name: 'oob.power' }))).toBe(
    operatePower,
  );
  fireEvent.click(screen.getByRole('tab', { name: 'oob.realtime' }));
  await screen.findByText('stored telemetry snapshot');
  expect(
    Boolean(screen.queryByRole('button', { name: 'oob.telemetry.sample' })),
  ).toBe(manageServers);
  if (operatePower) {
    fireEvent.click(screen.getByRole('tab', { name: 'oob.power' }));
    expect(
      await screen.findByText('power operations panel'),
    ).toBeInTheDocument();
  }
  expect(api.sample).not.toHaveBeenCalled();
  expect(api.inventory).not.toHaveBeenCalled();
  view.unmount();
  client.clear();
});

it.each([
  [390, 12],
  [800, 6],
  [1280, 4],
])('服务器基本信息在%ipx按%i行显示，不产生Descriptions span警告', async (width, rows) => {
  const media = vi.spyOn(window, 'matchMedia').mockImplementation((query) => ({
    matches: /min-width:\s*(\d+)px/.test(query)
      ? width >= Number(query.match(/min-width:\s*(\d+)px/)?.[1])
      : /max-width:\s*(\d+)px/.test(query)
        ? width <= Number(query.match(/max-width:\s*(\d+)px/)?.[1])
        : false,
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(),
  }));
  const error = vi.spyOn(console, 'error').mockImplementation(() => undefined);
  const snapshot = telemetryFixture('2026-10-25T00:00:00Z', []);
  api.get.mockResolvedValue({
    success: true,
    status: 200,
    data: {
      server: snapshot.server,
      telemetry: snapshot.telemetry,
      inventory: null,
      capabilities: {
        protocol: 'Redfish',
        supportsInventory: true,
        supportedMetrics: [],
        powerActions: [],
        warnings: [],
      },
      powerOperations: [],
    },
  });
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const view = render(
    <App>
      <QueryClientProvider client={client}>
        <ServerDetail />
      </QueryClientProvider>
    </App>,
  );
  await waitFor(() =>
    expect(
      view.container.querySelectorAll('.ant-descriptions-row'),
    ).toHaveLength(rows),
  );
  expect(
    error.mock.calls.some((args) =>
      args.join(' ').includes('Sum of column `span`'),
    ),
  ).toBe(false);
  view.unmount();
  error.mockRestore();
  media.mockRestore();
});
