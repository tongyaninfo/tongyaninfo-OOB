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
import { requestFailure } from '@/services/oob/http';
import type { ServerDto } from '@/services/oob/models';
import { beginSession } from '@/services/oob/session';

const api = vi.hoisted(() => ({
  capabilities: vi.fn(),
  create: vi.fn(),
  find: vi.fn(),
  close: vi.fn(),
  traffic: vi.fn(),
  input: vi.fn(),
}));
vi.mock('@/services/oob/console', () => ({
  getConsoleCapabilities: api.capabilities,
  createConsoleSession: api.create,
  findConsoleSession: api.find,
  closeConsoleSession: api.close,
  getConsoleTraffic: api.traffic,
  setConsoleInput: api.input,
}));
vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));
vi.mock('./VirtualMediaDrawer', () => ({ default: () => null }));
// 电源面板自身的权限与请求由独立组件测试覆盖，此处验证它与远端输入的隔离。
vi.mock('./ConsolePowerPanel', () => ({
  default: ({ beforeInteraction }: { beforeInteraction: () => void }) => (
    <button type="button" onClick={beforeInteraction}>
      fixture-power-interaction
    </button>
  ),
}));

import ConsoleSession from './ConsoleSession';
import { isConsoleBridgeMessage } from './ConsoleViewport';

const server = {
  id: '9007199254740993',
  name: '测试服务器',
  controllerType: 'Ilo4',
  address: 'https://bmc.invalid',
} as ServerDto;
const session = {
  id: '9007199254740995',
  requestId: 'intent-one',
  serverId: server.id,
  serverName: server.name,
  controllerType: 'Ilo4',
  status: 'Connected',
  createdAt: '2026-10-25T08:00:00Z',
  expiresAt: '2026-10-25T09:00:00Z',
  frameUrl: '/api/oob/console-sessions/9007199254740995/frame/index.html',
  errorCode: null,
  message: null,
  inputEnabled: false,
  bytesReceived: '100',
  bytesSent: '50',
  mediaBytesReceived: '0',
  mediaBytesSent: '0',
  trafficAvailable: true,
  bridgeProtocol: 'OobConsoleV1',
};
const ok = <T,>(data: T) => ({ success: true as const, status: 200, data });
beforeEach(() => {
  beginSession();
  (
    window as unknown as {
      happyDOM: { settings: { disableIframePageLoading: boolean } };
    }
  ).happyDOM.settings.disableIframePageLoading = false;
  const setAttribute = HTMLIFrameElement.prototype.setAttribute;
  vi.spyOn(HTMLIFrameElement.prototype, 'setAttribute').mockImplementation(
    function (this: HTMLIFrameElement, name, value) {
      setAttribute.call(this, name, name === 'src' ? 'about:blank' : value);
    },
  );
  for (const fn of Object.values(api)) fn.mockReset();
  api.capabilities.mockResolvedValue(
    ok({
      serverId: server.id,
      status: 'Available',
      keyboardSupported: true,
      mouseSupported: true,
      localMediaSupported: true,
    }),
  );
  api.create.mockResolvedValue(ok(session));
  api.find.mockResolvedValue(ok(session));
  api.close.mockResolvedValue(ok({ ...session, status: 'Closed' }));
  let inputEnabled = false;
  // 轮询应回显最近一次授权状态，不能按固定夹具把成功开启的输入覆盖为关闭。
  api.traffic.mockImplementation(async () => ok({ ...session, inputEnabled }));
  api.input.mockImplementation(async (_id, enabled) => {
    inputEnabled = enabled;
    return ok({ ...session, inputEnabled });
  });
});
afterEach(() => {
  vi.restoreAllMocks();
});
const mount = () =>
  render(
    <App>
      <ConsoleSession
        server={server}
        active
        getContainer={() => document.body}
      />
    </App>,
  );

/** 保留原生全屏的归属语义，测试完成后还原浏览器对象。 */
function mockFullscreen({ secure = true } = {}) {
  let activeElement: Element | null = null;
  let pointerElement: Element | null = null;
  const originals = [
    [window, 'isSecureContext'],
    [navigator, 'keyboard'],
    [document, 'fullscreenElement'],
    [document, 'fullscreenEnabled'],
    [document, 'exitFullscreen'],
    [document, 'pointerLockElement'],
    [document, 'exitPointerLock'],
    [HTMLElement.prototype, 'requestFullscreen'],
    [HTMLElement.prototype, 'requestPointerLock'],
  ].map(([object, key]) => ({
    object: object as object,
    key: key as string,
    descriptor: Object.getOwnPropertyDescriptor(object, key as string),
  }));
  const exit = vi.fn(async () => {
    activeElement = null;
    document.dispatchEvent(new Event('fullscreenchange'));
  });
  const request = vi.fn(async function (this: HTMLElement) {
    activeElement = this;
    document.dispatchEvent(new Event('fullscreenchange'));
  });
  const keyboardLock = vi.fn(async () => {});
  const keyboardUnlock = vi.fn();
  const pointerRequest = vi.fn(async function (this: HTMLElement) {
    pointerElement = this;
    document.dispatchEvent(new Event('pointerlockchange'));
  });
  const pointerExit = vi.fn(() => {
    pointerElement = null;
    document.dispatchEvent(new Event('pointerlockchange'));
  });
  Object.defineProperty(window, 'isSecureContext', {
    configurable: true,
    value: secure,
  });
  Object.defineProperty(navigator, 'keyboard', {
    configurable: true,
    value: secure ? { lock: keyboardLock, unlock: keyboardUnlock } : undefined,
  });
  Object.defineProperties(document, {
    fullscreenElement: { configurable: true, get: () => activeElement },
    fullscreenEnabled: { configurable: true, get: () => true },
    exitFullscreen: { configurable: true, value: exit },
    pointerLockElement: { configurable: true, get: () => pointerElement },
    exitPointerLock: { configurable: true, value: pointerExit },
  });
  Object.defineProperties(HTMLElement.prototype, {
    requestFullscreen: { configurable: true, value: request },
    requestPointerLock: { configurable: true, value: pointerRequest },
  });
  return {
    exit,
    request,
    keyboardLock,
    keyboardUnlock,
    pointerRequest,
    pointerExit,
    current: () => activeElement,
    restore: () => {
      for (const { object, key, descriptor } of originals) {
        if (descriptor) Object.defineProperty(object, key, descriptor);
        else Reflect.deleteProperty(object, key);
      }
    },
  };
}

/** 通过真实连接按钮与同源握手启用输入，避免跳过会话权限状态。 */
async function connectWithInput() {
  const view = mount();
  const connect = await screen.findByRole('button', {
    name: 'oob.console.connect',
  });
  await waitFor(() => expect(connect).not.toBeDisabled());
  fireEvent.click(connect);
  const iframe = await waitFor(() => {
    const frame = view.container.querySelector('iframe');
    if (!frame?.contentWindow) throw new Error('Frame not ready');
    return frame;
  });
  const post = vi
    .spyOn(iframe.contentWindow as Window, 'postMessage')
    .mockImplementation(() => {});
  fireEvent.load(iframe);
  const challenge = post.mock.calls.findLast(
    ([message]) => message.type === 'oob-console-handshake',
  )?.[0].challenge;
  act(() =>
    window.dispatchEvent(
      new MessageEvent('message', {
        origin: window.location.origin,
        source: iframe.contentWindow,
        data: {
          type: 'oob-console-ready',
          sessionId: session.id,
          bridgeProtocol: 'OobConsoleV1',
          challenge,
        },
      }),
    ),
  );
  const inputSwitch = screen.getByRole('switch', { name: 'oob.console.input' });
  await waitFor(() => expect(inputSwitch).not.toBeDisabled());
  fireEvent.click(inputSwitch);
  await waitFor(() =>
    expect(inputSwitch).toHaveAttribute('aria-checked', 'true'),
  );
  const viewport = screen.getByRole('application');
  fireEvent.pointerDown(viewport, { button: 0, buttons: 1 });
  return {
    view,
    iframe,
    post,
    inputSwitch,
    viewport,
    inputs: () =>
      post.mock.calls
        .filter(([message]) => message.type === 'oob-console-input')
        .map(([message]) => message.event),
  };
}

describe('控制台会话的真实用户意图与输入隔离', () => {
  it('外部点击保持输入开关，释放已按键且外部输入不发往远端，再点画面可继续', async () => {
    const { view, iframe, viewport, inputSwitch, inputs } =
      await connectWithInput();
    const outside = render(<input aria-label="outside input" />);
    try {
      fireEvent.pointerDown(viewport, { button: 0, buttons: 1 });
      fireEvent.keyDown(viewport, { code: 'ControlLeft', key: 'Control' });
      const field = screen.getByRole('textbox', { name: 'outside input' });
      fireEvent.pointerDown(field);
      act(() => field.focus());
      expect(inputSwitch).toHaveAttribute('aria-checked', 'true');
      expect(api.input).not.toHaveBeenCalledWith(session.id, false);
      expect(inputs()).toContainEqual(
        expect.objectContaining({ code: 'ControlLeft', down: false }),
      );
      const count = inputs().length;
      fireEvent.keyDown(field, { code: 'KeyB', key: 'b' });
      fireEvent.keyDown(viewport, { code: 'KeyB', key: 'b' });
      expect(inputs()).toHaveLength(count);
      fireEvent.pointerDown(viewport, { button: 0, buttons: 1 });
      fireEvent.keyDown(viewport, { code: 'KeyC', key: 'c' });
      expect(inputs()).toContainEqual(
        expect.objectContaining({ code: 'KeyC', down: true }),
      );
      expect(view.container.querySelector('iframe')).toBe(iframe);
      expect(api.create).toHaveBeenCalledOnce();
    } finally {
      outside.unmount();
      view.unmount();
    }
  });

  it('浮窗跨页和最小化保留同一 iframe 与输入开关，恢复时不自动接管', async () => {
    const { view, iframe } = await connectWithInput();
    const parent = iframe.parentElement;
    const renderWindow = (pageVisible: boolean, minimized = false) => (
      <App>
        <ConsoleSession
          server={server}
          active={!minimized}
          floating
          minimized={minimized}
          pageVisible={pageVisible}
          getContainer={() => document.body}
        />
      </App>
    );
    try {
      view.rerender(renderWindow(false));
      expect(api.input).not.toHaveBeenCalledWith(session.id, false);
      expect(
        screen.getByRole('switch', { name: 'oob.console.input' }),
      ).toHaveAttribute('aria-checked', 'true');
      for (const [visible, minimized] of [
        [false, true],
        [false, false],
        [true, false],
      ]) {
        view.rerender(renderWindow(visible, minimized));
        expect(view.container.querySelector('iframe')).toBe(iframe);
        expect(iframe.parentElement).toBe(parent);
      }
      expect(api.create).toHaveBeenCalledTimes(1);
      expect(api.close).not.toHaveBeenCalled();
      expect(
        api.input.mock.calls.filter(([, enabled]) => enabled),
      ).toHaveLength(1);
      for (const inputSwitch of screen.getAllByRole('switch', {
        name: 'oob.console.input',
      }))
        expect(inputSwitch).toHaveAttribute('aria-checked', 'true');
    } finally {
      view.unmount();
    }
  });

  it('重连等待关闭时切换认证代次，不再用新身份创建旧服务器连接', async () => {
    const view = mount();
    const connect = screen.getByRole('button', { name: 'oob.console.connect' });
    await waitFor(() => expect(connect).not.toBeDisabled());
    fireEvent.click(connect);
    const reconnect = await screen.findByRole('button', {
      name: /oob\.console\.reconnect$/,
    });
    await waitFor(() => expect(reconnect).not.toBeDisabled());
    let completeClose!: (value: ReturnType<typeof ok>) => void;
    api.close.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          completeClose = resolve;
        }),
    );
    fireEvent.click(reconnect);
    await waitFor(() => expect(api.close).toHaveBeenCalledTimes(1));
    act(() => beginSession());
    view.unmount();
    await act(async () => {
      completeClose(ok({ ...session, status: 'Closed' }));
    });
    expect(api.create).toHaveBeenCalledTimes(1);
    expect(api.close).toHaveBeenCalledTimes(1);
  });

  it('进入电源操作前释放远端输入，保留开关与原画面会话', async () => {
    const { view, iframe, inputSwitch } = await connectWithInput();
    try {
      fireEvent.click(
        screen.getByRole('button', { name: 'fixture-power-interaction' }),
      );
      expect(api.input).not.toHaveBeenCalledWith(session.id, false);
      expect(inputSwitch).toHaveAttribute('aria-checked', 'true');
      expect(view.container.querySelector('iframe')).toBe(iframe);
      expect(api.create).toHaveBeenCalledTimes(1);
      expect(api.close).not.toHaveBeenCalled();
    } finally {
      view.unmount();
    }
  });

  it('原生全屏锁定键盘后短按 Esc 发远端；按钮隔离输入，切标签退出且不重建画面', async () => {
    const fullscreen = mockFullscreen();
    const view = mount();
    try {
      const connect = await screen.findByRole('button', {
        name: 'oob.console.connect',
      });
      await waitFor(() => expect(connect).not.toBeDisabled());
      fireEvent.click(connect);
      const iframe = await waitFor(() => {
        const frame = view.container.querySelector('iframe');
        if (!frame?.contentWindow) throw new Error('Frame not ready');
        return frame;
      });
      const post = vi
        .spyOn(iframe.contentWindow as Window, 'postMessage')
        .mockImplementation(() => {});
      fireEvent.load(iframe);
      const challenge = post.mock.calls.findLast(
        ([message]) => message.type === 'oob-console-handshake',
      )?.[0].challenge;
      act(() =>
        window.dispatchEvent(
          new MessageEvent('message', {
            origin: window.location.origin,
            source: iframe.contentWindow,
            data: {
              type: 'oob-console-ready',
              sessionId: session.id,
              bridgeProtocol: 'OobConsoleV1',
              challenge,
            },
          }),
        ),
      );
      const inputSwitch = screen.getByRole('switch', {
        name: 'oob.console.input',
      });
      await waitFor(() => expect(inputSwitch).not.toBeDisabled());
      fireEvent.click(inputSwitch);
      await waitFor(() =>
        expect(inputSwitch).toHaveAttribute('aria-checked', 'true'),
      );
      const viewport = screen.getByRole('application');
      const fullscreenButton = screen.getByRole('button', {
        name: /oob\.console\.fullscreen$/,
      });
      const remoteInputs = () =>
        post.mock.calls.filter(
          ([message]) =>
            message.type === 'oob-console-input' &&
            ['key', 'pointer', 'wheel'].includes(message.event?.type),
        ).length;
      const before = remoteInputs();
      fireEvent.pointerDown(fullscreenButton, { pointerId: 1, buttons: 1 });
      fireEvent.pointerUp(fullscreenButton, { pointerId: 1, buttons: 0 });
      fireEvent.keyDown(fullscreenButton, { key: 'f', code: 'KeyF' });
      fireEvent.keyUp(fullscreenButton, { key: 'f', code: 'KeyF' });
      fireEvent.click(fullscreenButton);
      await waitFor(() => expect(fullscreen.request).toHaveBeenCalledOnce());
      expect(fullscreen.keyboardLock).toHaveBeenCalled();
      await waitFor(() => expect(viewport).toHaveFocus());
      const fullscreenRoot = fullscreen.current();
      expect(fullscreenRoot).toContainElement(viewport);
      expect(fullscreenRoot).toContainElement(iframe);
      expect(fullscreenRoot).toContainElement(fullscreenButton);
      expect(fullscreenRoot).not.toContainElement(connect);
      expect(fullscreenRoot).not.toContainElement(inputSwitch);
      expect(remoteInputs()).toBe(before);
      expect(view.container.querySelector('iframe')).toBe(iframe);
      fireEvent.keyDown(viewport, { key: 'a', code: 'KeyA' });
      fireEvent.keyUp(viewport, { key: 'a', code: 'KeyA' });
      expect(
        post.mock.calls.some(
          ([message]) =>
            message.type === 'oob-console-input' &&
            message.event?.type === 'key' &&
            message.event.code === 'KeyA' &&
            message.event.down,
        ),
      ).toBe(true);
      const afterScreenInput = remoteInputs();
      const fullscreenSwitch = screen
        .getAllByRole('switch', { name: 'oob.console.input' })
        .find((element) => fullscreenRoot?.contains(element));
      expect(fullscreenSwitch).toBeInTheDocument();
      expect(fullscreenSwitch).toHaveAttribute('aria-checked', 'true');
      expect(api.input).not.toHaveBeenCalledWith(session.id, false);
      fireEvent.pointerDown(fullscreenSwitch as HTMLElement, {
        pointerId: 2,
        buttons: 1,
      });
      fireEvent.pointerUp(fullscreenSwitch as HTMLElement, {
        pointerId: 2,
        buttons: 0,
      });
      fireEvent.click(fullscreenSwitch as HTMLElement);
      await waitFor(() =>
        expect(api.input).toHaveBeenLastCalledWith(session.id, false),
      );
      expect(remoteInputs()).toBe(afterScreenInput);
      fireEvent.click(fullscreenSwitch as HTMLElement);
      await waitFor(() =>
        expect(api.input).toHaveBeenLastCalledWith(session.id, true),
      );
      expect(remoteInputs()).toBe(afterScreenInput);

      // 重新开启只保留授权；用户再次点击画面才恢复捕获。
      fireEvent.pointerDown(viewport, { button: 0, buttons: 1 });
      // 短按 Esc 属于远端输入；浏览器长按应急退出由实际浏览器回归验证。
      const disableCount = api.input.mock.calls.filter(
        ([, enabled]) => !enabled,
      ).length;
      fireEvent.keyDown(viewport, { key: 'Escape', code: 'Escape' });
      fireEvent.keyUp(viewport, { key: 'Escape', code: 'Escape' });
      expect(
        post.mock.calls
          .filter(
            ([message]) =>
              message.type === 'oob-console-input' &&
              message.event?.code === 'Escape',
          )
          .map(([message]) => message.event.down),
      ).toEqual([true, false]);
      expect(fullscreen.exit).not.toHaveBeenCalled();
      expect(fullscreen.current()).toBe(fullscreenRoot);
      expect(
        api.input.mock.calls.filter(([, enabled]) => !enabled),
      ).toHaveLength(disableCount);
      // 再次点击画面会异步申请键盘锁，等捕获完成后再点退出按钮。
      await waitFor(() =>
        expect(
          screen.getByRole('button', { name: /oob\.console\.exitFullscreen$/ }),
        ).not.toBeDisabled(),
      );
      fireEvent.click(
        screen.getByRole('button', { name: /oob\.console\.exitFullscreen$/ }),
      );
      await waitFor(() => expect(fullscreen.exit).toHaveBeenCalledOnce());
      expect(fullscreen.current()).toBeNull();
      expect(view.container.querySelector('iframe')).toBe(iframe);
      expect(api.create).toHaveBeenCalledOnce();

      fireEvent.click(
        screen.getByRole('button', { name: /oob\.console\.fullscreen$/ }),
      );
      await waitFor(() => expect(fullscreen.request).toHaveBeenCalledTimes(2));
      view.rerender(
        <App>
          <ConsoleSession
            server={server}
            active={false}
            getContainer={() => document.body}
          />
        </App>,
      );
      await waitFor(() => expect(fullscreen.exit).toHaveBeenCalledTimes(2));
      expect(fullscreen.current()).toBeNull();
      expect(view.container.querySelector('iframe')).toBe(iframe);
      expect(api.create).toHaveBeenCalledOnce();
    } finally {
      view.unmount();
      fullscreen.restore();
    }
  });

  it('HTTP 页面全屏不申请原生键鼠锁，Esc 持续发远端且保留输入权限', async () => {
    const fullscreen = mockFullscreen({ secure: false });
    const { view, iframe, viewport, inputSwitch, inputs } =
      await connectWithInput();
    try {
      fireEvent.click(
        screen.getByRole('button', { name: /oob\.console\.fullscreen$/ }),
      );
      const shell = viewport.closest('[data-fullscreen-mode]');
      await waitFor(() =>
        expect(shell).toHaveAttribute('data-fullscreen-mode', 'page'),
      );
      expect(fullscreen.request).not.toHaveBeenCalled();
      expect(fullscreen.keyboardLock).not.toHaveBeenCalled();
      expect(fullscreen.pointerRequest).not.toHaveBeenCalled();
      fireEvent.keyDown(viewport, { key: 'Escape', code: 'Escape' });
      fireEvent.keyUp(viewport, { key: 'Escape', code: 'Escape' });
      fireEvent.keyDown(viewport, { key: 'a', code: 'KeyA' });
      fireEvent.keyUp(viewport, { key: 'a', code: 'KeyA' });
      expect(
        inputs()
          .filter((event) => event.type === 'key')
          .map((event) => [event.code, event.down]),
      ).toEqual([
        ['Escape', true],
        ['Escape', false],
        ['KeyA', true],
        ['KeyA', false],
      ]);
      expect(inputSwitch).toHaveAttribute('aria-checked', 'true');
      expect(api.input).not.toHaveBeenCalledWith(session.id, false);
      expect(shell).toHaveAttribute('data-fullscreen-mode', 'page');
      expect(view.container.querySelector('iframe')).toBe(iframe);
      expect(api.create).toHaveBeenCalledOnce();
    } finally {
      view.unmount();
      fullscreen.restore();
    }
  });

  it('物理 Ctrl+Alt 释放已按键与捕获，保留开关且不重建控制台', async () => {
    const fullscreen = mockFullscreen();
    const { view, iframe, viewport, inputSwitch, inputs } =
      await connectWithInput();
    try {
      fireEvent.click(
        screen.getByRole('button', { name: /oob\.console\.fullscreen$/ }),
      );
      await waitFor(() => expect(fullscreen.keyboardLock).toHaveBeenCalled());
      await screen.findByText('oob.console.captureActive');
      await waitFor(() => expect(viewport).toHaveFocus());
      expect(inputSwitch).toHaveAttribute('aria-checked', 'true');
      fireEvent.keyDown(viewport, { key: 'a', code: 'KeyA' });
      fireEvent.keyDown(viewport, {
        key: 'Control',
        code: 'ControlLeft',
        ctrlKey: true,
      });
      const releaseChord = new KeyboardEvent('keydown', {
        key: 'Alt',
        code: 'AltLeft',
        ctrlKey: true,
        altKey: true,
        bubbles: true,
      });
      // Happy DOM 将任何 Alt 都当作 AltGraph；这里显式模拟物理左 Ctrl+左 Alt。
      Object.defineProperty(releaseChord, 'getModifierState', {
        value: (key: string) => key === 'Control' || key === 'Alt',
      });
      fireEvent(viewport, releaseChord);
      expect(api.input).not.toHaveBeenCalledWith(session.id, false);
      expect(inputSwitch).toHaveAttribute('aria-checked', 'true');
      expect(inputs()).toEqual(
        expect.arrayContaining([
          expect.objectContaining({ type: 'key', code: 'KeyA', down: false }),
          expect.objectContaining({
            type: 'key',
            code: 'ControlLeft',
            down: false,
          }),
          { type: 'release-all' },
        ]),
      );
      expect(fullscreen.keyboardUnlock).toHaveBeenCalled();
      expect(view.container.querySelector('iframe')).toBe(iframe);
      expect(api.create).toHaveBeenCalledOnce();
      expect(api.close).not.toHaveBeenCalled();
      const before = inputs().length;
      fireEvent.keyDown(viewport, { key: 'b', code: 'KeyB' });
      expect(inputs()).toHaveLength(before);
    } finally {
      view.unmount();
      fullscreen.restore();
    }
  });

  it('AltGraph 和携带 Ctrl+Alt 标志的普通键不误触解锁', async () => {
    const fullscreen = mockFullscreen({ secure: false });
    const { view, viewport, inputSwitch, inputs } = await connectWithInput();
    try {
      fireEvent.click(
        screen.getByRole('button', { name: /oob\.console\.fullscreen$/ }),
      );
      await waitFor(() =>
        expect(viewport.closest('[data-fullscreen-mode]')).toHaveAttribute(
          'data-fullscreen-mode',
          'page',
        ),
      );
      // 部分键盘以 Ctrl+Alt 表示 AltGraph，不能把字符输入误认成释放手势。
      const altGraph = new KeyboardEvent('keydown', {
        key: 'AltGraph',
        code: 'AltRight',
        ctrlKey: true,
        altKey: true,
        bubbles: true,
      });
      Object.defineProperty(altGraph, 'getModifierState', {
        value: (key: string) => key === 'AltGraph',
      });
      fireEvent(viewport, altGraph);
      fireEvent.keyUp(viewport, { key: 'AltGraph', code: 'AltRight' });
      fireEvent.keyDown(viewport, {
        key: '@',
        code: 'KeyQ',
        ctrlKey: true,
        altKey: true,
      });
      fireEvent.keyUp(viewport, { key: '@', code: 'KeyQ' });
      expect(inputs()).toEqual(
        expect.arrayContaining([
          expect.objectContaining({
            type: 'key',
            code: 'AltRight',
            down: true,
          }),
          expect.objectContaining({ type: 'key', code: 'KeyQ', down: true }),
        ]),
      );
      expect(inputSwitch).toHaveAttribute('aria-checked', 'true');
      expect(api.input).not.toHaveBeenCalledWith(session.id, false);
    } finally {
      view.unmount();
      fullscreen.restore();
    }
  });

  it('从画面切到屏幕键盘按 Esc 后仍能输入 A，屏幕 Ctrl+Alt+Del 发远端', async () => {
    const { view, iframe, viewport, inputSwitch, inputs } =
      await connectWithInput();
    try {
      fireEvent.click(
        screen.getByRole('button', { name: 'oob.console.keyboard' }),
      );
      const escapeButton = screen.getByRole('button', { name: 'Escape' });
      const letter = screen.getByRole('button', { name: 'KeyA' });
      act(() => viewport.focus());
      expect(viewport).toHaveFocus();
      // 模拟浏览器点击按钮时真实发生的焦点迁移，而非只派发 click。
      act(() => escapeButton.focus());
      fireEvent.click(escapeButton);
      act(() => letter.focus());
      fireEvent.click(letter);
      expect(
        inputs()
          .filter((event) => event.type === 'key')
          .map((event) => [event.code, event.down]),
      ).toEqual([
        ['Escape', true],
        ['Escape', false],
        ['KeyA', true],
        ['KeyA', false],
      ]);
      expect(inputSwitch).toHaveAttribute('aria-checked', 'true');
      expect(api.input).not.toHaveBeenCalledWith(session.id, false);
      const before = inputs().length;
      act(() => viewport.focus());
      const combination = screen.getByRole('button', {
        name: 'oob.console.ctrlAltDel',
      });
      act(() => combination.focus());
      fireEvent.click(combination);
      expect(
        inputs()
          .slice(before)
          .filter((event) => event.type === 'key')
          .map((event) => [event.code, event.down]),
      ).toEqual([
        ['ControlLeft', true],
        ['AltLeft', true],
        ['Delete', true],
        ['Delete', false],
        ['AltLeft', false],
        ['ControlLeft', false],
      ]);
      expect(api.input).not.toHaveBeenCalledWith(session.id, false);
      expect(view.container.querySelector('iframe')).toBe(iframe);
      expect(api.create).toHaveBeenCalledOnce();
    } finally {
      view.unmount();
    }
  });

  it('从屏幕键盘移到会话外立即释放修饰键，保留输入开关和画面会话', async () => {
    const { view, iframe, viewport, inputSwitch, inputs } =
      await connectWithInput();
    const outsideView = render(<button type="button">outside session</button>);
    try {
      fireEvent.click(
        screen.getByRole('button', { name: 'oob.console.keyboard' }),
      );
      const control = screen.getByRole('button', { name: 'ControlLeft' });
      act(() => viewport.focus());
      act(() => control.focus());
      fireEvent.click(control);
      expect(control).toHaveAttribute('aria-pressed', 'true');
      const before = inputs().length;
      act(() =>
        screen.getByRole('button', { name: 'outside session' }).focus(),
      );
      expect(api.input).not.toHaveBeenCalledWith(session.id, false);
      expect(inputs().slice(before)).toContainEqual({ type: 'release-all' });
      expect(inputSwitch).toHaveAttribute('aria-checked', 'true');
      expect(control).toHaveAttribute('aria-pressed', 'false');
      expect(control).not.toBeDisabled();
      expect(view.container.querySelector('iframe')).toBe(iframe);
      expect(api.create).toHaveBeenCalledOnce();
      expect(api.close).not.toHaveBeenCalled();
    } finally {
      outsideView.unmount();
      view.unmount();
    }
  });

  it('开启请求未完成时点击外部，保留开启意图但迟到成功不能自动接管', async () => {
    const { view, viewport, inputSwitch, post, inputs } =
      await connectWithInput();
    const outsideView = render(<div>outside session</div>);
    try {
      fireEvent.click(inputSwitch);
      await waitFor(() =>
        expect(inputSwitch).toHaveAttribute('aria-checked', 'false'),
      );
      await waitFor(() => expect(inputSwitch).not.toBeDisabled());
      let finishEnable!: (value: ReturnType<typeof ok<typeof session>>) => void;
      api.input.mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            finishEnable = resolve;
          }),
      );
      act(() => inputSwitch.focus());
      fireEvent.click(inputSwitch);
      await waitFor(() =>
        expect(api.input).toHaveBeenLastCalledWith(session.id, true),
      );
      expect(api.input).toHaveBeenCalledTimes(3);
      // 不可聚焦的外部区域也要暂停捕获，但不能取消用户开启输入的意图。
      fireEvent.pointerDown(screen.getByText('outside session'));
      const afterBlur = post.mock.calls.length;
      await act(async () =>
        finishEnable(ok({ ...session, inputEnabled: true })),
      );
      expect(api.input).toHaveBeenCalledTimes(3);
      expect(api.input).toHaveBeenLastCalledWith(session.id, true);
      expect(inputSwitch).toHaveAttribute('aria-checked', 'true');
      expect(
        post.mock.calls
          .slice(afterBlur)
          .some(
            ([message]) =>
              message.type === 'oob-console-input-state' && message.enabled,
          ),
      ).toBe(false);
      const count = inputs().length;
      fireEvent.keyDown(viewport, { key: 'b', code: 'KeyB' });
      expect(inputs()).toHaveLength(count);
      fireEvent.pointerDown(viewport, { button: 0, buttons: 1 });
      fireEvent.keyDown(viewport, { key: 'c', code: 'KeyC' });
      expect(inputs()).toContainEqual(
        expect.objectContaining({ code: 'KeyC', down: true }),
      );
    } finally {
      outsideView.unmount();
      view.unmount();
    }
  });

  it('输入开关忙碌禁用产生的 null 失焦不会取消用户刚发起的开启', async () => {
    const { view, inputSwitch } = await connectWithInput();
    try {
      fireEvent.click(inputSwitch);
      await waitFor(() =>
        expect(inputSwitch).toHaveAttribute('aria-checked', 'false'),
      );
      await waitFor(() => expect(inputSwitch).not.toBeDisabled());
      let finishEnable!: (value: ReturnType<typeof ok<typeof session>>) => void;
      api.input.mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            finishEnable = resolve;
          }),
      );
      act(() => inputSwitch.focus());
      fireEvent.click(inputSwitch);
      await waitFor(() => expect(inputSwitch).toBeDisabled());
      fireEvent.blur(inputSwitch, { relatedTarget: null });
      await act(async () =>
        finishEnable(ok({ ...session, inputEnabled: true })),
      );
      await waitFor(() =>
        expect(inputSwitch).toHaveAttribute('aria-checked', 'true'),
      );
      expect(api.input).toHaveBeenCalledTimes(3);
      expect(api.input).toHaveBeenLastCalledWith(session.id, true);
    } finally {
      view.unmount();
    }
  });

  it('开启请求期间 iframe 重载，迟到成功仍需禁用，不能把真实失效当作失焦', async () => {
    const { view, iframe, inputSwitch, post } = await connectWithInput();
    try {
      fireEvent.click(inputSwitch);
      await waitFor(() =>
        expect(inputSwitch).toHaveAttribute('aria-checked', 'false'),
      );
      await waitFor(() => expect(inputSwitch).not.toBeDisabled());
      let finishEnable!: (value: ReturnType<typeof ok<typeof session>>) => void;
      api.input.mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            finishEnable = resolve;
          }),
      );
      fireEvent.click(inputSwitch);
      await waitFor(() =>
        expect(api.input).toHaveBeenLastCalledWith(session.id, true),
      );
      fireEvent.load(iframe);
      const afterReload = post.mock.calls.length;
      await act(async () =>
        finishEnable(ok({ ...session, inputEnabled: true })),
      );
      await waitFor(() =>
        expect(api.input).toHaveBeenLastCalledWith(session.id, false),
      );
      expect(inputSwitch).toHaveAttribute('aria-checked', 'false');
      expect(
        post.mock.calls
          .slice(afterReload)
          .some(
            ([message]) =>
              message.type === 'oob-console-input-state' && message.enabled,
          ),
      ).toBe(false);
    } finally {
      view.unmount();
    }
  });

  it('服务端关闭会话后开关关闭，既有焦点不能继续输入', async () => {
    const { view, viewport, inputSwitch, inputs } = await connectWithInput();
    try {
      api.traffic.mockResolvedValue(
        ok({ ...session, status: 'Closed', inputEnabled: false }),
      );
      await waitFor(
        () => expect(inputSwitch).toHaveAttribute('aria-checked', 'false'),
        { timeout: 1800 },
      );
      expect(inputSwitch).toBeDisabled();
      const before = inputs().length;
      fireEvent.pointerDown(viewport, { button: 0, buttons: 1 });
      fireEvent.keyDown(viewport, { key: 'b', code: 'KeyB' });
      expect(inputs()).toHaveLength(before);
      expect(
        api.input.mock.calls.filter(([, enabled]) => enabled),
      ).toHaveLength(1);
    } finally {
      view.unmount();
    }
  });

  it('开启请求期间服务端关闭会话，迟到的 Connected 响应不能复活会话', async () => {
    const { view, inputSwitch, post } = await connectWithInput();
    try {
      fireEvent.click(inputSwitch);
      await waitFor(() => expect(inputSwitch).not.toBeDisabled());
      let finishEnable!: (value: ReturnType<typeof ok<typeof session>>) => void;
      api.input.mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            finishEnable = resolve;
          }),
      );
      fireEvent.click(inputSwitch);
      await waitFor(() => expect(api.input).toHaveBeenCalledTimes(3));
      let finishTraffic!: (
        value: ReturnType<typeof ok<typeof session>>,
      ) => void;
      api.traffic.mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            finishTraffic = resolve;
          }),
      );
      await waitFor(() => expect(finishTraffic).toBeDefined(), {
        timeout: 1800,
      });
      const before = post.mock.calls.length;
      // 同一批次内先终态后旧响应，不能依赖中间一次 React 渲染撤销授权。
      await act(async () => {
        finishTraffic(
          ok({ ...session, status: 'Closed', inputEnabled: false }),
        );
        await Promise.resolve();
        finishEnable(ok({ ...session, inputEnabled: true }));
      });
      expect(screen.getByText('oob.console.status.Closed')).toBeInTheDocument();
      expect(inputSwitch).toHaveAttribute('aria-checked', 'false');
      expect(inputSwitch).toBeDisabled();
      expect(api.input).toHaveBeenCalledTimes(3);
      expect(
        post.mock.calls
          .slice(before)
          .some(
            ([message]) =>
              message.type === 'oob-console-input-state' && message.enabled,
          ),
      ).toBe(false);
    } finally {
      view.unmount();
    }
  });

  it('HTTP 环境缺少 randomUUID 时仍能创建请求并完成 iframe 握手', async () => {
    // 局域网 HTTP 页面通常没有 randomUUID，但仍保留 getRandomValues。
    const descriptor = Object.getOwnPropertyDescriptor(crypto, 'randomUUID');
    Object.defineProperty(crypto, 'randomUUID', {
      configurable: true,
      value: undefined,
    });
    try {
      expect(typeof crypto.getRandomValues).toBe('function');
      const view = mount();
      const connect = await screen.findByRole('button', {
        name: 'oob.console.connect',
      });
      await waitFor(() => expect(connect).not.toBeDisabled());
      fireEvent.click(connect);
      await waitFor(() => expect(api.create).toHaveBeenCalledTimes(1));
      const [serverId, requestId] = api.create.mock.calls[0];
      expect(serverId).toBe(server.id);
      expect(requestId).toMatch(
        /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
      );

      const iframe = await waitFor(() => {
        const frame = view.container.querySelector('iframe');
        if (!frame?.contentWindow) throw new Error('Frame not ready');
        return frame;
      });
      const post = vi
        .spyOn(iframe.contentWindow as Window, 'postMessage')
        .mockImplementation(() => {});
      fireEvent.load(iframe);
      const challenge = post.mock.calls.findLast(
        ([event]) => event.type === 'oob-console-handshake',
      )?.[0].challenge;
      expect(challenge).toMatch(
        /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
      );
      expect(challenge).not.toBe(requestId);
      act(() =>
        window.dispatchEvent(
          new MessageEvent('message', {
            origin: window.location.origin,
            source: iframe.contentWindow,
            data: {
              type: 'oob-console-ready',
              sessionId: session.id,
              bridgeProtocol: 'OobConsoleV1',
              challenge,
            },
          }),
        ),
      );
      await waitFor(() =>
        expect(
          screen.getByRole('switch', { name: 'oob.console.input' }),
        ).not.toBeDisabled(),
      );

      fireEvent.load(iframe);
      const nextChallenge = post.mock.calls.findLast(
        ([event]) => event.type === 'oob-console-handshake',
      )?.[0].challenge;
      expect(nextChallenge).not.toBe(challenge);
      view.unmount();
    } finally {
      if (descriptor) Object.defineProperty(crypto, 'randomUUID', descriptor);
      else Reflect.deleteProperty(crypto, 'randomUUID');
    }
  });

  it('挂载只预检；显式连接和受控握手后才能启用输入，失焦释放按键', async () => {
    const view = mount();
    const connect = await screen.findByRole('button', {
      name: 'oob.console.connect',
    });
    await waitFor(() => expect(connect).not.toBeDisabled());
    expect(api.create).not.toHaveBeenCalled();
    fireEvent.click(connect);
    const iframe = await waitFor(() => {
      const frame = view.container.querySelector('iframe');
      if (!frame?.contentWindow) throw new Error('Frame not ready');
      return frame;
    });
    const post = vi
      .spyOn(iframe.contentWindow as Window, 'postMessage')
      .mockImplementation(() => {});
    fireEvent.load(iframe);
    const challenge = post.mock.calls.findLast(
      ([event]) => event.type === 'oob-console-handshake',
    )?.[0].challenge;
    expect(typeof challenge).toBe('string');
    expect(
      screen.getByRole('switch', { name: 'oob.console.input' }),
    ).toBeDisabled();
    act(() =>
      window.dispatchEvent(
        new MessageEvent('message', {
          origin: window.location.origin,
          source: iframe.contentWindow,
          data: {
            type: 'oob-console-ready',
            sessionId: session.id,
            bridgeProtocol: 'OobConsoleV1',
            challenge,
          },
        }),
      ),
    );
    const inputSwitch = screen.getByRole('switch', {
      name: 'oob.console.input',
    });
    await waitFor(() => expect(inputSwitch).not.toBeDisabled());
    fireEvent.click(inputSwitch);
    await waitFor(() =>
      expect(api.input).toHaveBeenCalledWith(session.id, true),
    );
    await waitFor(() =>
      expect(inputSwitch).toHaveAttribute('aria-checked', 'true'),
    );
    const viewport = screen.getByRole('application');
    fireEvent.pointerDown(viewport, { button: 0, buttons: 1 });
    await waitFor(() =>
      expect(
        post.mock.calls.some(
          ([event]) =>
            event.type === 'oob-console-input-state' && event.enabled,
        ),
      ).toBe(true),
    );
    fireEvent.keyDown(viewport, { key: 'Control', code: 'ControlLeft' });
    act(() => window.dispatchEvent(new Event('blur')));
    expect(
      post.mock.calls.some(
        ([event]) =>
          event.event?.type === 'key' &&
          event.event.code === 'ControlLeft' &&
          event.event.down === false,
      ),
    ).toBe(true);
    act(() =>
      window.dispatchEvent(
        new MessageEvent('message', {
          origin: window.location.origin,
          source: iframe.contentWindow,
          data: {
            type: 'oob-console-input-disabled',
            sessionId: session.id,
            challenge,
          },
        }),
      ),
    );
    expect(api.input).not.toHaveBeenCalledWith(session.id, false);
    expect(inputSwitch).toHaveAttribute('aria-checked', 'true');
    fireEvent.load(iframe);
    await waitFor(() =>
      expect(api.input).toHaveBeenLastCalledWith(session.id, false),
    );
    expect(inputSwitch).toHaveAttribute('aria-checked', 'false');
    const freshChallenge = post.mock.calls.findLast(
      ([event]) => event.type === 'oob-console-handshake',
    )?.[0].challenge;
    expect(freshChallenge).not.toBe(challenge);
    act(() =>
      window.dispatchEvent(
        new MessageEvent('message', {
          origin: window.location.origin,
          source: iframe.contentWindow,
          data: {
            type: 'oob-console-ready',
            sessionId: session.id,
            bridgeProtocol: 'OobConsoleV1',
            challenge,
          },
        }),
      ),
    );
    expect(inputSwitch).toBeDisabled();
    act(() =>
      window.dispatchEvent(
        new MessageEvent('message', {
          origin: window.location.origin,
          source: iframe.contentWindow,
          data: {
            type: 'oob-console-ready',
            sessionId: session.id,
            bridgeProtocol: 'OobConsoleV1',
            challenge: freshChallenge,
          },
        }),
      ),
    );
    await waitFor(() => expect(inputSwitch).not.toBeDisabled());
    fireEvent.click(inputSwitch);
    await waitFor(() =>
      expect(inputSwitch).toHaveAttribute('aria-checked', 'true'),
    );
    const enableCount = api.input.mock.calls.filter(
      ([, enabled]) => enabled,
    ).length;
    view.rerender(
      <App>
        <ConsoleSession
          server={server}
          active={false}
          getContainer={() => document.body}
        />
      </App>,
    );
    expect(api.input).toHaveBeenLastCalledWith(session.id, true);
    expect(inputSwitch).toHaveAttribute('aria-checked', 'true');
    const before = post.mock.calls.length;
    fireEvent.keyDown(viewport, { key: 'x', code: 'KeyX' });
    expect(post.mock.calls.length).toBe(before);
    expect(api.create).toHaveBeenCalledTimes(1);
    view.rerender(
      <App>
        <ConsoleSession
          server={server}
          active
          getContainer={() => document.body}
        />
      </App>,
    );
    expect(
      screen.getByRole('switch', { name: 'oob.console.input' }),
    ).toHaveAttribute('aria-checked', 'true');
    expect(api.input.mock.calls.filter(([, enabled]) => enabled)).toHaveLength(
      enableCount,
    );
    view.unmount();
  });
  it('结果未知保留原requestId核对，不自动创建第二次意图', async () => {
    api.create.mockResolvedValue(
      requestFailure(
        'NETWORK_ERROR',
        'network unavailable',
        0,
        'network',
        true,
      ),
    );
    const view = mount();
    const connect = screen.getByRole('button', { name: 'oob.console.connect' });
    await waitFor(() => expect(connect).not.toBeDisabled());
    fireEvent.click(connect);
    const verify = await screen.findByRole('button', {
      name: 'oob.console.verify',
    });
    const requestId = api.create.mock.calls[0][1];
    expect(connect).toBeDisabled();
    fireEvent.click(verify);
    await waitFor(() => expect(api.find).toHaveBeenCalledWith(requestId));
    expect(api.create).toHaveBeenCalledTimes(1);
    view.unmount();
  });
  it('拒绝其他窗口、来源、会话标识和未登记消息', () => {
    const source = {} as Window;
    const event = {
      source,
      origin: 'https://app.invalid',
      data: {
        type: 'oob-console-ready',
        sessionId: session.id,
        challenge: 'current-generation',
      },
    };
    expect(
      isConsoleBridgeMessage(
        event,
        source,
        session.id,
        'https://app.invalid',
        'current-generation',
      ),
    ).toBe(true);
    expect(
      isConsoleBridgeMessage(
        { ...event, origin: 'https://evil.invalid' },
        source,
        session.id,
        'https://app.invalid',
        'current-generation',
      ),
    ).toBe(false);
    expect(
      isConsoleBridgeMessage(
        event,
        {} as Window,
        session.id,
        'https://app.invalid',
        'current-generation',
      ),
    ).toBe(false);
    expect(
      isConsoleBridgeMessage(
        event,
        source,
        'other',
        'https://app.invalid',
        'current-generation',
      ),
    ).toBe(false);
    expect(
      isConsoleBridgeMessage(
        { ...event, data: { type: 'unknown', sessionId: session.id } },
        source,
        session.id,
        'https://app.invalid',
        'current-generation',
      ),
    ).toBe(false);
    expect(
      isConsoleBridgeMessage(
        event,
        source,
        session.id,
        'https://app.invalid',
        'old-generation',
      ),
    ).toBe(false);
  });
});
