import {
  onlineManager,
  QueryClient,
  QueryClientProvider,
} from '@tanstack/react-query';
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { App } from 'antd';
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type {
  MailConfigurationDto,
  MailMessageDto,
} from '@/services/oob/models';

const api = vi.hoisted(() => ({
  configuration: vi.fn(),
  save: vi.fn(),
  probe: vi.fn(),
  send: vi.fn(),
  request: vi.fn(),
  list: vi.fn(),
  detail: vi.fn(),
}));
const session = vi.hoisted(() => ({ value: 0 }));
const locale = vi.hoisted(() => ({ value: 'key' }));
vi.mock('@/services/oob/session', () => ({
  sessionGeneration: () => session.value,
}));
vi.mock('@/services/oob/mail', () => ({
  getMailConfiguration: api.configuration,
  saveMailConfiguration: api.save,
  probeMailConnection: api.probe,
  sendMail: api.send,
  getMailRequest: api.request,
  getMailMessages: api.list,
  getMailMessage: api.detail,
}));
vi.mock('../intl', () => ({
  useOobIntl: () => (key: string) =>
    locale.value === 'en' && key === 'oob.mail.connected'
      ? 'SMTP connection and authentication succeeded'
      : key,
}));

import Configuration from './Configuration';
import Records from './Records';
import SendMailModal from './SendMailModal';

const configuration: MailConfigurationDto = {
  configured: true,
  host: 'smtp.example.test',
  port: 465,
  securityMode: 'SslOnConnect',
  fromAddress: 'sender@example.test',
  username: 'sender@example.test',
  passwordConfigured: true,
  version: '9007199254740993',
  updatedAt: '2026-11-02T00:00:00Z',
};
const record: MailMessageDto = {
  id: '9007199254740993',
  requestId: 'original-request',
  kind: 'Manual',
  fromAddress: 'sender@example.test',
  toAddress: 'reader@example.test',
  subject: 'Fixture mail',
  textBody: '<script>text remains text</script>',
  htmlBody: null,
  status: 'Accepted',
  errorCode: null,
  message: 'Safe accepted summary',
  createdByUserId: '9007199254740995',
  createdAt: '2026-11-02T00:00:00Z',
  completedAt: '2026-11-02T00:00:01Z',
};
const success = <T,>(data: T) => ({ success: true, data, status: 200 });
const clients: QueryClient[] = [];
function mount(children: React.ReactNode) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  clients.push(client);
  return render(
    <App>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </App>,
  );
}
beforeEach(() => {
  vi.resetAllMocks();
  session.value = 0;
  locale.value = 'key';
  api.configuration.mockResolvedValue(success(configuration));
  api.list.mockResolvedValue(
    success({
      items: [{ ...record, textBody: null }],
      total: 40,
      current: 1,
      pageSize: 20,
    }),
  );
  api.detail.mockResolvedValue(success(record));
});
afterEach(() => {
  for (const client of clients.splice(0)) client.clear();
});

describe('精简SMTP配置与显式收发边界', () => {
  it('页面读取不连接或发送；留空保存密码并保留长版本，连接检测独立执行', async () => {
    api.save.mockResolvedValue(
      success({ ...configuration, version: '9007199254740994' }),
    );
    api.probe.mockResolvedValue(
      success({
        connected: true,
        message: 'Safe probe success',
        errorCode: null,
      }),
    );
    const view = mount(<Configuration onChanged={vi.fn()} />);
    expect(await screen.findByLabelText('oob.mail.host')).toHaveValue(
      configuration.host,
    );
    expect(screen.getByLabelText('oob.mail.password')).toHaveValue('');
    expect(api.send).not.toHaveBeenCalled();
    expect(api.probe).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'oob.mail.save' }));
    await waitFor(() => expect(api.save).toHaveBeenCalledTimes(1));
    expect(api.save.mock.calls[0][0]).toMatchObject({
      host: configuration.host,
      version: '9007199254740993',
      password: undefined,
    });
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'oob.mail.connect' }),
      ).toBeEnabled(),
    );
    expect(api.probe).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'oob.mail.connect' }));
    await screen.findByText('oob.mail.connected');
    locale.value = 'en';
    view.rerender(
      <App>
        <QueryClientProvider client={clients[clients.length - 1]}>
          <Configuration onChanged={vi.fn()} />
        </QueryClientProvider>
      </App>,
    );
    expect(
      await screen.findByText('SMTP connection and authentication succeeded'),
    ).toBeInTheDocument();
    expect(screen.queryByText('oob.mail.connected')).not.toBeInTheDocument();
    expect(api.probe).toHaveBeenCalledTimes(1);
    expect(api.send).not.toHaveBeenCalled();
  });

  it('未保存的改动阻止连接检测；重新载入相同配置也恢复表单和操作', async () => {
    mount(<Configuration onChanged={vi.fn()} />);
    const host = await screen.findByLabelText('oob.mail.host');
    fireEvent.change(host, { target: { value: 'changed.example.test' } });
    expect(
      screen.getByRole('button', { name: 'oob.mail.connect' }),
    ).toBeDisabled();
    expect(
      screen.getByRole('button', { name: 'oob.mail.testSend' }),
    ).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'oob.mail.reload' }));
    await waitFor(() => expect(host).toHaveValue(configuration.host));
    expect(
      screen.getByRole('button', { name: 'oob.mail.connect' }),
    ).toBeEnabled();
    expect(api.save).not.toHaveBeenCalled();
    expect(api.send).not.toHaveBeenCalled();
  });

  it('断线恢复和后台失效重查保留草稿及原并发版本，只有明确重新载入才替换', async () => {
    const remote = {
      ...configuration,
      host: 'other-admin.example.test',
      version: '9007199254740994',
    };
    api.save.mockResolvedValue({
      success: false,
      data: null,
      status: 409,
      kind: 'business',
      dispatched: true,
      errorCode: 'MAIL_CONFIGURATION_CONFLICT',
      errorMessage: 'Fixture configuration changed',
    });
    mount(<Configuration onChanged={vi.fn()} />);
    const host = await screen.findByLabelText('oob.mail.host');
    const password = screen.getByLabelText('oob.mail.password');
    fireEvent.change(host, { target: { value: 'draft.example.test' } });
    fireEvent.change(password, { target: { value: 'draft-app-password' } });
    api.configuration.mockResolvedValue(success(remote));
    await act(async () => {
      onlineManager.setOnline(false);
      onlineManager.setOnline(true);
    });
    expect(api.configuration).toHaveBeenCalledTimes(1);
    const client = clients[clients.length - 1];
    await act(async () => {
      await client.invalidateQueries({
        queryKey: ['oob', 'mail', 'configuration'],
      });
    });
    expect(api.configuration).toHaveBeenCalledTimes(2);
    expect(host).toHaveValue('draft.example.test');
    expect(password).toHaveValue('draft-app-password');
    expect(
      screen.getByRole('button', { name: 'oob.mail.connect' }),
    ).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'oob.mail.save' }));
    await screen.findByText('Fixture configuration changed');
    expect(api.save).toHaveBeenCalledWith(
      expect.objectContaining({
        host: 'draft.example.test',
        password: 'draft-app-password',
        version: configuration.version,
      }),
    );
    expect(host).toHaveValue('draft.example.test');
    expect(password).toHaveValue('draft-app-password');
    fireEvent.click(screen.getByRole('button', { name: 'oob.mail.reload' }));
    await waitFor(() => expect(host).toHaveValue(remote.host));
    expect(password).toHaveValue('');
    expect(
      screen.getByRole('button', { name: 'oob.mail.connect' }),
    ).toBeEnabled();
    expect(api.send).not.toHaveBeenCalled();
    expect(api.probe).not.toHaveBeenCalled();
  });

  it('发送结果未知时锁定原内容并只查询同一请求，不自动重发', async () => {
    api.send.mockResolvedValue({
      success: false,
      data: null,
      status: 0,
      kind: 'network',
      dispatched: true,
      errorCode: 'NETWORK_ERROR',
      errorMessage: 'fixture interrupted',
    });
    api.request.mockResolvedValue(success({ ...record, status: 'Unknown' }));
    mount(<SendMailModal open onClose={vi.fn()} onChanged={vi.fn()} />);
    fireEvent.change(await screen.findByLabelText('oob.mail.recipient'), {
      target: { value: record.toAddress },
    });
    fireEvent.change(screen.getByLabelText('oob.mail.subject'), {
      target: { value: record.subject },
    });
    fireEvent.change(screen.getByLabelText('oob.mail.body'), {
      target: { value: 'Fixture plain text' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'oob.mail.send' }));
    await screen.findByText('oob.mail.unknown');
    const sent = api.send.mock.calls[0][0];
    expect(sent.requestId).toMatch(/^[a-f0-9-]{36}$/);
    expect(screen.getByLabelText('oob.mail.recipient')).toBeDisabled();
    expect(
      screen.queryByRole('button', { name: 'oob.mail.send' }),
    ).not.toBeInTheDocument();
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.mail.checkStatus' }),
    );
    await waitFor(() =>
      expect(api.request).toHaveBeenCalledWith(sent.requestId),
    );
    expect(api.send).toHaveBeenCalledTimes(1);
    expect(
      screen.queryByRole('button', { name: 'oob.mail.retryOriginal' }),
    ).not.toBeInTheDocument();
  });

  it('原请求明确查无记录后，用户重试仍使用原标识与相同内容', async () => {
    api.send
      .mockResolvedValueOnce({
        success: false,
        data: null,
        status: 0,
        kind: 'network',
        dispatched: true,
        errorCode: 'NETWORK_ERROR',
        errorMessage: 'fixture interrupted',
      })
      .mockResolvedValueOnce(success(record));
    api.request.mockResolvedValue({
      success: false,
      data: null,
      status: 404,
      kind: 'business',
      dispatched: true,
      errorCode: 'MAIL_MESSAGE_NOT_FOUND',
      errorMessage: 'Original request not found',
    });
    mount(<SendMailModal open test onClose={vi.fn()} onChanged={vi.fn()} />);
    fireEvent.change(await screen.findByLabelText('oob.mail.recipient'), {
      target: { value: record.toAddress },
    });
    fireEvent.click(screen.getByRole('button', { name: 'oob.mail.testSend' }));
    await screen.findByText('oob.mail.unknown');
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.mail.checkStatus' }),
    );
    fireEvent.click(
      await screen.findByRole('button', { name: 'oob.mail.retryOriginal' }),
    );
    await waitFor(() => expect(api.send).toHaveBeenCalledTimes(2));
    expect(api.send.mock.calls[1]).toEqual(api.send.mock.calls[0]);
    expect(api.send.mock.calls[1][1]).toBe(true);
    await screen.findByText('oob.mail.acceptedHelp');
  });

  it('发件记录左右键交由服务端分页、支持排序与长ID行详情，正文作为文本展示', async () => {
    mount(<Records />);
    await screen.findByText(record.toAddress);
    const table = screen.getByRole('region', { name: 'oob.table.settings' });
    fireEvent.keyDown(table, { key: 'ArrowRight' });
    await waitFor(() =>
      expect(api.list).toHaveBeenLastCalledWith(
        expect.objectContaining({ current: 2 }),
        expect.any(AbortSignal),
      ),
    );
    fireEvent.click(
      screen.getByRole('columnheader', { name: 'oob.mail.recipient' }),
    );
    await waitFor(() =>
      expect(api.list).toHaveBeenLastCalledWith(
        expect.objectContaining({
          current: 1,
          sortField: 'toAddress',
          sortOrder: 'ascend',
        }),
        expect.any(AbortSignal),
      ),
    );
    const row = screen.getByText(record.toAddress).closest('tr');
    expect(row).not.toBeNull();
    if (row) fireEvent.keyDown(row, { key: 'Enter' });
    await screen.findByText(record.textBody || '');
    expect(api.detail).toHaveBeenCalledWith(
      '9007199254740993',
      expect.any(AbortSignal),
    );
    expect(document.querySelector('script')).toBeNull();
    expect(screen.getAllByText('2026-11-02 08:00:01').length).toBeGreaterThan(
      0,
    );
    expect(api.send).not.toHaveBeenCalled();
  });

  it('发送校验拒绝保留草稿，修正字段后才创建新的显式发送', async () => {
    api.send.mockResolvedValue({
      success: false,
      data: null,
      status: 400,
      kind: 'business',
      dispatched: true,
      errorCode: 'MAIL_INVALID_RECIPIENT',
      errorMessage: 'Fixture recipient rejected',
    });
    mount(<SendMailModal open onClose={vi.fn()} onChanged={vi.fn()} />);
    fireEvent.change(await screen.findByLabelText('oob.mail.recipient'), {
      target: { value: record.toAddress },
    });
    fireEvent.change(screen.getByLabelText('oob.mail.subject'), {
      target: { value: record.subject },
    });
    fireEvent.change(screen.getByLabelText('oob.mail.body'), {
      target: { value: 'Draft body remains available' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'oob.mail.send' }));
    await screen.findByText('Fixture recipient rejected');
    expect(screen.getByLabelText('oob.mail.subject')).toHaveValue(
      record.subject,
    );
    expect(screen.getByLabelText('oob.mail.body')).toHaveValue(
      'Draft body remains available',
    );
    expect(screen.getByLabelText('oob.mail.recipient')).toBeEnabled();
    expect(api.send).toHaveBeenCalledTimes(1);
  });

  it('系统报表HTML详情使用无脚本沙箱，普通摘要继续作为文本显示', async () => {
    const htmlBody =
      '<h1>Fixture report</h1><img src="https://external.example.test/image"><script>window.parent.bad = true</script>';
    api.detail.mockResolvedValue(
      success({ ...record, kind: 'System', createdByUserId: null, htmlBody }),
    );
    mount(<Records />);
    fireEvent.click(await screen.findByText(record.toAddress));
    const frame = await screen.findByTitle('oob.mail.reports.previewTitle');
    expect(frame).toHaveAttribute('sandbox', '');
    expect(frame.getAttribute('srcdoc')).toContain("default-src 'none'");
    expect(frame.getAttribute('srcdoc')).toContain(htmlBody);
    expect(screen.getByText(record.textBody || '')).toBeInTheDocument();
    expect(document.querySelector('script')).toBeNull();
    expect(api.send).not.toHaveBeenCalled();
  });

  it('账号切换后忽略旧账号发送响应，不把已受理结果回写到当前页面', async () => {
    let finish:
      | ((value: ReturnType<typeof success<MailMessageDto>>) => void)
      | undefined;
    api.send.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    const changed = vi.fn();
    mount(<SendMailModal open test onClose={vi.fn()} onChanged={changed} />);
    fireEvent.change(await screen.findByLabelText('oob.mail.recipient'), {
      target: { value: record.toAddress },
    });
    fireEvent.click(screen.getByRole('button', { name: 'oob.mail.testSend' }));
    await waitFor(() => expect(api.send).toHaveBeenCalledTimes(1));
    session.value++;
    await act(async () => {
      finish?.(success(record));
    });
    expect(changed).not.toHaveBeenCalled();
    expect(
      screen.queryByText('oob.mail.status.Accepted'),
    ).not.toBeInTheDocument();
    expect(api.send).toHaveBeenCalledTimes(1);
  });
});
