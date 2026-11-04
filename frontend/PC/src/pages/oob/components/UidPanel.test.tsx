import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { RequestResult } from '@/services/oob/http';
import type { UidStatusDto } from '@/services/oob/models';

const api = vi.hoisted(() => ({ get: vi.fn(), set: vi.fn(), log: vi.fn() }));
vi.mock('@/services/oob/api', () => ({ getUid: api.get, setUid: api.set }));
vi.mock('@/services/oob/browserExceptions', () => ({
  captureRuntimeException: api.log,
}));
vi.mock('../intl', async () => {
  const { uidZhCN } = await import('./uidMessages');
  const messages: Record<string, string> = {
    ...uidZhCN,
    'oob.cancel': '取消',
    'oob.observedAt': '观测时间',
  };
  return {
    useOobIntl:
      () =>
      (key: string, values: Record<string, string> = {}) =>
        Object.entries(values).reduce(
          (text, [name, value]) => text.replaceAll(`{${name}}`, value),
          messages[key] || key,
        ),
  };
});

import UidPanel from './UidPanel';

const server = { id: '9007199254740993', name: 'fixture-server' };
const off: UidStatusDto = {
  state: 'Off',
  canControl: true,
  supportedStates: ['Off', 'Lit'],
  observedAt: '2026-10-26T00:00:00Z',
  message: null,
};
const lit: UidStatusDto = { ...off, state: 'Lit' };
const ok = (data: UidStatusDto): RequestResult<UidStatusDto> => ({
  success: true,
  status: 200,
  data,
});
const clients: QueryClient[] = [];
function mount(current = server) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  clients.push(client);
  const element = (value: typeof server) => (
    <ConfigProvider theme={{ token: { motion: false } }}>
      <App>
        <QueryClientProvider client={client}>
          <UidPanel server={value} />
        </QueryClientProvider>
      </App>
    </ConfigProvider>
  );
  return { ...render(element(current)), element };
}
async function turnOn() {
  const button = await screen.findByRole('button', { name: '点亮 UID' });
  await waitFor(() => expect(button).toBeEnabled());
  fireEvent.click(button);
}

describe('UID定位灯的直接操作与只读核对', () => {
  beforeEach(() => {
    api.get.mockReset().mockResolvedValue(ok(off));
    api.set.mockReset().mockResolvedValue(ok(lit));
    api.log.mockReset();
  });
  afterEach(() => {
    cleanup();
    for (const client of clients.splice(0)) client.clear();
  });

  it('挂载只读取UID，名称切换、聚焦和恢复网络不会发送写请求', async () => {
    const view = mount();
    await screen.findByText('已熄灭');
    expect(
      screen.queryByRole('button', { name: '闪烁 UID' }),
    ).not.toBeInTheDocument();
    expect(api.get).toHaveBeenCalledWith(server.id, expect.any(AbortSignal));
    expect(api.set).not.toHaveBeenCalled();
    fireEvent(window, new Event('focus'));
    fireEvent(window, new Event('online'));
    view.rerender(view.element(server));
    expect(api.get).toHaveBeenCalledTimes(1);
    expect(api.set).not.toHaveBeenCalled();
  });

  it('点击动作直接发送一次PUT，无确认输入框，连点被阻止且成功后只读核对', async () => {
    let finish!: (value: RequestResult<UidStatusDto>) => void;
    api.set.mockReturnValue(
      new Promise<RequestResult<UidStatusDto>>((resolve) => {
        finish = resolve;
      }),
    );
    api.get.mockResolvedValueOnce(ok(off)).mockResolvedValue(ok(lit));
    mount();
    const on = await screen.findByRole('button', { name: '点亮 UID' });
    await waitFor(() => expect(on).toBeEnabled());
    fireEvent.click(on);
    fireEvent.click(on);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    await waitFor(() => expect(api.set).toHaveBeenCalledTimes(1));
    expect(api.set).toHaveBeenCalledWith(server.id, {
      state: 'Lit',
      confirmation: server.name,
    });
    expect(on).toBeDisabled();
    expect(screen.getByRole('button', { name: '关闭 UID' })).toBeDisabled();
    expect(
      screen.getByRole('button', { name: '刷新 UID 状态' }),
    ).toBeDisabled();
    finish(ok(lit));
    await screen.findByText('UID 状态已更新并完成读取核对。');
    await waitFor(() =>
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
    );
    expect(api.get).toHaveBeenCalledTimes(2);
    expect(screen.getByRole('button', { name: '关闭 UID' })).toBeEnabled();
  });

  it('状态和控制能力以服务端为准，闪烁可关闭且不依赖电源动作', async () => {
    api.get.mockResolvedValue(
      ok({ ...off, state: 'Blinking', supportedStates: ['Off'] }),
    );
    mount();
    await screen.findByText('闪烁');
    expect(
      screen.queryByRole('button', { name: '点亮 UID' }),
    ).not.toBeInTheDocument();
    const button = screen.getByRole('button', { name: '关闭 UID' });
    expect(button).toBeEnabled();
    api.set.mockResolvedValue(ok(off));
    api.get.mockResolvedValue(ok(off));
    fireEvent.click(button);
    await waitFor(() =>
      expect(api.set).toHaveBeenCalledWith(server.id, {
        state: 'Off',
        confirmation: server.name,
      }),
    );
  });

  it('Dell只显示已声明的闪烁和关闭操作，直接点击发送Blinking并等值回读', async () => {
    const dellOff: UidStatusDto = {
      ...off,
      supportedStates: ['Off', 'Blinking'],
    };
    const dellBlinking: UidStatusDto = { ...dellOff, state: 'Blinking' };
    api.get
      .mockResolvedValueOnce(ok(dellOff))
      .mockResolvedValue(ok(dellBlinking));
    api.set.mockResolvedValue(ok(dellBlinking));
    mount();
    await screen.findByText('已熄灭');
    expect(
      screen.queryByRole('button', { name: '点亮 UID' }),
    ).not.toBeInTheDocument();
    const blink = screen.getByRole('button', { name: '闪烁 UID' });
    expect(blink).toBeEnabled();
    fireEvent.click(blink);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await screen.findByText('UID 状态已更新并完成读取核对。');
    await waitFor(() =>
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
    );
    expect(api.set).toHaveBeenCalledTimes(1);
    expect(api.set).toHaveBeenCalledWith(server.id, {
      state: 'Blinking',
      confirmation: server.name,
    });
    expect(api.get).toHaveBeenCalledTimes(2);
    expect(screen.getByText('闪烁', { exact: true })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '闪烁 UID' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '关闭 UID' })).toBeEnabled();
  });

  it('Dell明确拒绝闪烁后不自动尝试常亮或其他UID动作', async () => {
    api.get.mockResolvedValue(
      ok({ ...off, supportedStates: ['Off', 'Blinking'] }),
    );
    api.set.mockResolvedValue({
      success: false,
      data: null,
      errorCode: 'UID_UNSUPPORTED',
      errorMessage: 'fixture blinking rejected',
      status: 409,
      kind: 'business',
      dispatched: true,
    });
    mount();
    await screen.findByText('已熄灭');
    fireEvent.click(screen.getByRole('button', { name: '闪烁 UID' }));
    await screen.findByText('fixture blinking rejected');
    await waitFor(() =>
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
    );
    expect(api.set).toHaveBeenCalledTimes(1);
    expect(api.set).toHaveBeenCalledWith(server.id, {
      state: 'Blinking',
      confirmation: server.name,
    });
    expect(api.get).toHaveBeenCalledTimes(1);
    expect(
      screen.queryByRole('button', { name: '点亮 UID' }),
    ).not.toBeInTheDocument();
  });

  it.each([
    false,
    true,
  ])('UID_UNSUPPORTED/422立即转为只读，手动刷新后按新能力%s决定操作', async (canControl) => {
    api.get.mockResolvedValue(
      ok({ ...off, supportedStates: ['Off', 'Blinking'] }),
    );
    api.set.mockResolvedValue({
      success: false,
      data: null,
      errorCode: 'UID_UNSUPPORTED',
      errorMessage: 'fixture uid capability rejected',
      status: 422,
      kind: 'business',
      dispatched: true,
    });
    mount();
    await screen.findByText('已熄灭');
    fireEvent.click(screen.getByRole('button', { name: '闪烁 UID' }));
    await screen.findByText('fixture uid capability rejected');
    await waitFor(() =>
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
    );
    expect(screen.getByRole('button', { name: '闪烁 UID' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '关闭 UID' })).toBeDisabled();
    expect(screen.getByText('已熄灭', { exact: true })).toBeInTheDocument();
    expect(screen.queryByText('未知', { exact: true })).not.toBeInTheDocument();
    expect(
      screen.getByText('当前设备未提供可用的 UID 控制能力。'),
    ).toBeInTheDocument();
    expect(api.get).toHaveBeenCalledTimes(1);
    expect(api.set).toHaveBeenCalledTimes(1);
    api.get.mockResolvedValue(
      ok({
        ...off,
        canControl,
        supportedStates: canControl ? ['Off', 'Blinking'] : [],
      }),
    );
    fireEvent.click(screen.getByRole('button', { name: '刷新 UID 状态' }));
    await waitFor(() => expect(api.get).toHaveBeenCalledTimes(2));
    if (canControl)
      await waitFor(() =>
        expect(screen.getByRole('button', { name: '闪烁 UID' })).toBeEnabled(),
      );
    else
      await waitFor(() =>
        expect(
          screen.queryByRole('button', { name: '闪烁 UID' }),
        ).not.toBeInTheDocument(),
      );
    expect(api.set).toHaveBeenCalledTimes(1);
  });

  it('读取失败或设备只读时不允许写入，错误信息保留且可主动刷新', async () => {
    api.get.mockResolvedValueOnce({
      success: false,
      data: null,
      errorCode: 'FORBIDDEN',
      errorMessage: 'fixture uid denied',
      status: 403,
      kind: 'business',
      dispatched: true,
    });
    mount();
    await screen.findByText('fixture uid denied');
    expect(
      screen.queryByRole('button', { name: '点亮 UID' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: '关闭 UID' }),
    ).not.toBeInTheDocument();
    api.get.mockResolvedValue(ok({ ...off, canControl: false }));
    fireEvent.click(screen.getByRole('button', { name: '刷新 UID 状态' }));
    await screen.findByText('当前设备未提供可用的 UID 控制能力。');
    expect(api.set).not.toHaveBeenCalled();
  });

  it('明确业务拒绝保留错误，不自动重发或读取确认成功', async () => {
    api.set.mockResolvedValue({
      success: false,
      data: null,
      errorCode: 'FORBIDDEN',
      errorMessage: 'fixture write denied',
      status: 403,
      kind: 'business',
      dispatched: true,
    });
    mount();
    await screen.findByText('已熄灭');
    await turnOn();
    await screen.findByText('fixture write denied');
    await waitFor(() =>
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
    );
    expect(api.set).toHaveBeenCalledTimes(1);
    expect(api.get).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('button', { name: '点亮 UID' })).toBeEnabled();
  });

  it('网络结果未知锁住后续写入，只有手动GET核对后解锁，绝不自动重发', async () => {
    api.set.mockResolvedValue({
      success: false,
      data: null,
      errorCode: 'NETWORK_ERROR',
      errorMessage: 'fixture lost response',
      status: 0,
      kind: 'network',
      dispatched: true,
    });
    mount();
    await screen.findByText('已熄灭');
    await turnOn();
    await screen.findByText(/UID 操作结果尚未确认/);
    await waitFor(() =>
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
    );
    expect(api.set).toHaveBeenCalledTimes(1);
    expect(api.get).toHaveBeenCalledTimes(1);
    expect(screen.getByText('未知', { exact: true })).toBeInTheDocument();
    expect(
      screen.queryByText('已熄灭', { exact: true }),
    ).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: '点亮 UID' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '关闭 UID' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: '刷新 UID 状态' }));
    await screen.findByText('已重新读取 UID 状态，可以按当前状态继续操作。');
    expect(api.get).toHaveBeenCalledTimes(2);
    expect(api.set).toHaveBeenCalledTimes(1);
    expect(screen.getByText('已熄灭', { exact: true })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '点亮 UID' })).toBeEnabled();
  });

  it.each([
    off,
    lit,
  ])('UID_RESULT_UNKNOWN不将写前$state标签继续作为当前状态，手动读取后恢复', async (initial) => {
    api.get.mockResolvedValue(ok(initial));
    api.set.mockResolvedValue({
      success: false,
      data: null,
      errorCode: 'UID_RESULT_UNKNOWN',
      errorMessage: 'fixture result unconfirmed',
      status: 502,
      kind: 'business',
      dispatched: true,
    });
    mount();
    const oldLabel = initial.state === 'Off' ? '已熄灭' : '常亮';
    await screen.findByText(oldLabel, { exact: true });
    const command = initial.state === 'Off' ? '点亮 UID' : '关闭 UID';
    fireEvent.click(screen.getByRole('button', { name: command }));
    await screen.findByText(/UID 操作结果尚未确认/);
    await waitFor(() =>
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
    );
    expect(screen.getByText('未知', { exact: true })).toBeInTheDocument();
    expect(
      screen.queryByText(oldLabel, { exact: true }),
    ).not.toBeInTheDocument();
    expect(api.get).toHaveBeenCalledTimes(1);
    expect(api.set).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: '刷新 UID 状态' }));
    await screen.findByText('已重新读取 UID 状态，可以按当前状态继续操作。');
    expect(screen.getByText(oldLabel, { exact: true })).toBeInTheDocument();
    expect(screen.queryByText('未知', { exact: true })).not.toBeInTheDocument();
    expect(api.get).toHaveBeenCalledTimes(2);
    expect(api.set).toHaveBeenCalledTimes(1);
  });

  it('成功响应后的读取与目标不符时保留待核对，不宣称更新成功', async () => {
    mount();
    await screen.findByText('已熄灭');
    await turnOn();
    await screen.findByText(
      'UID 请求已返回，但未能读取确认结果；请刷新状态核对。',
    );
    await waitFor(() =>
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
    );
    expect(
      screen.queryByText('UID 状态已更新并完成读取核对。'),
    ).not.toBeInTheDocument();
    expect(api.get).toHaveBeenCalledTimes(2);
    expect(api.set).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('button', { name: '点亮 UID' })).toBeDisabled();
  });

  it('PUT返回Unknown也保留待核对，不自动查询或重发', async () => {
    api.set.mockResolvedValue(ok({ ...lit, state: 'Unknown' }));
    mount();
    await screen.findByText('已熄灭');
    await turnOn();
    await screen.findByText(/UID 操作结果尚未确认/);
    await waitFor(() =>
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
    );
    expect(api.get).toHaveBeenCalledTimes(1);
    expect(api.set).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('button', { name: '点亮 UID' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '关闭 UID' })).toBeDisabled();
  });

  it('服务端返回未知状态时仅允许刷新，不把未知状态当作熄灭', async () => {
    api.get.mockResolvedValue(ok({ ...off, state: 'Unknown' }));
    mount();
    await screen.findByText('未知');
    expect(screen.queryByText('已熄灭')).not.toBeInTheDocument();
    expect(
      await screen.findByRole('button', { name: '点亮 UID' }),
    ).toBeDisabled();
    expect(screen.getByRole('button', { name: '关闭 UID' })).toBeDisabled();
    expect(api.set).not.toHaveBeenCalled();
  });

  it('切换服务器会取消旧读取并隔离旧响应', async () => {
    let finish!: (value: RequestResult<UidStatusDto>) => void;
    api.get
      .mockReturnValueOnce(
        new Promise<RequestResult<UidStatusDto>>((resolve) => {
          finish = resolve;
        }),
      )
      .mockResolvedValue(ok(lit));
    const view = mount();
    await waitFor(() => expect(api.get).toHaveBeenCalledTimes(1));
    const oldSignal = api.get.mock.calls[0][1] as AbortSignal;
    view.rerender(
      view.element({ id: '9007199254740995', name: 'fixture-new-server' }),
    );
    await screen.findByText('常亮');
    expect(oldSignal.aborted).toBe(true);
    finish(ok(off));
    await waitFor(() =>
      expect(screen.queryByText('已熄灭')).not.toBeInTheDocument(),
    );
    expect(screen.getByRole('button', { name: '关闭 UID' })).toBeEnabled();
    expect(api.set).not.toHaveBeenCalled();
  });
});
