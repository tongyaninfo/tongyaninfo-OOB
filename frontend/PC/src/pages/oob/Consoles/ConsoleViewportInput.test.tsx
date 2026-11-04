import { act, fireEvent, render, waitFor } from '@testing-library/react';
import React, { createRef } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ConsoleSessionDto } from '@/services/oob/models';
import ConsoleViewport, { type ConsoleViewportHandle } from './ConsoleViewport';

vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));

const session = {
  id: '9007199254740995',
  serverName: '输入测试服务器',
  status: 'Connected',
  frameUrl: '/api/oob/console-sessions/9007199254740995/frame/index.html',
  bridgeProtocol: 'OobConsoleV1',
  inputEnabled: false,
} as ConsoleSessionDto;

beforeEach(() => {
  vi.stubGlobal('isSecureContext', false);
  const setAttribute = HTMLIFrameElement.prototype.setAttribute;
  vi.spyOn(HTMLIFrameElement.prototype, 'setAttribute').mockImplementation(
    function (this: HTMLIFrameElement, name, value) {
      setAttribute.call(this, name, name === 'src' ? 'about:blank' : value);
    },
  );
});
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

async function mount() {
  const events = {
    onInputToggle: vi.fn(),
    onReady: vi.fn(),
    onRelease: vi.fn(),
    onMediaStatus: vi.fn(),
    onError: vi.fn(),
    onInputDisabled: vi.fn(),
  };
  const ref = createRef<ConsoleViewportHandle>();
  let props = {
    active: true,
    inputEnabled: true,
    floating: true,
    minimized: false,
    session,
  };
  const content = () => (
    <div data-console-session={session.id}>
      <input aria-label="local-form" />
      <div data-console-keyboard>
        <button type="button">screen-escape</button>
      </div>
      <ConsoleViewport
        ref={ref}
        {...props}
        inputToggleDisabled={false}
        {...events}
      />
    </div>
  );
  const view = render(content());
  const iframe = view.container.querySelector('iframe') as HTMLIFrameElement;
  const viewport = view.getByRole('application');
  await waitFor(() => expect(iframe.contentWindow).not.toBeNull());
  vi.spyOn(iframe, 'getBoundingClientRect').mockReturnValue(
    new DOMRect(0, 0, 640, 480),
  );
  const post = vi
    .spyOn(iframe.contentWindow as Window, 'postMessage')
    .mockImplementation(() => {});
  fireEvent.load(iframe);
  const challenge = post.mock.calls.findLast(
    ([message]) => message.type === 'oob-console-handshake',
  )?.[0].challenge;
  const bridge = (data: Record<string, unknown>) =>
    act(() =>
      window.dispatchEvent(
        new MessageEvent('message', {
          source: iframe.contentWindow,
          origin: window.location.origin,
          data: { ...data, sessionId: session.id, challenge },
        }),
      ),
    );
  bridge({ type: 'oob-console-ready', bridgeProtocol: 'OobConsoleV1' });
  post.mockClear();
  const resume = () =>
    fireEvent.pointerDown(viewport, {
      pointerId: 1,
      button: 0,
      buttons: 1,
      clientX: 30,
      clientY: 40,
    });
  return {
    ...view,
    iframe,
    viewport,
    post,
    events,
    ref,
    bridge,
    resume,
    inputs: () =>
      post.mock.calls
        .filter(([message]) => message.type === 'oob-console-input')
        .map(([message]) => message.event),
    rerenderProps: (patch: Partial<typeof props>) => {
      props = { ...props, ...patch };
      view.rerender(content());
    },
  };
}

describe('控制台授权与局部输入焦点分离', () => {
  it('明确进入全屏的成功捕获可恢复桥接，失焦后普通focus仍不能重新捕获', async () => {
    const test = await mount();
    fireEvent.click(
      test.getByRole('button', { name: 'oob.console.fullscreen' }),
    );
    await waitFor(() =>
      expect(
        test.container.querySelector('[data-fullscreen-mode="page"]'),
      ).not.toBeNull(),
    );
    expect(
      test.post.mock.calls.some(
        ([message]) =>
          message.type === 'oob-console-input-state' && message.enabled,
      ),
    ).toBe(true);
    fireEvent.keyDown(test.viewport, { code: 'KeyA', key: 'a' });
    expect(test.inputs()).toContainEqual({
      type: 'key',
      code: 'KeyA',
      key: 'a',
      down: true,
    });
    act(() => window.dispatchEvent(new Event('blur')));
    test.post.mockClear();
    act(() => window.dispatchEvent(new Event('focus')));
    fireEvent.keyDown(test.viewport, { code: 'KeyB', key: 'b' });
    expect(test.post).not.toHaveBeenCalled();
  });

  it('保留授权后本会话普通表单的Ctrl+Alt属于本地，不发送或截获', async () => {
    const test = await mount();
    test.resume();
    const localForm = test.getByRole('textbox', { name: 'local-form' });
    act(() => localForm.focus());
    test.post.mockClear();
    const releaseCount = test.events.onRelease.mock.calls.length;
    const chord = new KeyboardEvent('keydown', {
      code: 'AltLeft',
      key: 'Alt',
      ctrlKey: true,
      altKey: true,
      bubbles: true,
      cancelable: true,
    });
    Object.defineProperty(chord, 'getModifierState', {
      value: (key: string) => key === 'Control' || key === 'Alt',
    });
    fireEvent(localForm, chord);
    expect(chord.defaultPrevented).toBe(false);
    expect(test.events.onRelease).toHaveBeenCalledTimes(releaseCount);
    expect(test.post).not.toHaveBeenCalled();
  });

  it.each([
    'blur',
    'window-blur',
    'bridge-blur',
    'inactive',
    'minimize',
    'suspend',
    'hidden',
    'ctrl-alt',
    'fullscreen-exit',
  ] as const)('%s仅释放和暂停，重新点击原画面可继续且不撤销授权', async (reason) => {
    const test = await mount();
    test.resume();
    fireEvent.keyDown(test.viewport, { code: 'KeyA', key: 'a' });
    test.post.mockClear();
    if (reason === 'blur')
      fireEvent.blur(test.viewport, { relatedTarget: document.body });
    if (reason === 'window-blur')
      act(() => window.dispatchEvent(new Event('blur')));
    if (reason === 'bridge-blur')
      test.bridge({ type: 'oob-console-input-disabled' });
    if (reason === 'inactive') test.rerenderProps({ active: false });
    if (reason === 'minimize') test.rerenderProps({ minimized: true });
    if (reason === 'suspend') act(() => test.ref.current?.suspend());
    if (reason === 'hidden') {
      const visibility = vi
        .spyOn(document, 'visibilityState', 'get')
        .mockReturnValue('hidden');
      act(() => document.dispatchEvent(new Event('visibilitychange')));
      visibility.mockReturnValue('visible');
    }
    if (reason === 'ctrl-alt') {
      const chord = new KeyboardEvent('keydown', {
        code: 'AltLeft',
        key: 'Alt',
        ctrlKey: true,
        altKey: true,
        bubbles: true,
      });
      // Happy DOM 将普通 Alt 也识别为 AltGraph，明确模拟真实左 Ctrl + 左 Alt。
      Object.defineProperty(chord, 'getModifierState', {
        value: (key: string) => key === 'Control' || key === 'Alt',
      });
      fireEvent(test.viewport, chord);
    }
    if (reason === 'fullscreen-exit') {
      fireEvent.click(
        test.getByRole('button', { name: 'oob.console.fullscreen' }),
      );
      await waitFor(() =>
        expect(
          test.container.querySelector('[data-fullscreen-mode="page"]'),
        ).not.toBeNull(),
      );
      fireEvent.click(
        test.getByRole('button', { name: 'oob.console.exitFullscreen' }),
      );
      await waitFor(() =>
        expect(
          test.container.querySelector('[data-fullscreen-mode="window"]'),
        ).not.toBeNull(),
      );
    }
    expect(test.events.onInputDisabled).not.toHaveBeenCalled();
    expect(test.inputs()).toContainEqual({
      type: 'key',
      code: 'KeyA',
      key: 'a',
      down: false,
    });
    expect(test.inputs()).toContainEqual({ type: 'release-all' });
    test.rerenderProps({ active: true, minimized: false });
    test.post.mockClear();
    fireEvent.keyDown(test.viewport, { code: 'KeyB', key: 'b' });
    fireEvent.pointerMove(test.viewport, {
      pointerId: 1,
      clientX: 60,
      clientY: 60,
    });
    fireEvent.pointerUp(test.viewport, {
      pointerId: 1,
      clientX: 60,
      clientY: 60,
    });
    fireEvent.wheel(test.viewport, { deltaY: 100 });
    act(() => window.dispatchEvent(new Event('focus')));
    expect(test.post).not.toHaveBeenCalled();
    test.resume();
    fireEvent.keyDown(test.viewport, { code: 'KeyC', key: 'c' });
    expect(test.post.mock.calls[0][0]).toMatchObject({
      type: 'oob-console-input-state',
      enabled: true,
    });
    expect(test.inputs()).toContainEqual({
      type: 'key',
      code: 'KeyC',
      key: 'c',
      down: true,
    });
    expect(test.container.querySelector('iframe')).toBe(test.iframe);
    expect(test.events.onInputDisabled).not.toHaveBeenCalled();
  });

  it('迟到开启不会重新捕获焦点，显式屏幕键盘按键可重新启用局部桥接', async () => {
    const test = await mount();
    test.resume();
    act(() => test.ref.current?.suspend());
    test.post.mockClear();
    act(() => test.ref.current?.setInput(true));
    fireEvent.keyDown(test.viewport, { code: 'KeyA', key: 'a' });
    expect(test.post).not.toHaveBeenCalled();
    act(() =>
      test.ref.current?.send({
        type: 'key',
        code: 'Escape',
        key: 'Escape',
        down: true,
      }),
    );
    expect(test.post.mock.calls[0][0]).toMatchObject({
      type: 'oob-console-input-state',
      enabled: true,
    });
    expect(test.inputs()).toEqual([
      { type: 'key', code: 'Escape', key: 'Escape', down: true },
    ]);
  });

  it('暂停后在本会话屏幕键盘按物理Esc仍属于明确输入，可恢复桥接', async () => {
    const test = await mount();
    test.resume();
    act(() => test.ref.current?.suspend());
    test.post.mockClear();
    const key = test.getByRole('button', { name: 'screen-escape' });
    act(() => key.focus());
    test.post.mockClear();
    fireEvent.keyDown(key, { code: 'Escape', key: 'Escape' });
    expect(test.post.mock.calls[0]?.[0]).toMatchObject({
      type: 'oob-console-input-state',
      enabled: true,
    });
    expect(test.inputs()).toContainEqual({
      type: 'key',
      code: 'Escape',
      key: 'Escape',
      down: true,
    });
  });

  it('显式关闭立即阻断旧授权props，只有明确重新开启后点击画面才能继续', async () => {
    const test = await mount();
    test.resume();
    act(() => test.ref.current?.setInput(false));
    test.post.mockClear();
    test.resume();
    act(() =>
      test.ref.current?.send({
        type: 'key',
        code: 'KeyA',
        key: 'a',
        down: true,
      }),
    );
    expect(test.post).not.toHaveBeenCalled();
    act(() => test.ref.current?.setInput(true));
    expect(test.post).not.toHaveBeenCalled();
    test.resume();
    expect(test.post.mock.calls[0][0]).toMatchObject({
      type: 'oob-console-input-state',
      enabled: true,
    });
  });

  it.each([
    'authorization',
    'closed',
    'bridge-error',
  ] as const)('%s失效后点击画面及显式按键均不能自动恢复输入', async (reason) => {
    const test = await mount();
    test.resume();
    if (reason === 'authorization') test.rerenderProps({ inputEnabled: false });
    if (reason === 'closed')
      test.rerenderProps({ session: { ...session, status: 'Closed' } });
    if (reason === 'bridge-error')
      test.bridge({ type: 'oob-console-error', message: 'fixture failure' });
    test.post.mockClear();
    test.resume();
    fireEvent.keyDown(test.viewport, { code: 'KeyA', key: 'a' });
    act(() =>
      test.ref.current?.send({
        type: 'key',
        code: 'KeyB',
        key: 'b',
        down: true,
      }),
    );
    expect(test.post).not.toHaveBeenCalled();
  });

  it('原iframe重新加载仍撤销旧授权并重新握手', async () => {
    const test = await mount();
    test.rerenderProps({ session: { ...session, inputEnabled: true } });
    test.resume();
    fireEvent.load(test.iframe);
    expect(test.events.onInputDisabled).toHaveBeenCalledOnce();
    expect(test.events.onReady).toHaveBeenLastCalledWith(false);
  });

  it('真实桥接错误撤销授权，迟到ready或重新聚焦不能恢复旧输入', async () => {
    const test = await mount();
    test.resume();
    fireEvent.keyDown(test.viewport, { code: 'KeyA', key: 'a' });
    test.bridge({
      type: 'oob-console-error',
      message: 'fixture transport failed',
    });
    expect(test.events.onInputDisabled).toHaveBeenCalledOnce();
    expect(test.events.onReady).toHaveBeenLastCalledWith(false);
    expect(test.events.onError).toHaveBeenCalledWith(
      'fixture transport failed',
    );
    expect(test.inputs()).toContainEqual({
      type: 'key',
      code: 'KeyA',
      key: 'a',
      down: false,
    });
    test.bridge({ type: 'oob-console-ready', bridgeProtocol: 'OobConsoleV1' });
    test.post.mockClear();
    test.resume();
    fireEvent.keyDown(test.viewport, { code: 'KeyB', key: 'b' });
    act(() =>
      test.ref.current?.send({
        type: 'key',
        code: 'KeyC',
        key: 'c',
        down: true,
      }),
    );
    expect(test.post).not.toHaveBeenCalled();
  });
});
