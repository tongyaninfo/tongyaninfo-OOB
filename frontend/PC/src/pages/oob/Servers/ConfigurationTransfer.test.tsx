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
import { beginSession, expireSession } from '@/services/oob/session';

const exported = vi.fn();
const imported = vi.fn();
vi.mock('@/services/oob/api', () => ({
  exportServerConfiguration: (...args: unknown[]) => exported(...args),
  importServerConfiguration: (...args: unknown[]) => imported(...args),
}));
vi.mock('../intl', () => ({
  useOobIntl: () => (key: string, values?: Record<string, string | number>) =>
    `${key}${values ? ` ${JSON.stringify(values)}` : ''}`,
}));

import ConfigurationTransfer from './ConfigurationTransfer';

const encrypted = {
  format: 'fixture-encrypted-configuration',
  version: 1,
  salt: 'fixture-salt',
  nonce: 'fixture-nonce',
  ciphertext: 'fixture-ciphertext',
  tag: 'fixture-tag',
};
const filePassword = 'fixture-file-password';

function mount(mode: 'import' | 'export' = 'import') {
  const closed = vi.fn();
  const saved = vi.fn();
  const view = render(
    <App>
      <ConfigurationTransfer mode={mode} onClose={closed} onImported={saved} />
    </App>,
  );
  return { ...view, closed, saved };
}

function enterPassword(value = filePassword) {
  fireEvent.change(screen.getByLabelText('oob.configuration.password'), {
    target: { value },
  });
}

function selectFile(text = JSON.stringify(encrypted), bytes?: number) {
  const file = new File([text], 'configuration.json', {
    type: 'application/json',
  });
  const read = vi.fn().mockResolvedValue(text);
  Object.defineProperty(file, 'text', { value: read });
  if (bytes !== undefined)
    Object.defineProperty(file, 'size', { value: bytes });
  fireEvent.change(screen.getByLabelText('oob.configuration.file'), {
    target: { files: [file] },
  });
  return { file, read };
}

beforeEach(() => {
  vi.clearAllMocks();
  beginSession();
  exported
    .mockReset()
    .mockResolvedValue({ success: true, data: encrypted, status: 200 });
  imported.mockReset().mockResolvedValue({
    success: true,
    data: { added: 1, updated: 2, skipped: 3 },
    status: 200,
  });
});
afterEach(() => vi.restoreAllMocks());

describe('加密服务器配置导入导出', () => {
  it('打开与选择文件均不提交，明确导入后显示结果并刷新列表', async () => {
    const view = mount();
    enterPassword();
    const { read } = selectFile();
    expect(exported).not.toHaveBeenCalled();
    expect(imported).not.toHaveBeenCalled();
    expect(read).not.toHaveBeenCalled();
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.configuration.import' }),
    );
    await waitFor(() => expect(imported).toHaveBeenCalledOnce());
    expect(imported.mock.calls[0][0]).toEqual({
      password: filePassword,
      file: encrypted,
    });
    expect(imported.mock.calls[0][1]).toBeInstanceOf(AbortSignal);
    expect(
      await screen.findByText(
        'oob.configuration.importResult {"added":1,"updated":2,"skipped":3}',
      ),
    ).toBeInTheDocument();
    expect(view.saved).toHaveBeenCalledOnce();
    expect(view.closed).not.toHaveBeenCalled();
    expect(
      screen.queryByLabelText('oob.configuration.password'),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'oob.configuration.import' }),
    ).not.toBeInTheDocument();
  });

  it('重复导入跳过数量原样展示，业务拒绝不刷新或关闭弹窗', async () => {
    imported.mockResolvedValueOnce({
      success: false,
      errorCode: 'CONFIGURATION_PASSWORD_INVALID',
      errorMessage: 'fixture invalid password',
      kind: 'business',
      dispatched: true,
      status: 400,
    });
    const view = mount();
    enterPassword();
    selectFile();
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.configuration.import' }),
    );
    await screen.findByText('fixture invalid password');
    expect(view.saved).not.toHaveBeenCalled();
    expect(view.closed).not.toHaveBeenCalled();
    imported.mockResolvedValueOnce({
      success: true,
      data: { added: 0, updated: 0, skipped: 6 },
      status: 200,
    });
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'oob.configuration.import' }),
      ).toBeEnabled(),
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.configuration.import' }),
    );
    await screen.findByText(
      'oob.configuration.importResult {"added":0,"updated":0,"skipped":6}',
    );
    expect(view.saved).toHaveBeenCalledOnce();
  });

  it.each([
    'short',
    '        ',
  ])('密码不符合要求时不读取文件或请求：%s', async (password) => {
    mount();
    enterPassword(password);
    const { read } = selectFile();
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.configuration.import' }),
    );
    await screen.findByText('oob.configuration.passwordInvalid');
    expect(read).not.toHaveBeenCalled();
    expect(imported).not.toHaveBeenCalled();
  });

  it('缺文件或大于4MiB时不请求', async () => {
    mount();
    enterPassword();
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.configuration.import' }),
    );
    await screen.findByText('oob.configuration.fileRequired');
    const { read } = selectFile('{}', 4 * 1024 * 1024 + 1);
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.configuration.import' }),
    );
    await screen.findByText('oob.configuration.fileTooLarge');
    expect(read).not.toHaveBeenCalled();
    expect(imported).not.toHaveBeenCalled();
  });

  it.each([
    'not json',
    '{}',
    JSON.stringify({ ...encrypted, password: 'fixture-device-password' }),
  ])('格式错误或包含额外明文字段时不请求：%s', async (text) => {
    mount();
    enterPassword();
    selectFile(text);
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.configuration.import' }),
    );
    await screen.findByText('oob.configuration.fileInvalid');
    expect(imported).not.toHaveBeenCalled();
  });

  it('导出确认密码匹配后仅下载服务端密文，不附带筛选或设备明文', async () => {
    const download = vi
      .spyOn(HTMLAnchorElement.prototype, 'click')
      .mockImplementation(() => undefined);
    const blobUrl = vi
      .spyOn(URL, 'createObjectURL')
      .mockReturnValue('blob:fixture-download');
    const view = mount('export');
    enterPassword();
    fireEvent.change(
      screen.getByLabelText('oob.configuration.confirmPassword'),
      { target: { value: 'fixture-other-password' } },
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.configuration.export' }),
    );
    await screen.findByText('oob.configuration.passwordMismatch');
    expect(exported).not.toHaveBeenCalled();
    fireEvent.change(
      screen.getByLabelText('oob.configuration.confirmPassword'),
      { target: { value: filePassword } },
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.configuration.export' }),
    );
    await waitFor(() => expect(view.closed).toHaveBeenCalledOnce());
    expect(exported.mock.calls[0][0]).toEqual({ password: filePassword });
    expect(download).toHaveBeenCalledOnce();
    const blob = blobUrl.mock.calls[0][0] as Blob;
    expect(JSON.parse(await blob.text())).toEqual(encrypted);
    expect(view.saved).not.toHaveBeenCalled();
  });

  it('处理中防重复提交，网络中断只显示未知结果且不自动重试', async () => {
    let finish: (value: unknown) => void = () => undefined;
    imported.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    const view = mount();
    enterPassword();
    selectFile();
    const submit = screen.getByRole('button', {
      name: 'oob.configuration.import',
    });
    fireEvent.click(submit);
    fireEvent.click(submit);
    await waitFor(() => expect(imported).toHaveBeenCalledOnce());
    expect(
      screen.getByRole('button', { name: 'oob.configuration.close' }),
    ).toBeDisabled();
    await act(async () =>
      finish({
        success: false,
        errorCode: 'NETWORK_ERROR',
        errorMessage: 'fixture network',
        kind: 'network',
        dispatched: true,
        status: 0,
      }),
    );
    await screen.findByText('oob.configuration.importUnknown');
    expect(imported).toHaveBeenCalledOnce();
    expect(view.saved).not.toHaveBeenCalled();
    expect(view.closed).not.toHaveBeenCalled();
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.configuration.reload' }),
    );
    expect(view.saved).toHaveBeenCalledOnce();
  });

  it.each([
    'unmount',
    'new-session',
    'expired-session',
  ])('请求迟到后不回写或下载：%s', async (ending) => {
    let finish: (value: unknown) => void = () => undefined;
    exported.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    const download = vi.spyOn(URL, 'createObjectURL');
    const view = mount('export');
    enterPassword();
    fireEvent.change(
      screen.getByLabelText('oob.configuration.confirmPassword'),
      { target: { value: filePassword } },
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.configuration.export' }),
    );
    await waitFor(() => expect(exported).toHaveBeenCalledOnce());
    if (ending === 'unmount') view.unmount();
    else if (ending === 'new-session') beginSession();
    else act(() => expireSession());
    await act(async () =>
      finish({ success: true, data: encrypted, status: 200 }),
    );
    expect(download).not.toHaveBeenCalled();
    expect(view.saved).not.toHaveBeenCalled();
    expect(view.closed).toHaveBeenCalledTimes(
      ending === 'expired-session' ? 1 : 0,
    );
    if (ending !== 'new-session')
      expect(exported.mock.calls[0][1].aborted).toBe(true);
  });
});
