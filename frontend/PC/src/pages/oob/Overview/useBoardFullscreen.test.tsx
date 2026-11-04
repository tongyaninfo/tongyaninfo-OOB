import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import React, { useState } from 'react';
import { createPortal } from 'react-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import useBoardFullscreen from './useBoardFullscreen';

const capture = vi.hoisted(() => vi.fn().mockResolvedValue(undefined));
vi.mock('@/services/oob/browserExceptions', () => ({
  captureRuntimeException: capture,
}));

let active: Element | null = null;
let enabled = true;
const request = vi.fn(function (this: HTMLElement): Promise<void> {
  active = this;
  document.dispatchEvent(new Event('fullscreenchange'));
  return Promise.resolve();
});
const exit = vi.fn((): Promise<void> => {
  active = null;
  document.dispatchEvent(new Event('fullscreenchange'));
  return Promise.resolve();
});
const originals = {
  enabled: Object.getOwnPropertyDescriptor(document, 'fullscreenEnabled'),
  element: Object.getOwnPropertyDescriptor(document, 'fullscreenElement'),
  exit: Object.getOwnPropertyDescriptor(document, 'exitFullscreen'),
  request: Object.getOwnPropertyDescriptor(
    HTMLElement.prototype,
    'requestFullscreen',
  ),
};
function restore(
  object: object,
  key: PropertyKey,
  descriptor?: PropertyDescriptor,
) {
  if (descriptor) Object.defineProperty(object, key, descriptor);
  else Reflect.deleteProperty(object, key);
}
function deferred() {
  let resolve!: () => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<void>((done, fail) => {
    resolve = done;
    reject = fail;
  });
  return { promise, resolve, reject };
}

function Popup() {
  const [count, setCount] = useState(0);
  return (
    <div role="dialog" aria-label="Details">
      <button type="button" onClick={() => setCount((value) => value + 1)}>
        Detail {count}
      </button>
    </div>
  );
}

/** 消费者使用真实按钮、根元素和Portal；原生状态只由测试中的浏览器事件驱动。 */
function Consumer({ rapid = false }: { rapid?: boolean }) {
  const board = useBoardFullscreen();
  const [popup, setPopup] = useState(false);
  return (
    <div ref={board.ref} data-testid="board">
      <button
        type="button"
        disabled={board.pending}
        onClick={() => {
          void board.toggle();
          if (rapid) void board.toggle();
        }}
      >
        {board.isFullscreen ? 'Exit fullscreen' : 'Enter fullscreen'}
      </button>
      <output aria-label="Fullscreen issue">{board.issue || ''}</output>
      <button type="button" onClick={() => setPopup(true)}>
        Show details
      </button>
      {popup && createPortal(<Popup />, board.popupContainer())}
    </div>
  );
}

beforeEach(() => {
  active = null;
  enabled = true;
  capture.mockClear();
  request.mockReset();
  request.mockImplementation(function (this: HTMLElement) {
    active = this;
    document.dispatchEvent(new Event('fullscreenchange'));
    return Promise.resolve();
  });
  exit.mockReset();
  exit.mockImplementation(() => {
    active = null;
    document.dispatchEvent(new Event('fullscreenchange'));
    return Promise.resolve();
  });
  Object.defineProperties(document, {
    fullscreenEnabled: { configurable: true, get: () => enabled },
    fullscreenElement: { configurable: true, get: () => active },
    exitFullscreen: { configurable: true, value: exit },
  });
  Object.defineProperty(HTMLElement.prototype, 'requestFullscreen', {
    configurable: true,
    value: request,
  });
});
afterEach(() => {
  cleanup();
  restore(document, 'fullscreenEnabled', originals.enabled);
  restore(document, 'fullscreenElement', originals.element);
  restore(document, 'exitFullscreen', originals.exit);
  restore(HTMLElement.prototype, 'requestFullscreen', originals.request);
  vi.restoreAllMocks();
});

describe('看板全屏的原生生命周期与浮层归属', () => {
  it('点击进入、浏览器Esc事件和再次点击退出都同步按钮，原生调用位于点击事件内', async () => {
    render(<Consumer />);
    const root = screen.getByTestId('board');
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    // 在返回用户事件时已经请求，不能等一个异步准备步骤才调用原生API。
    expect(request).toHaveBeenCalledTimes(1);
    expect(request.mock.instances[0]).toBe(root);
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Exit fullscreen' }),
      ).not.toBeDisabled(),
    );
    act(() => {
      active = null;
      document.dispatchEvent(new Event('fullscreenchange'));
    });
    expect(
      screen.getByRole('button', { name: 'Enter fullscreen' }),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Exit fullscreen' }),
      ).not.toBeDisabled(),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Exit fullscreen' }));
    expect(exit).toHaveBeenCalledTimes(1);
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Enter fullscreen' }),
      ).not.toBeDisabled(),
    );
    expect(capture).not.toHaveBeenCalled();
  });

  it('浏览器未发事件但请求完成时按真实fullscreenElement同步，不把仅Promise成功当全屏', async () => {
    request.mockImplementation(() => Promise.resolve());
    render(<Consumer />);
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Enter fullscreen' }),
      ).not.toBeDisabled(),
    );
    request.mockImplementation(function (this: HTMLElement) {
      active = this;
      return Promise.resolve();
    });
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Exit fullscreen' }),
      ).not.toBeDisabled(),
    );
  });

  it('页面收到Esc时退出自身全屏，长按、输入法组合及其他元素全屏均不误操作', async () => {
    render(<Consumer />);
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Exit fullscreen' }),
      ).not.toBeDisabled(),
    );
    fireEvent.keyDown(document, { key: 'Escape', repeat: true });
    fireEvent.keyDown(document, { key: 'Escape', isComposing: true });
    fireEvent.keyDown(document, { key: 'ArrowLeft' });
    expect(exit).not.toHaveBeenCalled();
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(exit).toHaveBeenCalledTimes(1);
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Enter fullscreen' }),
      ).not.toBeDisabled(),
    );
    act(() => {
      active = document.createElement('video');
      document.dispatchEvent(new Event('fullscreenchange'));
    });
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(exit).toHaveBeenCalledTimes(1);
  });

  it.each([
    'disabled',
    'missing',
  ] as const)('不支持全屏（%s）时显示状态，不创建异常或假全屏', async (reason) => {
    if (reason === 'disabled') enabled = false;
    else
      Object.defineProperty(HTMLElement.prototype, 'requestFullscreen', {
        configurable: true,
        value: undefined,
      });
    render(<Consumer />);
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    await waitFor(() =>
      expect(screen.getByLabelText('Fullscreen issue')).toHaveTextContent(
        'unsupported',
      ),
    );
    expect(request).not.toHaveBeenCalled();
    expect(exit).not.toHaveBeenCalled();
    expect(capture).not.toHaveBeenCalled();
    expect(
      screen.getByRole('button', { name: 'Enter fullscreen' }),
    ).not.toBeDisabled();
  });

  it('其他元素拥有全屏时不抢占或退出；该元素退出后可以正常进入看板', async () => {
    active = document.createElement('video');
    render(<Consumer />);
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    await waitFor(() =>
      expect(screen.getByLabelText('Fullscreen issue')).toHaveTextContent(
        'occupied',
      ),
    );
    expect(request).not.toHaveBeenCalled();
    expect(exit).not.toHaveBeenCalled();
    act(() => {
      active = null;
      document.dispatchEvent(new Event('fullscreenchange'));
    });
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Exit fullscreen' }),
      ).not.toBeDisabled(),
    );
    expect(screen.getByLabelText('Fullscreen issue')).toBeEmptyDOMElement();
  });

  it.each([
    new DOMException('User gesture unavailable', 'NotAllowedError'),
    new TypeError('Fullscreen request denied'),
    new DOMException('Not available', 'NotSupportedError'),
  ])('浏览器原生拒绝 $name 作为预期失败反馈，不生成系统异常', async (error) => {
    request.mockRejectedValue(error);
    render(<Consumer />);
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    await waitFor(() =>
      expect(screen.getByLabelText('Fullscreen issue')).toHaveTextContent(
        'failed',
      ),
    );
    expect(
      screen.getByRole('button', { name: 'Enter fullscreen' }),
    ).not.toBeDisabled();
    expect(capture).not.toHaveBeenCalled();
  });

  it('非预期原生异常被记录，同时允许用户重试', async () => {
    const error = new Error('Unexpected fullscreen implementation failure');
    request.mockRejectedValue(error);
    render(<Consumer />);
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    await waitFor(() => expect(capture).toHaveBeenCalledWith(error));
    expect(screen.getByLabelText('Fullscreen issue')).toHaveTextContent(
      'failed',
    );
    expect(
      screen.getByRole('button', { name: 'Enter fullscreen' }),
    ).not.toBeDisabled();
  });

  it('同一个用户事件内的快速重复调用也不会并发请求两次', async () => {
    const native = deferred();
    request.mockReturnValue(native.promise);
    render(<Consumer rapid />);
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    expect(request).toHaveBeenCalledTimes(1);
    expect(
      screen.getByRole('button', { name: 'Enter fullscreen' }),
    ).toBeDisabled();
    await act(async () => native.resolve());
    expect(
      screen.getByRole('button', { name: 'Enter fullscreen' }),
    ).not.toBeDisabled();
    expect(capture).not.toHaveBeenCalled();
  });

  it('退出请求被拒绝时保留真实全屏状态，后续Esc仍能同步', async () => {
    render(<Consumer />);
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Exit fullscreen' }),
      ).not.toBeDisabled(),
    );
    exit.mockRejectedValue(
      new DOMException('Browser refused exit', 'NotAllowedError'),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Exit fullscreen' }));
    await waitFor(() =>
      expect(screen.getByLabelText('Fullscreen issue')).toHaveTextContent(
        'failed',
      ),
    );
    expect(
      screen.getByRole('button', { name: 'Exit fullscreen' }),
    ).not.toBeDisabled();
    act(() => {
      active = null;
      document.dispatchEvent(new Event('fullscreenchange'));
    });
    expect(
      screen.getByRole('button', { name: 'Enter fullscreen' }),
    ).toBeInTheDocument();
    expect(capture).not.toHaveBeenCalled();
  });

  it('进入与退出全屏时浮层始终属于同一看板根节点，已有明细状态不重置', async () => {
    render(<Consumer />);
    const root = screen.getByTestId('board');
    fireEvent.click(screen.getByRole('button', { name: 'Show details' }));
    const popup = screen.getByRole('dialog', { name: 'Details' });
    expect(popup.parentElement).toBe(root);
    fireEvent.click(screen.getByRole('button', { name: 'Detail 0' }));
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Exit fullscreen' }),
      ).not.toBeDisabled(),
    );
    expect(screen.getByRole('dialog', { name: 'Details' })).toBe(popup);
    expect(
      screen.getByRole('button', { name: 'Detail 1' }),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Exit fullscreen' }));
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Enter fullscreen' }),
      ).not.toBeDisabled(),
    );
    expect(screen.getByRole('dialog', { name: 'Details' }).parentElement).toBe(
      root,
    );
    expect(
      screen.getByRole('button', { name: 'Detail 1' }),
    ).toBeInTheDocument();
  });

  it('卸载会退出自身全屏，且后续事件不能重新创建已卸载消费者', async () => {
    const view = render(<Consumer />);
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Exit fullscreen' }),
      ).not.toBeDisabled(),
    );
    view.unmount();
    expect(exit).toHaveBeenCalledTimes(1);
    act(() => document.dispatchEvent(new Event('fullscreenchange')));
    expect(screen.queryByTestId('board')).not.toBeInTheDocument();
  });

  it('卸载时不退出其他元素拥有的全屏', () => {
    active = document.createElement('video');
    const view = render(<Consumer />);
    view.unmount();
    expect(exit).not.toHaveBeenCalled();
  });

  it('卸载后的真实意外异常仍进入统一捕获通道，不恢复已卸载页面', async () => {
    const native = deferred();
    const error = new Error('Unexpected late fullscreen failure');
    request.mockReturnValue(native.promise);
    const view = render(<Consumer />);
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    view.unmount();
    await act(async () => native.reject(error));
    expect(capture).toHaveBeenCalledWith(error);
    expect(screen.queryByTestId('board')).not.toBeInTheDocument();
  });

  it.each([
    true,
    false,
  ])('卸载后进入请求才完成时只清理自身归属（owned=%s）', async (owned) => {
    const native = deferred();
    request.mockReturnValue(native.promise);
    const view = render(<Consumer />);
    const root = screen.getByTestId('board');
    fireEvent.click(screen.getByRole('button', { name: 'Enter fullscreen' }));
    view.unmount();
    await act(async () => {
      active = owned ? root : document.createElement('video');
      native.resolve();
    });
    expect(exit).toHaveBeenCalledTimes(owned ? 1 : 0);
    expect(screen.queryByTestId('board')).not.toBeInTheDocument();
    expect(capture).not.toHaveBeenCalled();
  });
});
