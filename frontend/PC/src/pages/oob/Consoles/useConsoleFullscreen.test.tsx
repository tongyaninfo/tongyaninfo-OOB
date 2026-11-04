import { act, cleanup, renderHook } from '@testing-library/react';
import useConsoleFullscreen from './useConsoleFullscreen';

const originalProperties: Array<{
  target: object;
  name: string;
  descriptor: PropertyDescriptor | undefined;
}> = [];

function defineProperty(
  target: object,
  name: string,
  descriptor: PropertyDescriptor,
) {
  originalProperties.push({
    target,
    name,
    descriptor: Object.getOwnPropertyDescriptor(target, name),
  });
  Object.defineProperty(target, name, descriptor);
}

function deferred() {
  let resolve!: () => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<void>((accept, fail) => {
    resolve = accept;
    reject = fail;
  });
  return { promise, resolve, reject };
}

function browser() {
  let fullscreen: Element | null = null;
  let pointer: Element | null = null;
  const order: string[] = [];
  const fullscreenChanged = (element: Element | null) => {
    fullscreen = element;
    document.dispatchEvent(new Event('fullscreenchange'));
  };
  const pointerChanged = (element: Element | null) => {
    pointer = element;
    document.dispatchEvent(new Event('pointerlockchange'));
  };
  const keyboard = {
    lock: vi.fn(async () => {
      order.push('keyboard');
    }),
    unlock: vi.fn(),
  };
  const requestFullscreen = vi.fn(function (this: HTMLElement) {
    order.push('fullscreen');
    fullscreenChanged(this);
    return Promise.resolve();
  });
  const requestPointerLock = vi.fn(function (this: HTMLElement) {
    order.push('pointer');
    pointerChanged(this);
    return Promise.resolve();
  });
  const exitFullscreen = vi.fn(async () => {
    fullscreenChanged(null);
  });
  const exitPointerLock = vi.fn(() => {
    pointerChanged(null);
  });
  vi.stubGlobal('isSecureContext', true);
  defineProperty(navigator, 'keyboard', {
    configurable: true,
    value: keyboard,
  });
  defineProperty(document, 'fullscreenElement', {
    configurable: true,
    get: () => fullscreen,
  });
  defineProperty(document, 'pointerLockElement', {
    configurable: true,
    get: () => pointer,
  });
  defineProperty(document, 'fullscreenEnabled', {
    configurable: true,
    value: true,
  });
  defineProperty(HTMLElement.prototype, 'requestFullscreen', {
    configurable: true,
    value: requestFullscreen,
  });
  defineProperty(HTMLElement.prototype, 'requestPointerLock', {
    configurable: true,
    value: requestPointerLock,
  });
  defineProperty(document, 'exitFullscreen', {
    configurable: true,
    value: exitFullscreen,
  });
  defineProperty(document, 'exitPointerLock', {
    configurable: true,
    value: exitPointerLock,
  });
  const shell = document.createElement('div');
  const viewport = document.createElement('div');
  viewport.setAttribute('role', 'application');
  shell.append(viewport);
  const hook = renderHook(() => useConsoleFullscreen());
  hook.result.current.ref.current = shell;
  return {
    ...hook,
    shell,
    viewport,
    order,
    keyboard,
    requestFullscreen,
    requestPointerLock,
    exitFullscreen,
    exitPointerLock,
    fullscreenChanged,
    pointerChanged,
  };
}

afterEach(() => {
  cleanup();
  for (const { target, name, descriptor } of originalProperties.reverse()) {
    if (descriptor) Object.defineProperty(target, name, descriptor);
    else Reflect.deleteProperty(target, name);
  }
  originalProperties.length = 0;
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

it('requests pointer before fullscreen and keyboard, then unlocks input without closing fullscreen', async () => {
  const test = browser();
  await act(() => test.result.current.enter(true));
  expect(test.order).toEqual(['pointer', 'fullscreen', 'keyboard']);
  expect(test.requestPointerLock.mock.instances[0]).toBe(test.viewport);
  expect(test.keyboard.lock).toHaveBeenCalledWith();
  expect(test.result.current).toMatchObject({
    mode: 'native',
    captured: true,
    pointerLocked: true,
    pending: false,
  });
  act(() => test.result.current.unlock());
  expect(test.result.current).toMatchObject({
    mode: 'native',
    captured: false,
    pointerLocked: false,
  });
  expect(test.exitFullscreen).not.toHaveBeenCalled();
  await act(() => test.result.current.capture());
  expect(test.result.current.captured).toBe(true);
  await act(() => test.result.current.exit());
  expect(test.result.current).toMatchObject({
    mode: 'window',
    captured: false,
    pointerLocked: false,
    pending: false,
  });
});

it.each([
  'insecure',
  'unsupported',
])('uses page fullscreen without requesting pointer lock when %s', async (reason) => {
  const test = browser();
  if (reason === 'insecure') vi.stubGlobal('isSecureContext', false);
  else
    Object.defineProperty(navigator, 'keyboard', {
      configurable: true,
      value: undefined,
    });
  await act(() => test.result.current.enter(true));
  expect(test.result.current).toMatchObject({
    mode: 'page',
    fullscreen: true,
    captured: true,
    pointerLocked: false,
  });
  expect(test.requestFullscreen).not.toHaveBeenCalled();
  expect(test.requestPointerLock).not.toHaveBeenCalled();
  act(() => test.result.current.unlock());
  await act(() => test.result.current.capture());
  expect(test.result.current.captured).toBe(true);
  expect(test.requestPointerLock).not.toHaveBeenCalled();
});

it.each([
  'keyboard',
  'fullscreen',
])('falls back to page capture when %s is rejected', async (rejected) => {
  const test = browser();
  if (rejected === 'keyboard')
    test.keyboard.lock.mockRejectedValueOnce(new Error('denied'));
  else test.requestFullscreen.mockRejectedValueOnce(new Error('denied'));
  await act(() => test.result.current.enter(true));
  expect(test.result.current).toMatchObject({
    mode: 'page',
    captured: true,
    pointerLocked: false,
    pending: false,
    issue: true,
  });
  expect(document.fullscreenElement).toBeNull();
  expect(document.pointerLockElement).toBeNull();
});

it('keeps keyboard capture when pointer lock alone fails', async () => {
  const test = browser();
  test.requestPointerLock.mockRejectedValueOnce(new Error('denied'));
  await act(() => test.result.current.enter(true));
  expect(test.result.current).toMatchObject({
    mode: 'native',
    captured: true,
    pointerLocked: false,
    issue: true,
  });
});

it.each([
  'resolve',
  'reject',
])('does not recapture or leave fullscreen when keyboard request completes after unlock: %s', async (completion) => {
  const test = browser();
  const keyboard = deferred();
  test.keyboard.lock.mockReturnValueOnce(keyboard.promise);
  let entering!: Promise<void>;
  await act(async () => {
    entering = test.result.current.enter(true);
    await Promise.resolve();
  });
  act(() => test.result.current.unlock());
  await act(async () => {
    if (completion === 'resolve') keyboard.resolve();
    else keyboard.reject(new Error('cancelled'));
    await entering;
  });
  expect(test.result.current).toMatchObject({
    mode: 'native',
    captured: false,
    pointerLocked: false,
    pending: false,
  });
  expect(test.exitFullscreen).not.toHaveBeenCalled();
});

it('releases a fullscreen request that completes after exit', async () => {
  const test = browser();
  const fullscreen = deferred();
  test.requestFullscreen.mockImplementationOnce(() =>
    fullscreen.promise.then(() => test.fullscreenChanged(test.shell)),
  );
  let entering!: Promise<void>;
  act(() => {
    entering = test.result.current.enter(true);
  });
  await act(() => test.result.current.exit());
  await act(async () => {
    fullscreen.resolve();
    await entering;
  });
  expect(test.result.current).toMatchObject({
    mode: 'window',
    captured: false,
    pointerLocked: false,
    pending: false,
  });
  expect(document.fullscreenElement).toBeNull();
  expect(test.keyboard.lock).not.toHaveBeenCalled();
});

it('releases late pointer and fullscreen ownership after unmount', async () => {
  const test = browser();
  const pointer = deferred(),
    fullscreen = deferred();
  test.requestPointerLock.mockImplementationOnce(() =>
    pointer.promise.then(() => test.pointerChanged(test.viewport)),
  );
  test.requestFullscreen.mockImplementationOnce(() =>
    fullscreen.promise.then(() => test.fullscreenChanged(test.shell)),
  );
  let entering!: Promise<void>;
  act(() => {
    entering = test.result.current.enter(true);
  });
  test.unmount();
  await act(async () => {
    pointer.resolve();
    fullscreen.resolve();
    await entering;
  });
  expect(document.fullscreenElement).toBeNull();
  expect(document.pointerLockElement).toBeNull();
  expect(test.keyboard.lock).not.toHaveBeenCalled();
});

it('revokes capture when the browser exits native fullscreen', async () => {
  const test = browser();
  await act(() => test.result.current.enter(true));
  act(() => test.fullscreenChanged(null));
  expect(test.result.current).toMatchObject({
    mode: 'window',
    captured: false,
    pointerLocked: false,
  });
  expect(test.keyboard.unlock).toHaveBeenCalled();
});

it('preserves an external exit while the original fullscreen promise is pending', async () => {
  const test = browser();
  const fullscreen = deferred();
  test.requestFullscreen.mockImplementationOnce(() => {
    test.fullscreenChanged(test.shell);
    return fullscreen.promise;
  });
  let entering!: Promise<void>;
  act(() => {
    entering = test.result.current.enter(true);
  });
  act(() => test.fullscreenChanged(null));
  await act(async () => {
    fullscreen.resolve();
    await entering;
  });
  expect(test.result.current).toMatchObject({
    mode: 'window',
    captured: false,
    pointerLocked: false,
    pending: false,
  });
  expect(test.keyboard.lock).not.toHaveBeenCalled();
});

it('releases late pointer ownership after keyboard rejection without cancelling page capture', async () => {
  const test = browser();
  const pointer = deferred();
  test.requestPointerLock.mockImplementationOnce(() =>
    pointer.promise.then(() => test.pointerChanged(test.viewport)),
  );
  test.keyboard.lock.mockRejectedValueOnce(new Error('denied'));
  await act(() => test.result.current.enter(true));
  await act(async () => {
    pointer.resolve();
    await pointer.promise;
  });
  expect(test.result.current).toMatchObject({
    mode: 'page',
    captured: true,
    pointerLocked: false,
    pending: false,
  });
  expect(document.pointerLockElement).toBeNull();
});

it('revokes capture but preserves fullscreen when pointer lock is lost externally', async () => {
  const test = browser();
  await act(() => test.result.current.enter(true));
  act(() => test.pointerChanged(null));
  expect(test.result.current).toMatchObject({
    mode: 'native',
    captured: false,
    pointerLocked: false,
  });
  expect(test.keyboard.unlock).toHaveBeenCalled();
  expect(test.exitFullscreen).not.toHaveBeenCalled();
});

it('does not exit another element fullscreen or pointer ownership', async () => {
  const test = browser();
  const other = document.createElement('div');
  act(() => {
    test.fullscreenChanged(other);
    test.pointerChanged(other);
  });
  await act(() => test.result.current.enter(true));
  await act(() => test.result.current.exit());
  expect(test.requestFullscreen).not.toHaveBeenCalled();
  expect(test.exitFullscreen).not.toHaveBeenCalled();
  expect(test.exitPointerLock).not.toHaveBeenCalled();
  expect(document.fullscreenElement).toBe(other);
  expect(document.pointerLockElement).toBe(other);
});
