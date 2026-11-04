import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { App } from 'antd';
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type {
  McpCallLogDto,
  McpClientDto,
  McpConfigurationDto,
} from '@/services/oob/models';
import { beginSession } from '@/services/oob/session';

const api = vi.hoisted(() => ({
  configuration: vi.fn(),
  saveConfiguration: vi.fn(),
  state: vi.fn(),
  tools: vi.fn(),
  clients: vi.fn(),
  client: vi.fn(),
  options: vi.fn(),
  saveClient: vi.fn(),
  changeClient: vi.fn(),
  rotate: vi.fn(),
  logs: vi.fn(),
  log: vi.fn(),
  access: { canAdmin: true },
}));
vi.mock('@/services/oob/mcp', () => ({
  getMcpConfiguration: api.configuration,
  saveMcpConfiguration: api.saveConfiguration,
  setMcpEnabled: api.state,
  getMcpTools: api.tools,
  getMcpClients: api.clients,
  getMcpClient: api.client,
  getMcpOptions: api.options,
  saveMcpClient: api.saveClient,
  changeMcpClient: api.changeClient,
  rotateMcpSecret: api.rotate,
  getMcpLogs: api.logs,
  getMcpLog: api.log,
}));
vi.mock('@umijs/max', () => ({
  useAccess: () => api.access,
  useModel: () => ({
    initialState: { currentUser: { userid: '9007199254740999' } },
  }),
}));
vi.mock('@ant-design/pro-components', () => ({
  PageContainer: ({ children }: { children: React.ReactNode }) => (
    <div>{children}</div>
  ),
}));
vi.mock('@/pages/oob/intl', () => ({ useOobIntl: () => (key: string) => key }));
vi.mock('@/services/oob/preferences', () => ({
  getTablePreference: () => undefined,
  updateTablePreference: vi.fn(),
}));

import McpLogs, { shanghaiInput, shanghaiIso, validLogRange } from '../McpLogs';
import ClientEditor from './ClientEditor';
import Clients from './Clients';
import Configuration from './Configuration';
import Mcp from './index';

const configuration: McpConfigurationDto = {
  enabled: false,
  version: '9007199254740993',
  supported: true,
  ready: true,
  endpoint: 'https://oob.example.com/mcp',
  protocolVersions: ['2026-07-28', '2025-11-25'],
  readinessErrors: [],
  requestsPerMinute: 60,
  clientConcurrency: 4,
  globalConcurrency: 16,
  queryTimeoutSeconds: 30,
  maxRequestBytes: 262144,
  maxResponseBytes: 2097152,
  enabledTools: [],
};
const client: McpClientDto = {
  id: '9007199254740995',
  name: 'fixture client',
  oAuthClientId: 'oob-fixture',
  clientType: 'confidential',
  userId: '9007199254740997',
  enabled: false,
  expiresAt: null,
  serverScope: 'SelectedServers',
  version: '9007199254740999',
  tools: [],
  serverIds: ['9007199254740996'],
  networks: ['192.0.2.0/24'],
  origins: ['https://client.example.com'],
  redirectUris: ['https://client.example.com/oauth/callback'],
  createdAt: '2026-11-02T00:00:00Z',
  updatedAt: '2026-11-02T00:00:00Z',
  lastCalledAt: null,
};
const log: McpCallLogDto = {
  id: '9007199254740993',
  callId: 'fixture-call',
  occurredAt: '2026-11-02T01:00:00Z',
  completedAt: '2026-11-02T01:00:01Z',
  clientId: client.id,
  clientName: client.name,
  userId: client.userId,
  method: 'tools/call',
  tool: 'oob_get_server',
  serverId: '9007199254740996',
  sourceIp: '192.0.2.1',
  protocolVersion: '2026-07-28',
  result: 'Succeeded',
  errorCode: null,
  httpStatusCode: 200,
  durationMilliseconds: '1000',
  traceId: 'fixture-trace',
  requestId: 'fixture-request',
  parametersSummary: '<script>untrusted</script>',
  outputSummary: 'safe summary',
  policySummary: 'allowlisted',
  exceptionId: null,
};
const ok = <T,>(data: T) => ({ success: true as const, data, status: 200 });
function view(element: React.ReactNode) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <App>
      <QueryClientProvider client={queryClient}>{element}</QueryClientProvider>
    </App>,
  );
}
beforeEach(() => {
  beginSession();
  for (const value of Object.values(api))
    if (typeof value === 'function') value.mockReset();
  api.access.canAdmin = true;
  api.configuration.mockResolvedValue(ok(configuration));
  api.tools.mockResolvedValue(ok([]));
  api.clients.mockResolvedValue(
    ok({ items: [client], total: 41, current: 1, pageSize: 20 }),
  );
  api.client.mockResolvedValue(ok(client));
  api.options.mockResolvedValue(
    ok({
      users: [
        {
          id: client.userId,
          displayName: 'Fixture user',
          username: 'fixture',
          enabled: true,
        },
      ],
      servers: [
        {
          id: '9007199254740996',
          name: 'Fixture server',
          brand: 'Dell',
          enabled: true,
        },
      ],
    }),
  );
  api.logs.mockResolvedValue(
    ok({ items: [log], total: 41, current: 1, pageSize: 20 }),
  );
  api.log.mockResolvedValue(ok(log));
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('MCP配置与管理授权', () => {
  it('普通用户直达两个管理页面不发管理查询', () => {
    api.access.canAdmin = false;
    view(
      <>
        <Mcp />
        <McpLogs />
      </>,
    );
    expect(api.configuration).not.toHaveBeenCalled();
    expect(api.tools).not.toHaveBeenCalled();
    expect(api.logs).not.toHaveBeenCalled();
  });
  it('页面挂载仅读取，保存保留长版本且不启用；冲突保留草稿并阻止覆盖', async () => {
    api.saveConfiguration.mockResolvedValue({
      success: false,
      status: 409,
      kind: 'business',
      dispatched: true,
      errorCode: 'MCP_VERSION_CONFLICT',
      errorMessage: 'conflict',
    });
    view(<Configuration account="fixture" tools={[]} />);
    const timeout = await screen.findByLabelText('mcp.timeout');
    expect(api.state).not.toHaveBeenCalled();
    expect(api.rotate).not.toHaveBeenCalled();
    fireEvent.change(timeout, { target: { value: '45' } });
    fireEvent.click(screen.getByRole('button', { name: 'mcp.save' }));
    await screen.findByText('mcp.conflict');
    expect(api.saveConfiguration).toHaveBeenCalledWith(
      expect.objectContaining({
        expectedVersion: configuration.version,
        queryTimeoutSeconds: 45,
      }),
    );
    expect(timeout).toHaveValue('45');
    expect(screen.getByRole('button', { name: 'mcp.save' })).toBeDisabled();
    expect(api.state).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'mcp.reload' }));
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'mcp.save' })).toBeEnabled(),
    );
    expect(timeout).toHaveValue('30');
  });
  it('切换身份后的迟到成功不覆盖原草稿', async () => {
    let resolve: (value: ReturnType<typeof ok<McpConfigurationDto>>) => void =
      () => undefined;
    api.saveConfiguration.mockImplementation(
      () =>
        new Promise((done) => {
          resolve = done;
        }),
    );
    view(<Configuration account="fixture" tools={[]} />);
    const timeout = await screen.findByLabelText('mcp.timeout');
    fireEvent.change(timeout, { target: { value: '40' } });
    fireEvent.click(screen.getByRole('button', { name: 'mcp.save' }));
    await waitFor(() => expect(api.saveConfiguration).toHaveBeenCalledTimes(1));
    beginSession();
    resolve(ok({ ...configuration, queryTimeoutSeconds: 80 }));
    await waitFor(() => expect(timeout).toHaveValue('40'));
    expect(api.state).not.toHaveBeenCalled();
  });
  it('保存结果未知后保留草稿、禁止重复提交并要求重新核对', async () => {
    api.saveConfiguration.mockResolvedValue({
      success: false,
      status: 0,
      kind: 'network',
      dispatched: true,
      errorCode: 'NETWORK_ERROR',
      errorMessage: 'network',
    });
    view(<Configuration account="fixture" tools={[]} />);
    const timeout = await screen.findByLabelText('mcp.timeout');
    fireEvent.change(timeout, { target: { value: '42' } });
    fireEvent.click(screen.getByRole('button', { name: 'mcp.save' }));
    await screen.findByText('mcp.unknown');
    expect(timeout).toHaveValue('42');
    expect(screen.getByRole('button', { name: 'mcp.save' })).toBeDisabled();
    expect(api.saveConfiguration).toHaveBeenCalledTimes(1);
    expect(api.state).not.toHaveBeenCalled();
  });
  it('编辑调用方使用完整选项和正式长ID版本；保存不隐式启用或发证', async () => {
    api.saveClient.mockResolvedValue(ok(client));
    const saved = vi.fn();
    view(
      <ClientEditor
        id={client.id}
        account="fixture"
        tools={[]}
        onClose={vi.fn()}
        onSaved={saved}
      />,
    );
    const name = await screen.findByLabelText('mcp.name');
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'mcp.save' })).toBeEnabled(),
    );
    fireEvent.change(name, { target: { value: 'Updated client' } });
    fireEvent.click(screen.getByRole('button', { name: 'mcp.save' }));
    await waitFor(() => expect(saved).toHaveBeenCalledTimes(1));
    expect(api.saveClient).toHaveBeenCalledWith(
      expect.objectContaining({
        name: 'Updated client',
        userId: client.userId,
        expectedVersion: client.version,
        networks: client.networks,
        serverIds: client.serverIds,
      }),
      client.id,
    );
    expect(api.changeClient).not.toHaveBeenCalled();
    expect(api.rotate).not.toHaveBeenCalled();
  });
  it('选项加载失败不提交不完整授权', async () => {
    api.options.mockResolvedValue({
      success: false,
      status: 503,
      errorMessage: 'unavailable',
    });
    view(
      <ClientEditor
        id={client.id}
        account="fixture"
        tools={[]}
        onClose={vi.fn()}
        onSaved={vi.fn()}
      />,
    );
    await screen.findByText('mcp.optionsFailed');
    expect(screen.getByRole('button', { name: 'mcp.save' })).toBeDisabled();
    expect(api.saveClient).not.toHaveBeenCalled();
  });
  it('保密客户端缺少密钥的409显示实际原因，不冒充版本冲突', async () => {
    api.changeClient.mockResolvedValue({
      success: false,
      status: 409,
      kind: 'business',
      dispatched: true,
      errorCode: 'MCP_SECRET_REQUIRED',
      errorMessage: 'fixture secret required',
    });
    view(<Clients account="fixture" tools={[]} />);
    fireEvent.click(await screen.findByRole('button', { name: 'mcp.enable' }));
    const buttons = screen.getAllByRole('button', { name: 'mcp.enable' });
    fireEvent.click(buttons[buttons.length - 1]);
    await screen.findByText('fixture secret required');
    expect(screen.queryByText('mcp.conflict')).not.toBeInTheDocument();
    expect(api.changeClient).toHaveBeenCalledWith(client.id, 'enable', {
      expectedVersion: client.version,
    });
  });
  it('轮换必须显式确认，secret仅保留当前弹框且关闭即清除', async () => {
    api.rotate.mockResolvedValue(
      ok({ clientId: client.id, secret: 'fixture-once-secret' }),
    );
    view(<Clients account="fixture" tools={[]} />);
    fireEvent.click(await screen.findByRole('button', { name: 'mcp.rotate' }));
    expect(api.rotate).not.toHaveBeenCalled();
    const buttons = screen.getAllByRole('button', { name: 'mcp.rotate' });
    fireEvent.click(buttons[buttons.length - 1]);
    await screen.findByDisplayValue('fixture-once-secret');
    expect(api.rotate).toHaveBeenCalledWith(client.id, {
      expectedVersion: client.version,
    });
    fireEvent.click(screen.getByRole('button', { name: 'mcp.closeSecret' }));
    await waitFor(() =>
      expect(
        screen.queryByDisplayValue('fixture-once-secret'),
      ).not.toBeInTheDocument(),
    );
    expect(localStorage.setItem).not.toHaveBeenCalledWith(
      expect.anything(),
      expect.stringContaining('fixture-once-secret'),
    );
  });
});

describe('MCP调用日志', () => {
  it('默认降序列点击后可切换升序，再次点击恢复降序', async () => {
    view(<McpLogs />);
    await screen.findByRole('button', { name: 'mcp.details' });
    const header = screen.getByRole('columnheader', {
      name: 'mcp.logs.occurredAt',
    });
    fireEvent.click(header);
    await waitFor(() =>
      expect(api.logs).toHaveBeenLastCalledWith(
        expect.objectContaining({
          sortField: 'occurredAt',
          sortOrder: 'ascend',
          current: 1,
        }),
        expect.any(AbortSignal),
      ),
    );
    fireEvent.click(header);
    await waitFor(() =>
      expect(api.logs).toHaveBeenLastCalledWith(
        expect.objectContaining({
          sortField: 'occurredAt',
          sortOrder: 'descend',
        }),
        expect.any(AbortSignal),
      ),
    );
  });
  it('时间在上海展示且仅转换一次，拒绝无效或超过31天的查询范围', () => {
    expect(shanghaiInput('2026-11-01T16:00:00Z')).toBe('2026-11-02T00:00:00');
    expect(shanghaiIso('2026-11-02T00:00:00')).toBe('2026-11-01T16:00:00.000Z');
    expect(validLogRange('2026-10-01T00:00:00Z', '2026-11-01T00:00:00Z')).toBe(
      true,
    );
    expect(validLogRange('2026-10-01T00:00:00Z', '2026-11-02T00:00:00Z')).toBe(
      false,
    );
    expect(validLogRange('invalid', '2026-11-02T00:00:00Z')).toBe(false);
  });
  it('长ID详情、日志纯文本及脱敏复制使用正式响应，筛选排序分页在服务端执行', async () => {
    const copy = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal(
      'navigator',
      Object.create(navigator, { clipboard: { value: { writeText: copy } } }),
    );
    view(<McpLogs />);
    fireEvent.click(await screen.findByRole('button', { name: 'mcp.details' }));
    await screen.findByText('<script>untrusted</script>');
    expect(document.querySelector('script')).toBeNull();
    expect(api.log).toHaveBeenCalledWith(log.id, expect.any(AbortSignal));
    fireEvent.click(screen.getByRole('button', { name: 'mcp.copy' }));
    await waitFor(() => expect(copy).toHaveBeenCalledTimes(1));
    expect(JSON.parse(copy.mock.calls[0][0])).toEqual(log);
    fireEvent.click(screen.getByRole('button', { name: 'mcp.cancel' }));
    fireEvent.change(screen.getByLabelText('mcp.logs.traceId'), {
      target: { value: 'trace-filter' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'mcp.logs.search' }));
    await waitFor(() =>
      expect(api.logs).toHaveBeenLastCalledWith(
        expect.objectContaining({ traceId: 'trace-filter', current: 1 }),
        expect.any(AbortSignal),
      ),
    );
    const table = screen.getByRole('region', { name: 'oob.table.settings' });
    fireEvent.keyDown(table, { key: 'ArrowRight' });
    await waitFor(() =>
      expect(api.logs).toHaveBeenLastCalledWith(
        expect.objectContaining({ current: 2, traceId: 'trace-filter' }),
        expect.any(AbortSignal),
      ),
    );
  });
  it('403在页面显示明确失败，不能伪装为空成功', async () => {
    api.logs.mockResolvedValue({
      success: false,
      status: 403,
      errorMessage: 'fixture forbidden',
    });
    view(<McpLogs />);
    await screen.findByText('fixture forbidden');
    expect(api.log).not.toHaveBeenCalled();
  });
});
