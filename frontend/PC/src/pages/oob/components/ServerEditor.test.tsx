import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ServerDto } from '@/services/oob/models';

const testConnection = vi.fn();
const saveServer = vi.fn();
vi.mock('@/services/oob/api', () => ({
  testConnection: (...args: unknown[]) => testConnection(...args),
  saveServer: (...args: unknown[]) => saveServer(...args),
}));
vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));

import ServerEditor from './ServerEditor';

const server: ServerDto = {
  id: '123',
  name: 'fixture-server',
  brand: 'HP',
  controllerType: 'iLO4',
  address: 'https://192.0.2.10/',
  username: 'fixture',
  samplingIntervalSeconds: 30,
  enabled: true,
  ignoreCertificateErrors: true,
  certificateFingerprint: null,
  connectionStatus: 'Online',
  lastCollectedAt: null,
  lastInventoryAt: null,
  lastError: null,
  createdAt: '',
  updatedAt: '',
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
};

function mount(existing: ServerDto | null = server) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const content = (record: ServerDto | null, open = true) => (
    <App>
      <QueryClientProvider client={client}>
        <ServerEditor
          open={open}
          server={record || undefined}
          onClose={() => undefined}
          onSaved={() => undefined}
        />
      </QueryClientProvider>
    </App>
  );
  const view = render(content(existing));
  return {
    ...view,
    rerenderServer: (record: ServerDto | null, open = true) =>
      view.rerender(content(record, open)),
  };
}

describe('设备凭据编辑边界', () => {
  beforeEach(() => {
    testConnection.mockReset().mockResolvedValue({
      success: true,
      status: 200,
      data: {
        success: true,
        identity: server.identity,
        capabilities: {
          protocol: 'Redfish',
          supportsInventory: true,
          supportedMetrics: [],
          powerActions: [],
          warnings: [],
        },
        warnings: [],
      },
    });
    saveServer
      .mockReset()
      .mockResolvedValue({ success: true, status: 200, data: server });
  });

  it('编辑不回填密码，原目标连接测试只携带原记录标识', async () => {
    mount();
    const password = await screen.findByLabelText('oob.password', {
      selector: 'input',
    });
    expect(password).toHaveValue('');
    await waitFor(() =>
      expect(
        screen.getByLabelText('oob.name', { selector: 'input' }),
      ).toHaveValue(server.name),
    );
    fireEvent.click(screen.getByRole('button', { name: 'oob.test' }));
    await waitFor(() => expect(testConnection).toHaveBeenCalledTimes(1));
    expect(testConnection.mock.calls[0][0]).toMatchObject({
      existingServerId: '123',
      address: server.address,
      ignoreCertificateErrors: true,
    });
    expect(testConnection.mock.calls[0][0].password).toBeUndefined();
  });

  it('新增默认勾选忽略证书错误，连接测试和保存均明确发送true', async () => {
    mount(null);
    const checkbox = await screen.findByRole('checkbox', {
      name: 'oob.ignoreCertificateErrors',
    });
    expect(checkbox).toBeChecked();
    fireEvent.change(screen.getByLabelText('oob.name', { selector: 'input' }), {
      target: { value: 'fixture-new-server' },
    });
    fireEvent.change(
      screen.getByLabelText('oob.address', { selector: 'input' }),
      {
        target: { value: 'https://192.0.2.20/' },
      },
    );
    fireEvent.change(
      screen.getByLabelText('oob.username', { selector: 'input' }),
      {
        target: { value: 'fixture-user' },
      },
    );
    fireEvent.change(
      screen.getByLabelText('oob.password', { selector: 'input' }),
      {
        target: { value: 'fixture-password' },
      },
    );
    fireEvent.click(screen.getByRole('button', { name: 'oob.test' }));
    await waitFor(() => expect(testConnection).toHaveBeenCalledTimes(1));
    expect(testConnection.mock.calls[0][0].ignoreCertificateErrors).toBe(true);
    await waitFor(() => expect(checkbox).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: 'oob.save' }));
    await waitFor(() => expect(saveServer).toHaveBeenCalledTimes(1));
    expect(saveServer.mock.calls[0][0].ignoreCertificateErrors).toBe(true);
    expect(saveServer.mock.calls[0][1]).toBeUndefined();
  });

  it('取消勾选会向测试和保存发送false，不要求原目标重新输入密码', async () => {
    mount();
    const checkbox = await screen.findByRole('checkbox', {
      name: 'oob.ignoreCertificateErrors',
    });
    expect(checkbox).toBeChecked();
    fireEvent.click(checkbox);
    expect(checkbox).not.toBeChecked();
    fireEvent.click(screen.getByRole('button', { name: 'oob.test' }));
    await waitFor(() => expect(testConnection).toHaveBeenCalledTimes(1));
    expect(testConnection.mock.calls[0][0]).toMatchObject({
      existingServerId: server.id,
      ignoreCertificateErrors: false,
    });
    expect(testConnection.mock.calls[0][0].password).toBeUndefined();
    await waitFor(() => expect(checkbox).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: 'oob.save' }));
    await waitFor(() => expect(saveServer).toHaveBeenCalledTimes(1));
    expect(saveServer.mock.calls[0][0]).toMatchObject({
      ignoreCertificateErrors: false,
      existingServerId: server.id,
    });
    expect(saveServer.mock.calls[0][0].password).toBeUndefined();
  });

  it('编辑已保存为false的服务器时不被默认勾选覆盖', async () => {
    mount({ ...server, ignoreCertificateErrors: false });
    const checkbox = await screen.findByRole('checkbox', {
      name: 'oob.ignoreCertificateErrors',
    });
    expect(checkbox).not.toBeChecked();
    fireEvent.click(screen.getByRole('button', { name: 'oob.test' }));
    await waitFor(() => expect(testConnection).toHaveBeenCalledTimes(1));
    expect(testConnection.mock.calls[0][0].ignoreCertificateErrors).toBe(false);
  });

  it('同id后台推送不覆盖草稿和测试反馈，关闭重开才加载最新保存值', async () => {
    const view = mount();
    const checkbox = await screen.findByRole('checkbox', {
      name: 'oob.ignoreCertificateErrors',
    });
    const name = screen.getByLabelText('oob.name', { selector: 'input' });
    fireEvent.change(name, { target: { value: 'fixture-draft-name' } });
    fireEvent.click(checkbox);
    fireEvent.click(screen.getByRole('button', { name: 'oob.test' }));
    await screen.findByText('oob.test.success');
    const latest = {
      ...server,
      name: 'fixture-latest-saved-name',
      lastCollectedAt: '2026-10-25T01:00:00Z',
      updatedAt: '2026-10-25T01:00:01Z',
    };
    view.rerenderServer(latest);
    expect(
      screen.getByLabelText('oob.name', { selector: 'input' }),
    ).toHaveValue('fixture-draft-name');
    expect(
      screen.getByRole('checkbox', { name: 'oob.ignoreCertificateErrors' }),
    ).not.toBeChecked();
    expect(screen.getByText('oob.test.success')).toBeInTheDocument();
    expect(testConnection.mock.calls[0][0]).toMatchObject({
      name: 'fixture-draft-name',
      ignoreCertificateErrors: false,
    });

    view.rerenderServer(latest, false);
    view.rerenderServer(latest, true);
    await waitFor(() =>
      expect(
        screen.getByLabelText('oob.name', { selector: 'input' }),
      ).toHaveValue('fixture-latest-saved-name'),
    );
    expect(
      screen.getByRole('checkbox', { name: 'oob.ignoreCertificateErrors' }),
    ).toBeChecked();
    expect(screen.queryByText('oob.test.success')).not.toBeInTheDocument();
  });

  it('弹窗保持打开但显式切换另一台服务器时重新初始化表单', async () => {
    const view = mount();
    const name = await screen.findByLabelText('oob.name', {
      selector: 'input',
    });
    fireEvent.change(name, { target: { value: 'fixture-old-draft' } });
    view.rerenderServer({
      ...server,
      id: '456',
      name: 'fixture-second-server',
      ignoreCertificateErrors: false,
    });
    await waitFor(() =>
      expect(
        screen.getByLabelText('oob.name', { selector: 'input' }),
      ).toHaveValue('fixture-second-server'),
    );
    expect(
      screen.getByRole('checkbox', { name: 'oob.ignoreCertificateErrors' }),
    ).not.toBeChecked();
  });

  it('更换目标而不重输密码时阻断连接测试，避免转发旧秘密', async () => {
    mount();
    const address = await screen.findByLabelText('oob.address', {
      selector: 'input',
    });
    fireEvent.change(address, { target: { value: 'https://192.0.2.11/' } });
    fireEvent.click(screen.getByRole('button', { name: 'oob.test' }));
    await waitFor(() =>
      expect(
        screen.getAllByText('oob.password.reentry').length,
      ).toBeGreaterThan(0),
    );
    expect(testConnection).not.toHaveBeenCalled();
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /oob.test/ })).not.toHaveClass(
        'ant-btn-loading',
      ),
    );
    const password = screen.getByLabelText('oob.password', {
      selector: 'input',
    });
    fireEvent.change(password, { target: { value: 'fixture-new-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'oob.test' }));
    await waitFor(() => expect(testConnection).toHaveBeenCalledTimes(1));
    expect(testConnection.mock.calls[0][0]).toMatchObject({
      address: 'https://192.0.2.11/',
      password: 'fixture-new-password',
    });
  });
});

it('服务器保存被业务拒绝时显示原失败并保留弹窗，不触发成功回调', async () => {
  const closed = vi.fn();
  const saved = vi.fn();
  saveServer.mockReset().mockResolvedValue({
    success: false,
    data: null,
    errorCode: 'SERVER_EXISTS',
    errorMessage: 'fixture duplicate server',
    status: 409,
    kind: 'business',
    dispatched: true,
  });
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  render(
    <App>
      <QueryClientProvider client={client}>
        <ServerEditor open server={server} onClose={closed} onSaved={saved} />
      </QueryClientProvider>
    </App>,
  );
  fireEvent.click(await screen.findByRole('button', { name: 'oob.save' }));
  await screen.findByText('fixture duplicate server');
  expect(closed).not.toHaveBeenCalled();
  expect(saved).not.toHaveBeenCalled();
  expect(screen.getByRole('dialog')).toBeInTheDocument();
});
