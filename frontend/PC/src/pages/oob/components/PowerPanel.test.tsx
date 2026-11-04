import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ServerDetailDto } from '@/services/oob/models';

const execute = vi.fn();
const operations = vi.fn();
const reviewOperation = vi.fn();
const capabilities = vi.fn();
vi.mock('@/services/oob/api', () => ({
  executePowerAction: (...args: unknown[]) => execute(...args),
  getPowerOperations: (...args: unknown[]) => operations(...args),
  getPowerCapabilities: (...args: unknown[]) => capabilities(...args),
  reviewPowerOperation: (...args: unknown[]) => reviewOperation(...args),
}));
vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));

import PowerPanel from './PowerPanel';

const detail: ServerDetailDto = {
  server: {
    id: '123',
    name: 'fixture-server',
    brand: 'HP',
    controllerType: 'iLO4',
    address: 'https://192.0.2.10',
    username: 'fixture',
    samplingIntervalSeconds: 30,
    enabled: true,
    identity: {
      manufacturer: null,
      model: null,
      serialNumber: null,
      hostName: null,
      controllerHostName: null,
      controllerType: null,
      firmwareVersion: null,
      powerState: null,
    },
    certificateFingerprint: null,
    ignoreCertificateErrors: true,
    lastCollectedAt: null,
    lastInventoryAt: null,
    lastError: null,
    connectionStatus: 'Online',
    createdAt: '',
    updatedAt: '',
  },
  capabilities: {
    protocol: 'Redfish',
    supportsInventory: true,
    supportedMetrics: [],
    powerActions: ['On', 'ForceOff'],
    warnings: [],
  },
  inventory: {
    identity: {
      manufacturer: null,
      model: null,
      serialNumber: null,
      hostName: null,
      controllerHostName: null,
      controllerType: null,
      firmwareVersion: null,
      powerState: null,
    },
    collectedAt: '2026-10-24T00:00:00Z',
    components: [],
    warnings: [],
  },
  telemetry: { collectedAt: null, stale: false, metrics: [], warnings: [] },
  powerOperations: [],
};

const powerCapabilities = (overrides = {}) => ({
  success: true,
  status: 200,
  data: {
    enabled: true,
    capabilities: detail.capabilities,
    powerState: 'Off',
    ...overrides,
  },
});

beforeEach(() => {
  capabilities.mockReset().mockResolvedValue(powerCapabilities());
  execute.mockReset().mockResolvedValue({
    success: true,
    status: 200,
    data: {
      id: '456',
      requestId: 'fixture-id',
      action: 'On',
      status: 'Submitted',
      createdAt: '',
    },
  });
  operations
    .mockReset()
    .mockResolvedValue({ success: true, status: 200, data: [] });
  reviewOperation.mockReset();
});

function mount() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <App>
      <QueryClientProvider client={client}>
        <PowerPanel
          server={detail.server}
          initialOperations={detail.powerOperations}
        />
      </QueryClientProvider>
    </App>,
  );
}

describe('真实电源意图确认', () => {
  it('页面挂载只查询记录和实时能力，聚焦与恢复网络不重查能力，绝不发电源命令', async () => {
    mount();
    await waitFor(() => expect(operations).toHaveBeenCalled());
    await waitFor(() => expect(capabilities).toHaveBeenCalledTimes(1));
    fireEvent(window, new Event('focus'));
    fireEvent(window, new Event('online'));
    expect(capabilities).toHaveBeenCalledTimes(1);
    expect(execute).not.toHaveBeenCalled();
  });
  it('动作按钮只打开确认框，精确确认名称后才发一次模拟命令', async () => {
    mount();
    fireEvent.click(
      await screen.findByRole('button', { name: 'oob.power.On' }),
    );
    expect(execute).not.toHaveBeenCalled();
    const submit = await screen.findByRole('button', {
      name: 'oob.power.confirm.submit',
    });
    expect(submit).toBeDisabled();
    fireEvent.change(
      screen.getByRole('textbox', { name: 'oob.power.confirm.name' }),
      { target: { value: 'fixture-server' } },
    );
    expect(submit).toBeEnabled();
    fireEvent.click(submit);
    await waitFor(() => expect(execute).toHaveBeenCalledTimes(1));
    expect(execute).toHaveBeenCalledWith(
      '123',
      expect.objectContaining({
        action: 'On',
        confirmation: 'fixture-server',
        requestId: expect.any(String),
      }),
    );
  });
  it('人工核对只提交审计依据，不调用或重发设备电源动作', async () => {
    const pending = {
      id: '456',
      requestId: 'fixture-review-request',
      action: 'ForceRestart',
      status: 'Unknown',
      message: null,
      createdAt: '2026-10-24T00:00:00Z',
      completedAt: null,
    };
    operations.mockResolvedValue({
      success: true,
      status: 200,
      data: [pending],
    });
    reviewOperation.mockResolvedValue({
      success: true,
      status: 200,
      data: {
        ...pending,
        status: 'ConfirmedByUser',
      },
    });
    mount();
    fireEvent.click(
      await screen.findByRole('button', { name: 'oob.power.review.title' }),
    );
    expect(execute).not.toHaveBeenCalled();
    expect(screen.getByText('oob.power.review.help')).toBeInTheDocument();
    const submit = screen.getByRole('button', {
      name: 'oob.power.review.submit',
    });
    expect(submit).toBeDisabled();
    fireEvent.change(
      screen.getByRole('textbox', { name: 'oob.power.confirm.name' }),
      { target: { value: 'fixture-server' } },
    );
    fireEvent.mouseDown(
      screen.getByRole('combobox', { name: 'oob.power.review.outcome' }),
    );
    fireEvent.click(await screen.findByText('oob.power.review.confirmed'));
    fireEvent.change(
      screen.getByRole('textbox', { name: 'oob.power.review.note' }),
      { target: { value: 'Verified using simulated console evidence.' } },
    );
    expect(submit).toBeEnabled();
    fireEvent.click(submit);
    await waitFor(() => expect(reviewOperation).toHaveBeenCalledTimes(1));
    expect(reviewOperation).toHaveBeenCalledWith('123', '456', {
      confirmation: 'fixture-server',
      outcome: 'Confirmed',
      note: 'Verified using simulated console evidence.',
    });
    expect(execute).not.toHaveBeenCalled();
  });
});

it('实时能力为空时不回退到旧详情动作，刷新能力只发读取请求', async () => {
  capabilities.mockResolvedValue(
    powerCapabilities({
      capabilities: { ...detail.capabilities, powerActions: [] },
    }),
  );
  const view = mount();
  await screen.findByText('oob.power.none');
  expect(
    screen.queryByRole('button', { name: 'oob.power.On' }),
  ).not.toBeInTheDocument();
  capabilities.mockResolvedValue(powerCapabilities());
  fireEvent.click(
    screen.getByRole('button', { name: 'oob.power.refreshCapabilities' }),
  );
  await screen.findByRole('button', { name: 'oob.power.On' });
  expect(capabilities).toHaveBeenCalledTimes(2);
  expect(capabilities).toHaveBeenLastCalledWith(
    detail.server.id,
    expect.any(AbortSignal),
  );
  expect(execute).not.toHaveBeenCalled();
  view.unmount();
});

it('全局开关关闭时解释原因并禁用已发现的动作', async () => {
  capabilities.mockResolvedValue(powerCapabilities({ enabled: false }));
  mount();
  await screen.findByText('oob.power.disabled');
  expect(screen.getByRole('button', { name: 'oob.power.On' })).toBeDisabled();
  expect(
    screen.getByRole('button', { name: 'oob.power.ForceOff' }),
  ).toBeDisabled();
  expect(execute).not.toHaveBeenCalled();
});

it('读取中和读取失败均清空旧动作，失败不自动重试', async () => {
  capabilities.mockReturnValueOnce(new Promise(() => {}));
  const first = mount();
  await screen.findByText('oob.power.loadingCapabilities');
  expect(
    screen.queryByRole('button', { name: 'oob.power.On' }),
  ).not.toBeInTheDocument();
  first.unmount();
  capabilities.mockResolvedValue({
    success: false,
    status: 502,
    data: null,
    errorCode: 'DEVICE_READ_FAILED',
    errorMessage: 'fixture device unavailable',
    dispatched: true,
    kind: 'business',
  });
  mount();
  await screen.findByText('fixture device unavailable');
  expect(
    screen.queryByRole('button', { name: 'oob.power.On' }),
  ).not.toBeInTheDocument();
  expect(capabilities).toHaveBeenCalledTimes(2);
  expect(execute).not.toHaveBeenCalled();
});

it('刷新失败后也不使用上次已成功读取的动作', async () => {
  mount();
  await screen.findByRole('button', { name: 'oob.power.On' });
  capabilities.mockRejectedValue(new Error('fixture refresh failed'));
  fireEvent.click(
    screen.getByRole('button', { name: 'oob.power.refreshCapabilities' }),
  );
  await screen.findByText('fixture refresh failed');
  expect(
    screen.queryByRole('button', { name: 'oob.power.On' }),
  ).not.toBeInTheDocument();
  expect(capabilities).toHaveBeenCalledTimes(2);
  expect(execute).not.toHaveBeenCalled();
});

it.each([
  'On',
  'Off',
  'PoweringOn',
  'PoweringOff',
  'Unknown',
  null,
])('按实时电源状态 %s 限制可执行动作', async (powerState) => {
  const powerActions = [
    'On',
    'PushPowerButton',
    'ForceOff',
    'GracefulShutdown',
    'ForceRestart',
  ];
  capabilities.mockResolvedValue(
    powerCapabilities({
      powerState,
      capabilities: { ...detail.capabilities, powerActions },
    }),
  );
  mount();
  await screen.findByRole('button', { name: 'oob.power.PushPowerButton' });
  for (const action of powerActions) {
    const button = screen.getByRole('button', { name: `oob.power.${action}` });
    if (
      (powerState === 'Off' && ['On', 'PushPowerButton'].includes(action)) ||
      (powerState === 'On' && action !== 'On')
    )
      expect(button).toBeEnabled();
    else expect(button).toBeDisabled();
  }
  if (!['On', 'Off'].includes(powerState || ''))
    expect(screen.getByText('oob.power.stateUnavailable')).toBeInTheDocument();
  expect(execute).not.toHaveBeenCalled();
});

it('短按按钮使用独立动作与说明，仍需精确名称确认且连点只发送一次', async () => {
  capabilities.mockResolvedValue(
    powerCapabilities({
      powerState: 'On',
      capabilities: {
        ...detail.capabilities,
        powerActions: ['PushPowerButton', 'ForceOff', 'GracefulShutdown'],
      },
    }),
  );
  execute.mockReturnValue(new Promise(() => {}));
  mount();
  fireEvent.click(
    await screen.findByRole('button', { name: 'oob.power.PushPowerButton' }),
  );
  expect(
    screen.getAllByText('oob.power.description.PushPowerButton'),
  ).toHaveLength(2);
  expect(
    screen.getByText('oob.power.description.ForceOff'),
  ).toBeInTheDocument();
  expect(
    screen.getByText('oob.power.description.GracefulShutdown'),
  ).toBeInTheDocument();
  const submit = screen.getByRole('button', {
    name: 'oob.power.confirm.submit',
  });
  const input = screen.getByRole('textbox', { name: 'oob.power.confirm.name' });
  expect(submit).toBeDisabled();
  fireEvent.change(input, { target: { value: `${detail.server.name} ` } });
  expect(submit).toBeDisabled();
  expect(execute).not.toHaveBeenCalled();
  fireEvent.change(input, { target: { value: detail.server.name } });
  fireEvent.click(submit);
  fireEvent.click(submit);
  await waitFor(() => expect(execute).toHaveBeenCalledTimes(1));
  expect(execute).toHaveBeenCalledWith(
    detail.server.id,
    expect.objectContaining({
      action: 'PushPowerButton',
      confirmation: detail.server.name,
      requestId: expect.any(String),
    }),
  );
  expect(input).toBeDisabled();
});

it('明确业务拒绝不抛异常、不进入结果未知，网络结果未知保留原RequestId且不重发', async () => {
  execute.mockReset().mockResolvedValue({
    success: false,
    data: null,
    errorCode: 'POWER_CONTROL_DISABLED',
    errorMessage: 'disabled',
    kind: 'business',
    status: 403,
    dispatched: true,
  });
  operations
    .mockReset()
    .mockResolvedValue({ success: true, data: [], status: 200 });
  const view = mount();
  const submit = async () => {
    fireEvent.click(
      await screen.findByRole('button', { name: 'oob.power.On' }),
    );
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'oob.power.confirm.name' }),
      { target: { value: 'fixture-server' } },
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.power.confirm.submit' }),
    );
  };
  await submit();
  await waitFor(() => expect(execute).toHaveBeenCalledTimes(1));
  await screen.findByText('disabled');
  expect(screen.queryByText('oob.power.unknown')).not.toBeInTheDocument();
  view.unmount();
  execute.mockReset().mockResolvedValue({
    success: false,
    data: null,
    errorCode: 'NETWORK_ERROR',
    errorMessage: 'lost',
    kind: 'network',
    status: 0,
    dispatched: true,
  });
  mount();
  await submit();
  await screen.findByText('oob.power.unknown');
  expect(
    screen.getByText(execute.mock.calls[0][1].requestId),
  ).toBeInTheDocument();
  expect(execute).toHaveBeenCalledTimes(1);
  expect(screen.getByRole('button', { name: 'oob.power.On' })).toBeDisabled();
});

it.each([
  'IDEMPOTENCY_CONFLICT',
  'POWER_CONFLICT',
  'SERVER_NOT_FOUND',
  'VALIDATION_FAILED',
])('发送前明确拒绝%s不会留下永久未知意图', async (errorCode) => {
  execute.mockReset().mockResolvedValue({
    success: false,
    data: null,
    errorCode,
    errorMessage: `rejected ${errorCode}`,
    kind: 'business',
    status: 409,
    dispatched: true,
  });
  operations
    .mockReset()
    .mockResolvedValue({ success: true, data: [], status: 200 });
  mount();
  fireEvent.click(await screen.findByRole('button', { name: 'oob.power.On' }));
  fireEvent.change(
    await screen.findByRole('textbox', { name: 'oob.power.confirm.name' }),
    { target: { value: 'fixture-server' } },
  );
  fireEvent.click(
    screen.getByRole('button', { name: 'oob.power.confirm.submit' }),
  );
  await screen.findByText(`rejected ${errorCode}`);
  expect(screen.queryByText('oob.power.unknown')).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'oob.power.On' })).toBeEnabled();
  expect(execute).toHaveBeenCalledTimes(1);
});

it.each([
  { errorCode: 'POWER_PRECHECK_FAILED', kind: 'business', unknown: false },
  { errorCode: 'DEVICE_ERROR', kind: 'business', unknown: true },
  { errorCode: 'POWER_PRECHECK_FAILED', kind: 'network', unknown: true },
])('502/$kind/$errorCode 仅正式预检失败可以按未发送继续人工操作', async ({
  errorCode,
  kind,
  unknown,
}) => {
  execute.mockResolvedValue({
    success: false,
    data: null,
    errorCode,
    errorMessage: 'fixture power precheck response',
    kind,
    status: 502,
    dispatched: true,
  });
  mount();
  fireEvent.click(await screen.findByRole('button', { name: 'oob.power.On' }));
  fireEvent.change(
    await screen.findByRole('textbox', { name: 'oob.power.confirm.name' }),
    { target: { value: detail.server.name } },
  );
  fireEvent.click(
    screen.getByRole('button', { name: 'oob.power.confirm.submit' }),
  );
  await screen.findByText('fixture power precheck response');
  const button = screen.getByRole('button', { name: 'oob.power.On' });
  const requestId = execute.mock.calls[0][1].requestId;
  if (unknown) {
    expect(screen.getByText('oob.power.unknown')).toBeInTheDocument();
    expect(screen.getByText(requestId)).toBeInTheDocument();
    expect(button).toBeDisabled();
  } else {
    expect(screen.queryByText('oob.power.unknown')).not.toBeInTheDocument();
    expect(screen.queryByText(requestId)).not.toBeInTheDocument();
    expect(button).toBeEnabled();
    fireEvent.click(button);
    const submit = await screen.findByRole('button', {
      name: 'oob.power.confirm.submit',
    });
    expect(submit).toBeDisabled();
    expect(
      screen.getByRole('textbox', { name: 'oob.power.confirm.name' }),
    ).toHaveValue('');
  }
  // 允许重新表达人工意图不等于自动重发；打开新确认框也不发送第二个 POST。
  expect(execute).toHaveBeenCalledTimes(1);
});
