import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import React, { createRef, useState } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ConsoleSessionDto } from '@/services/oob/models';
import ConsoleViewport, { type ConsoleViewportHandle } from './ConsoleViewport';

vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));
const browserProperties: Array<{
  target: object;
  key: string;
  descriptor?: PropertyDescriptor;
}> = [];
function browserProperty(
  target: object,
  key: string,
  descriptor: PropertyDescriptor,
) {
  browserProperties.push({
    target,
    key,
    descriptor: Object.getOwnPropertyDescriptor(target, key),
  });
  Object.defineProperty(target, key, { configurable: true, ...descriptor });
}

const session = {
  id: '9007199254740995',
  serverName: '悬浮测试服务器',
  status: 'Connected',
  frameUrl: '/api/oob/console-sessions/9007199254740995/frame/index.html',
  bridgeProtocol: 'OobConsoleV1',
  inputEnabled: false,
} as ConsoleSessionDto;
const callbacks = () => ({
  onInputToggle: vi.fn(),
  onReady: vi.fn(),
  onRelease: vi.fn(),
  onMediaStatus: vi.fn(),
  onError: vi.fn(),
  onInputDisabled: vi.fn(),
  onDetach: vi.fn(),
  onMinimize: vi.fn(),
  onRestore: vi.fn(),
  onClose: vi.fn(),
  onActivate: vi.fn(),
});

beforeEach(() => {
  vi.stubGlobal('innerWidth', 1280);
  vi.stubGlobal('innerHeight', 800);
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
  for (const { target, key, descriptor } of browserProperties
    .splice(0)
    .reverse()) {
    if (descriptor) Object.defineProperty(target, key, descriptor);
    else Reflect.deleteProperty(target, key);
  }
});

function mount(floating = false) {
  const events = callbacks();
  const handle = createRef<ConsoleViewportHandle>();
  const content = (extracted: boolean, minimized = false) => (
    <ConsoleViewport
      ref={handle}
      session={session}
      active={!minimized}
      inputEnabled={false}
      inputToggleDisabled={false}
      floating={extracted}
      minimized={minimized}
      {...events}
    />
  );
  const view = render(content(floating));
  const iframe = view.container.querySelector('iframe') as HTMLIFrameElement;
  const shell = iframe.parentElement?.parentElement as HTMLDivElement;
  return { view, iframe, shell, handle, events, content };
}

describe('控制台悬浮外壳', () => {
  it('从最小化直接全屏先同步展开原画面，再调用浏览器全屏API', async () => {
    const events = callbacks();
    let fullscreenElement: Element | null = null;
    const observed: Array<{ minimized?: string; display?: string }> = [];
    vi.stubGlobal('isSecureContext', true);
    browserProperty(navigator, 'keyboard', {
      value: { lock: vi.fn(async () => {}), unlock: vi.fn() },
    });
    browserProperty(document, 'fullscreenEnabled', { value: true });
    browserProperty(document, 'fullscreenElement', {
      get: () => fullscreenElement,
    });
    browserProperty(document, 'exitFullscreen', {
      value: async () => {
        fullscreenElement = null;
        document.dispatchEvent(new Event('fullscreenchange'));
      },
    });
    browserProperty(HTMLElement.prototype, 'requestFullscreen', {
      value: async function (this: HTMLElement) {
        observed.push({
          minimized: this.dataset.minimized,
          display: this.querySelector<HTMLElement>('[role="application"]')
            ?.style.display,
        });
        fullscreenElement = this;
        document.dispatchEvent(new Event('fullscreenchange'));
      },
    });
    function ControlledViewport() {
      const [minimized, setMinimized] = useState(true);
      return (
        <ConsoleViewport
          session={session}
          active={!minimized}
          floating
          minimized={minimized}
          inputEnabled={false}
          inputToggleDisabled={false}
          {...events}
          onRestore={() => {
            events.onRestore();
            setMinimized(false);
          }}
        />
      );
    }
    const view = render(<ControlledViewport />);
    const iframe = view.container.querySelector('iframe');
    const parent = iframe?.parentElement;
    expect(parent).toHaveStyle({ display: 'none' });
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.console.fullscreen' }),
    );
    await waitFor(() =>
      expect(
        view.container.querySelector('[data-fullscreen-mode="native"]'),
      ).not.toBeNull(),
    );
    expect(events.onRestore).toHaveBeenCalledOnce();
    expect(observed).toEqual([{ minimized: 'false', display: '' }]);
    expect(parent).not.toHaveStyle({ display: 'none' });
    expect(view.container.querySelector('iframe')).toBe(iframe);
  });

  it('提取、最小化、恢复和归位始终保留原 iframe 和父节点', () => {
    const { view, iframe, shell, content, events } = mount();
    const parent = iframe.parentElement;
    fireEvent.click(screen.getByRole('button', { name: 'oob.console.detach' }));
    expect(events.onDetach).toHaveBeenCalledOnce();
    expect(events.onInputDisabled).not.toHaveBeenCalled();
    view.rerender(content(true));
    expect(shell).toHaveAttribute('data-floating', 'true');
    view.rerender(content(true, true));
    expect(shell).toHaveAttribute('data-minimized', 'true');
    expect(iframe.parentElement).toBe(parent);
    expect(parent).toHaveStyle({ display: 'none' });
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.console.restore' }),
    );
    expect(events.onRestore).toHaveBeenCalledOnce();
    view.rerender(content(true));
    view.rerender(content(false));
    expect(view.container.querySelector('iframe')).toBe(iframe);
    expect(iframe.parentElement).toBe(parent);
    expect(parent).not.toHaveStyle({ display: 'none' });
  });

  it('拖动及缩放只改变几何并释放输入，越界会被限制', () => {
    const { iframe, shell, events } = mount(true);
    const title = screen.getByRole('button', {
      name: 'oob.console.moveWindow',
    });
    fireEvent.pointerDown(title, {
      pointerId: 1,
      button: 0,
      clientX: 550,
      clientY: 100,
    });
    fireEvent.pointerMove(window, { pointerId: 1, clientX: 450, clientY: 160 });
    fireEvent.pointerUp(window, { pointerId: 1 });
    expect(shell).toHaveStyle({ left: '396px', top: '140px' });
    expect(events.onInputDisabled).not.toHaveBeenCalled();
    const resize = screen.getByRole('button', {
      name: 'oob.console.resizeWindow',
    });
    fireEvent.pointerDown(resize, {
      pointerId: 2,
      button: 0,
      clientX: 1000,
      clientY: 600,
    });
    fireEvent.pointerMove(window, {
      pointerId: 2,
      clientX: 5000,
      clientY: 5000,
    });
    fireEvent.pointerUp(window, { pointerId: 2 });
    expect(shell).toHaveStyle({
      width: '876px',
      height: '652px',
      left: '396px',
      top: '140px',
    });
    expect(shell.querySelector('iframe')).toBe(iframe);
  });

  it('页面全屏退出恢复悬浮几何，最小化及关闭由原会话处理', async () => {
    const { shell, iframe, events } = mount(true);
    const geometry = shell.getAttribute('style');
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.console.fullscreen' }),
    );
    await waitFor(() =>
      expect(shell).toHaveAttribute('data-fullscreen-mode', 'page'),
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.console.exitFullscreen' }),
    );
    await waitFor(() =>
      expect(shell).toHaveAttribute('data-fullscreen-mode', 'window'),
    );
    expect(shell.getAttribute('style')).toBe(geometry);
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.console.minimize' }),
    );
    expect(events.onMinimize).toHaveBeenCalledOnce();
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.console.closeFloating' }),
    );
    expect(events.onClose).toHaveBeenCalledOnce();
    expect(shell.querySelector('iframe')).toBe(iframe);
  });

  it('键盘可移动缩放，视口缩小后标题栏仍可操作', () => {
    const { shell } = mount(true);
    const title = screen.getByRole('button', {
      name: 'oob.console.moveWindow',
    });
    fireEvent.keyDown(title, { key: 'ArrowLeft' });
    expect(shell).toHaveStyle({ left: '486px' });
    fireEvent.keyDown(
      screen.getByRole('button', { name: 'oob.console.resizeWindow' }),
      { key: 'ArrowDown' },
    );
    expect(shell).toHaveStyle({ height: '490px' });
    vi.stubGlobal('innerWidth', 300);
    vi.stubGlobal('innerHeight', 200);
    act(() => window.dispatchEvent(new Event('resize')));
    expect(shell).toHaveStyle({
      width: '284px',
      height: '184px',
      left: '8px',
      top: '8px',
    });
  });

  it('悬浮与最小化切换保留原桥接和本地文件，操作不会隐式弹出介质', async () => {
    const { view, iframe, content, handle, events } = mount(true);
    await waitFor(() => expect(iframe.contentWindow).not.toBeNull());
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
          source: iframe.contentWindow,
          origin: window.location.origin,
          data: {
            type: 'oob-console-ready',
            bridgeProtocol: 'OobConsoleV1',
            sessionId: session.id,
            challenge,
          },
        }),
      ),
    );
    expect(events.onReady).toHaveBeenLastCalledWith(true);
    const file = new File(['test-only'], 'fixture.iso');
    expect(handle.current?.localFile(file)).toBe(true);
    expect(
      post.mock.calls.filter(
        ([message]) => message.type === 'oob-console-local-media',
      ),
    ).toHaveLength(1);
    post.mockClear();
    view.rerender(content(true, true));
    expect(handle.current?.localFile(file)).toBe(false);
    view.rerender(content(true));
    view.rerender(content(false));
    expect(view.container.querySelector('iframe')).toBe(iframe);
    expect(events.onReady).toHaveBeenLastCalledWith(true);
    expect(
      post.mock.calls.some(([message]) =>
        ['oob-console-handshake', 'oob-console-local-media'].includes(
          message.type,
        ),
      ),
    ).toBe(false);
  });

  it('全屏中关闭会先退出全屏，再交给工作区确认', async () => {
    const { shell, events } = mount(true);
    events.onClose.mockImplementation(() => {
      expect(shell).toHaveAttribute('data-fullscreen-mode', 'window');
    });
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.console.fullscreen' }),
    );
    await waitFor(() =>
      expect(shell).toHaveAttribute('data-fullscreen-mode', 'page'),
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.console.closeFloating' }),
    );
    await waitFor(() => expect(events.onClose).toHaveBeenCalledOnce());
  });
});
