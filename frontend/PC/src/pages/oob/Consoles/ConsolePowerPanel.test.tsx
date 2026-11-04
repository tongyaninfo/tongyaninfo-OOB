import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import React from 'react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { RequestResult } from '@/services/oob/http';
import type { PowerOperationDto } from '@/services/oob/models';
import ConsolePowerPanel, {
  type ConsolePowerPanelProps,
} from './ConsolePowerPanel';

const api = vi.hoisted(() => ({
  capabilities: vi.fn(),
  operations: vi.fn(),
  execute: vi.fn(),
  review: vi.fn(),
  detail: vi.fn(),
}));
const access = vi.hoisted(() => ({
  canOperatePower: true,
  canManageServers: false,
}));
vi.mock('@umijs/max', () => ({ useAccess: () => access }));
vi.mock('@/services/oob/api', () => ({
  getPowerCapabilities: api.capabilities,
  getPowerOperations: api.operations,
  executePowerAction: api.execute,
  reviewPowerOperation: api.review,
  getServer: api.detail,
}));
vi.mock('@/services/oob/browserExceptions', () => ({
  captureRuntimeException: vi.fn(),
}));
vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));

const server = { id: '9007199254740993', name: 'console-power-server' };
const clients: QueryClient[] = [];
const ok = <T,>(data: T) => ({ success: true as const, status: 200, data });
const capability = {
  enabled: true,
  powerState: 'Off',
  capabilities: {
    protocol: 'Redfish',
    supportsInventory: true,
    supportedMetrics: [],
    powerActions: ['On', 'ForceOff'],
    warnings: [],
  },
};

beforeEach(() => {
  access.canOperatePower = true;
  access.canManageServers = false;
  for (const mock of Object.values(api)) mock.mockReset();
  api.capabilities.mockResolvedValue(ok(capability));
  api.operations.mockResolvedValue(ok([]));
});
afterEach(() => {
  cleanup();
  for (const client of clients.splice(0)) client.clear();
  vi.useRealTimers();
});

function mount(initial: Partial<ConsolePowerPanelProps> = {}) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  clients.push(client);
  let props: ConsolePowerPanelProps = { server, ...initial };
  const element = () => (
    <ConfigProvider theme={{ token: { motion: false } }}>
      <App>
        <QueryClientProvider client={client}>
          <div data-testid="power-modal-host" />
          <ConsolePowerPanel {...props} />
        </QueryClientProvider>
      </App>
    </ConfigProvider>
  );
  const view = render(element());
  return {
    ...view,
    change: (next: Partial<ConsolePowerPanelProps>) => {
      props = { ...props, ...next };
      view.rerender(element());
    },
  };
}

async function confirmOn() {
  fireEvent.click(await screen.findByRole('button', { name: 'oob.power.On' }));
  const input = await screen.findByRole('textbox', {
    name: 'oob.power.confirm.name',
  });
  fireEvent.change(input, { target: { value: server.name } });
  fireEvent.click(
    screen.getByRole('button', { name: 'oob.power.confirm.submit' }),
  );
}

it('没有电源权限时不挂载控件，不读取能力、记录或完整服务器详情', () => {
  access.canOperatePower = false;
  mount();
  expect(screen.queryByText('oob.power.actions')).not.toBeInTheDocument();
  expect(api.capabilities).not.toHaveBeenCalled();
  expect(api.operations).not.toHaveBeenCalled();
  expect(api.detail).not.toHaveBeenCalled();
  expect(api.execute).not.toHaveBeenCalled();
});

it('无需服务器管理权限即可只读加载；交互先释放输入并使用控制台弹层容器，名称确认仍必需', async () => {
  const beforeInteraction = vi.fn();
  mount({
    beforeInteraction,
    getContainer: () =>
      screen.queryByTestId('power-modal-host') || document.body,
  });
  const on = await screen.findByRole('button', { name: 'oob.power.On' });
  expect(on).toBeEnabled();
  expect(api.capabilities).toHaveBeenCalledWith(
    server.id,
    expect.any(AbortSignal),
  );
  expect(api.operations).toHaveBeenCalledWith(
    server.id,
    expect.any(AbortSignal),
  );
  expect(api.detail).not.toHaveBeenCalled();
  expect(api.execute).not.toHaveBeenCalled();
  fireEvent.click(on);
  expect(beforeInteraction).toHaveBeenCalled();
  const dialog = await within(
    screen.getByTestId('power-modal-host'),
  ).findByRole('dialog');
  const submit = within(dialog).getByRole('button', {
    name: 'oob.power.confirm.submit',
  });
  expect(submit).toBeDisabled();
  fireEvent.change(
    within(dialog).getByRole('textbox', { name: 'oob.power.confirm.name' }),
    { target: { value: `${server.name} ` } },
  );
  expect(submit).toBeDisabled();
  expect(api.execute).not.toHaveBeenCalled();
});

it('隐藏标签不发起读取或轮询，恢复后只读加载，离开时取消尚未提交的确认', async () => {
  const view = mount({ active: false });
  expect(api.capabilities).not.toHaveBeenCalled();
  expect(api.operations).not.toHaveBeenCalled();
  expect(
    screen.getByRole('button', { name: 'oob.power.refreshCapabilities' }),
  ).toBeDisabled();
  view.change({ active: true });
  fireEvent.click(await screen.findByRole('button', { name: 'oob.power.On' }));
  await screen.findByRole('dialog');
  view.change({ active: false });
  await waitFor(() =>
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
  );
  const getCounts = [
    api.capabilities.mock.calls.length,
    api.operations.mock.calls.length,
  ];
  vi.useFakeTimers();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(31000);
  });
  expect(api.capabilities).toHaveBeenCalledTimes(getCounts[0]);
  expect(api.operations).toHaveBeenCalledTimes(getCounts[1]);
  vi.useRealTimers();
  view.change({ active: true });
  await screen.findByRole('button', { name: 'oob.power.On' });
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  expect(api.execute).not.toHaveBeenCalled();
});

it('切换到隐藏标签期间的在途响应保留原请求未知状态，恢复后不重发或丢失锁定', async () => {
  let finish!: (result: RequestResult<PowerOperationDto>) => void;
  api.execute.mockReturnValue(
    new Promise<RequestResult<PowerOperationDto>>((resolve) => {
      finish = resolve;
    }),
  );
  const view = mount();
  await confirmOn();
  await waitFor(() => expect(api.execute).toHaveBeenCalledTimes(1));
  const requestId = api.execute.mock.calls[0][1].requestId;
  view.change({ active: false });
  await waitFor(() =>
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
  );
  const reads = api.operations.mock.calls.length;
  await act(async () => {
    finish({
      success: false,
      data: null,
      status: 0,
      kind: 'network',
      dispatched: true,
      errorCode: 'NETWORK_ERROR',
      errorMessage: 'fixture response lost',
    });
  });
  expect(api.operations).toHaveBeenCalledTimes(reads);
  view.change({ active: true });
  await screen.findByText('oob.power.unknown');
  expect(screen.getByText(requestId)).toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'oob.power.On' })).toBeDisabled();
  expect(api.execute).toHaveBeenCalledTimes(1);
  expect(api.execute).toHaveBeenCalledWith(
    server.id,
    expect.objectContaining({ requestId, confirmation: server.name }),
  );
});

it('切换服务器后旧响应仅归属旧目标，不锁定新服务器或复用旧确认', async () => {
  let finish!: (result: RequestResult<PowerOperationDto>) => void;
  api.execute.mockReturnValue(
    new Promise<RequestResult<PowerOperationDto>>((resolve) => {
      finish = resolve;
    }),
  );
  const view = mount();
  await confirmOn();
  await waitFor(() => expect(api.execute).toHaveBeenCalledTimes(1));
  const originalRequestId = api.execute.mock.calls[0][1].requestId;
  const next = { id: '9007199254740995', name: 'console-second-server' };
  view.change({ server: next });
  await waitFor(() =>
    expect(api.capabilities).toHaveBeenCalledWith(
      next.id,
      expect.any(AbortSignal),
    ),
  );
  await act(async () => {
    finish({
      success: false,
      data: null,
      status: 0,
      kind: 'network',
      dispatched: true,
      errorCode: 'NETWORK_ERROR',
      errorMessage: 'fixture old response lost',
    });
  });
  expect(screen.queryByText(originalRequestId)).not.toBeInTheDocument();
  expect(screen.queryByText('oob.power.unknown')).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'oob.power.On' })).toBeEnabled();
  expect(api.execute).toHaveBeenCalledTimes(1);
});
