import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
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
  MailReportConfigurationDto,
  MailReportPreviewDto,
} from '@/services/oob/models';

const api = vi.hoisted(() => ({
  configuration: vi.fn(),
  save: vi.fn(),
  preview: vi.fn(),
}));
const session = vi.hoisted(() => ({ value: 0 }));
vi.mock('@/services/oob/api', () => ({
  getMailReportConfiguration: api.configuration,
  saveMailReportConfiguration: api.save,
  previewMailReport: api.preview,
}));
vi.mock('@/services/oob/session', () => ({
  sessionGeneration: () => session.value,
}));
vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));

import Reports from './Reports';

const configuration: MailReportConfigurationDto = {
  configured: true,
  version: '9007199254740993',
  schedules: ['Daily', 'Weekly', 'Monthly'].map((period) => ({
    period,
    enabled: true,
    toAddress: `${period.toLowerCase()}@example.test`,
    sendTime: '08:00',
    dayOfWeek: 1,
    dayOfMonth: 31,
    effectiveFrom: '2026-11-02T00:00:00Z',
    nextSendAt: '2026-11-03T00:00:00Z',
  })),
};
const report: MailReportPreviewDto = {
  subject: 'Fixture operation report',
  textBody: 'Fixture plain summary',
  htmlBody:
    '<!DOCTYPE html><html><body><h1>Fixture report</h1><img alt="CPU temperature trend" src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jHwAAAABJRU5ErkJggg=="></body></html>',
  from: '2026-11-01T00:00:00Z',
  to: '2026-11-02T00:00:00Z',
};
const success = <T,>(data: T) => ({ success: true, data, status: 200 });
const clients: QueryClient[] = [];
function mount() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  clients.push(client);
  return render(
    <App>
      <QueryClientProvider client={client}>
        <Reports />
      </QueryClientProvider>
    </App>,
  );
}
function selectRange() {
  fireEvent.change(screen.getByLabelText('oob.history.from'), {
    target: { value: '2026-11-01T08:00:00' },
  });
  fireEvent.change(screen.getByLabelText('oob.history.to'), {
    target: { value: '2026-11-02T08:00:00' },
  });
}
beforeEach(() => {
  vi.resetAllMocks();
  session.value = 0;
  api.configuration.mockResolvedValue(success(configuration));
  api.save.mockResolvedValue(
    success({ ...configuration, version: '9007199254740994' }),
  );
  api.preview.mockResolvedValue(success(report));
});
afterEach(() => {
  for (const client of clients.splice(0)) client.clear();
});

describe('运行报表计划与只读预览', () => {
  it('读取不保存或预览，显式保存保留字符串版本及周月日期', async () => {
    mount();
    expect(
      await screen.findByLabelText('oob.mail.reports.Daily oob.mail.recipient'),
    ).toHaveValue('daily@example.test');
    expect(api.save).not.toHaveBeenCalled();
    expect(api.preview).not.toHaveBeenCalled();
    expect(screen.getAllByText(/2026-11-03 08:00:00/)).toHaveLength(3);
    fireEvent.change(
      screen.getByLabelText('oob.mail.reports.Daily oob.mail.recipient'),
      { target: { value: 'changed@example.test' } },
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.mail.reports.save' }),
    );
    await waitFor(() => expect(api.save).toHaveBeenCalledTimes(1));
    expect(api.save).toHaveBeenCalledWith({
      version: '9007199254740993',
      schedules: [
        {
          period: 'Daily',
          enabled: true,
          toAddress: 'changed@example.test',
          sendTime: '08:00',
          dayOfWeek: 1,
          dayOfMonth: 31,
        },
        {
          period: 'Weekly',
          enabled: true,
          toAddress: 'weekly@example.test',
          sendTime: '08:00',
          dayOfWeek: 1,
          dayOfMonth: 31,
        },
        {
          period: 'Monthly',
          enabled: true,
          toAddress: 'monthly@example.test',
          sendTime: '08:00',
          dayOfWeek: 1,
          dayOfMonth: 31,
        },
      ],
    });
    expect(api.preview).not.toHaveBeenCalled();
  });

  it('SMTP尚未配置时提示并阻止启用/保存，仍可显式预览', async () => {
    api.configuration.mockResolvedValue(
      success({ ...configuration, configured: false }),
    );
    mount();
    await screen.findByText('oob.mail.unsaved');
    expect(
      screen.getByRole('button', { name: 'oob.mail.reports.save' }),
    ).toBeDisabled();
    expect(
      screen.getByRole('switch', {
        name: 'oob.mail.reports.Daily oob.mail.reports.enabled',
      }),
    ).toBeDisabled();
    selectRange();
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.mail.reports.preview' }),
    );
    await screen.findByText(report.subject);
    expect(api.save).not.toHaveBeenCalled();
  });

  it('上海时间仅转换一次，预览沙箱限制脚本与外链，下载复用已生成快照', async () => {
    const create = vi
      .spyOn(URL, 'createObjectURL')
      .mockReturnValue('blob:fixture-report');
    const revoke = vi.fn();
    Object.defineProperty(URL, 'revokeObjectURL', {
      configurable: true,
      value: revoke,
    });
    const click = vi
      .spyOn(HTMLAnchorElement.prototype, 'click')
      .mockImplementation(() => {});
    mount();
    await screen.findByLabelText('oob.mail.reports.Daily oob.mail.recipient');
    expect(
      screen.getByRole('button', { name: 'oob.mail.reports.download' }),
    ).toBeDisabled();
    selectRange();
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.mail.reports.preview' }),
    );
    await screen.findByText(report.subject);
    expect(api.preview).toHaveBeenCalledWith(
      {
        from: new Date(report.from).toISOString(),
        to: new Date(report.to).toISOString(),
      },
      expect.any(AbortSignal),
    );
    const frame = screen.getByTitle('oob.mail.reports.previewTitle');
    expect(frame).toHaveAttribute('sandbox', '');
    expect(frame.getAttribute('srcdoc')).toContain("default-src 'none'");
    expect(frame.getAttribute('srcdoc')).toContain(report.htmlBody);
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.mail.reports.download' }),
    );
    expect(create).toHaveBeenCalledWith(expect.any(Blob));
    const downloadedHtml = await (create.mock.calls[0][0] as Blob).text();
    // 下载必须保留生成快照的内嵌图表，不再读取模板或依赖远程图片。
    expect(downloadedHtml).toBe(report.htmlBody);
    expect(downloadedHtml).toContain('data:image/png;base64,');
    expect(downloadedHtml).not.toMatch(/\{\{[A-Z_]+\}\}/);
    expect(click).toHaveBeenCalledTimes(1);
    expect(revoke).toHaveBeenCalledWith('blob:fixture-report');
    expect(api.preview).toHaveBeenCalledTimes(1);
    expect(api.save).not.toHaveBeenCalled();
    create.mockRestore();
    click.mockRestore();
  });

  it('拒绝倒置或超限时间，并在范围改变后清除旧预览下载', async () => {
    mount();
    await screen.findByLabelText('oob.mail.reports.Daily oob.mail.recipient');
    selectRange();
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.mail.reports.preview' }),
    );
    await screen.findByText(report.subject);
    fireEvent.change(screen.getByLabelText('oob.history.from'), {
      target: { value: '2026-11-03T08:00:00' },
    });
    expect(screen.queryByText(report.subject)).not.toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'oob.mail.reports.download' }),
    ).toBeDisabled();
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.mail.reports.preview' }),
    );
    await screen.findByText('oob.mail.reports.invalidRange');
    expect(api.preview).toHaveBeenCalledTimes(1);
    fireEvent.change(screen.getByLabelText('oob.history.from'), {
      target: { value: '2024-01-01T08:00:00' },
    });
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.mail.reports.preview' }),
    );
    await screen.findByText('oob.mail.reports.invalidRange');
    expect(api.preview).toHaveBeenCalledTimes(1);
  });

  it('并发冲突保留草稿和原版本，必须重新载入再保存', async () => {
    api.save.mockResolvedValue({
      success: false,
      status: 409,
      kind: 'business',
      dispatched: true,
      errorMessage: 'Fixture report conflict',
    });
    mount();
    const recipient = await screen.findByLabelText(
      'oob.mail.reports.Daily oob.mail.recipient',
    );
    fireEvent.change(recipient, { target: { value: 'draft@example.test' } });
    await act(async () => {
      api.configuration.mockResolvedValue(
        success({ ...configuration, version: '9007199254740994' }),
      );
      await clients[clients.length - 1].invalidateQueries({
        queryKey: ['oob', 'mail', 'reports', 'configuration'],
      });
    });
    expect(recipient).toHaveValue('draft@example.test');
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.mail.reports.save' }),
    );
    await screen.findByText('Fixture report conflict');
    expect(api.save.mock.calls[0][0].version).toBe('9007199254740993');
    expect(recipient).toHaveValue('draft@example.test');
    expect(
      screen.getByRole('button', { name: 'oob.mail.reports.save' }),
    ).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'oob.mail.reload' }));
    await waitFor(() => expect(recipient).toHaveValue('daily@example.test'));
    expect(
      screen.getByRole('button', { name: 'oob.mail.reports.save' }),
    ).toBeEnabled();
  });

  it('账号切换后丢弃旧预览响应，关闭页面时取消正在生成的查询', async () => {
    let finish:
      | ((value: ReturnType<typeof success<MailReportPreviewDto>>) => void)
      | undefined;
    api.preview.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    const view = mount();
    await screen.findByLabelText('oob.mail.reports.Daily oob.mail.recipient');
    selectRange();
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.mail.reports.preview' }),
    );
    await waitFor(() => expect(api.preview).toHaveBeenCalledTimes(1));
    session.value++;
    await act(async () => {
      finish?.(success(report));
    });
    expect(screen.queryByText(report.subject)).not.toBeInTheDocument();
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.mail.reports.preview' }),
    );
    await waitFor(() => expect(api.preview).toHaveBeenCalledTimes(2));
    const signal = api.preview.mock.calls[1][1] as AbortSignal;
    view.unmount();
    expect(signal.aborted).toBe(true);
  });
});
