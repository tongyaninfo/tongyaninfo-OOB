import { useCallback, useEffect, useRef, useState } from 'react';

type ConsoleFullscreenMode = 'window' | 'page' | 'native';
type KeyboardLock = { lock: () => Promise<void>; unlock: () => void };
type CaptureState = {
  mode: ConsoleFullscreenMode;
  captured: boolean;
  pointerLocked: boolean;
  pending: boolean;
  issue: boolean;
};

// Keyboard Lock 属于整个 document；不同控制台实例不得互相解除对方的锁。
let keyboardOwner: { instance: symbol; request: symbol } | null = null;
const keyboardApi = () =>
  (navigator as Navigator & { keyboard?: KeyboardLock }).keyboard;

/** 控制台独立管理全屏和输入捕获；不截获浏览器保留的长按 Esc 应急退出。 */
export default function useConsoleFullscreen() {
  const ref = useRef<HTMLDivElement>(null);
  const instance = useRef(Symbol('console-fullscreen'));
  const mounted = useRef(true);
  const epoch = useRef(0);
  const captureEpoch = useRef(0);
  const wantsNative = useRef(false);
  const wantsCapture = useRef(false);
  const wantsPointer = useRef(false);
  const ownerElement = useRef<HTMLDivElement | null>(null);
  const state = useRef<CaptureState>({
    mode: 'window',
    captured: false,
    pointerLocked: false,
    pending: false,
    issue: false,
  });
  const [view, setView] = useState(state.current);
  const update = useCallback((patch: Partial<CaptureState>) => {
    state.current = { ...state.current, ...patch };
    if (mounted.current) setView(state.current);
  }, []);
  const ownsPointer = useCallback(() => {
    const element = ownerElement.current;
    return Boolean(
      element &&
        document.pointerLockElement &&
        element.contains(document.pointerLockElement),
    );
  }, []);
  const releaseKeyboard = useCallback(() => {
    if (keyboardOwner?.instance !== instance.current) return;
    keyboardOwner = null;
    keyboardApi()?.unlock();
  }, []);
  const releasePointer = useCallback(() => {
    wantsPointer.current = false;
    // 先清除本地归属，主动释放的同步事件不能被当成浏览器抢回输入。
    update({ pointerLocked: false });
    if (ownsPointer()) document.exitPointerLock?.();
  }, [ownsPointer, update]);
  const unlock = useCallback(() => {
    wantsCapture.current = false;
    captureEpoch.current++;
    releaseKeyboard();
    releasePointer();
    update({ captured: false });
  }, [releaseKeyboard, releasePointer, update]);
  const leaveNative = useCallback(async () => {
    if (
      ownerElement.current &&
      document.fullscreenElement === ownerElement.current &&
      typeof document.exitFullscreen === 'function'
    ) {
      try {
        await document.exitFullscreen();
      } catch {
        update({ issue: true });
      }
    }
  }, [update]);

  const requestPointer = useCallback(
    (request: number, captureRequest: number) => {
      const element = ownerElement.current?.querySelector<HTMLElement>(
        '[role="application"]',
      );
      if (!element?.requestPointerLock) return;
      wantsPointer.current = true;
      // 必须先于 requestFullscreen 同步调用，后者会消耗本次用户激活。
      try {
        const result = element.requestPointerLock();
        void Promise.resolve(result).then(
          () => {
            if (
              !mounted.current ||
              request !== epoch.current ||
              captureRequest !== captureEpoch.current ||
              !wantsCapture.current ||
              !wantsPointer.current
            ) {
              if (!wantsCapture.current || !wantsPointer.current)
                releasePointer();
              return;
            }
            update({ pointerLocked: ownsPointer() });
          },
          () => {
            if (
              mounted.current &&
              request === epoch.current &&
              captureRequest === captureEpoch.current
            )
              update({ pointerLocked: ownsPointer(), issue: true });
          },
        );
      } catch {
        update({ pointerLocked: false, issue: true });
      }
    },
    [ownsPointer, releasePointer, update],
  );

  const lockKeyboard = useCallback(
    async (request: number, captureRequest: number) => {
      const keyboard = keyboardApi();
      if (!keyboard) throw new Error('Keyboard Lock unavailable');
      const ownership = { instance: instance.current, request: Symbol('lock') };
      keyboardOwner = ownership;
      await keyboard.lock();
      if (
        !mounted.current ||
        request !== epoch.current ||
        captureRequest !== captureEpoch.current ||
        !wantsCapture.current ||
        document.fullscreenElement !== ownerElement.current
      ) {
        if (keyboardOwner === ownership) releaseKeyboard();
        return;
      }
      update({ captured: true });
    },
    [releaseKeyboard, update],
  );

  const fallbackToPage = useCallback(
    async (request: number, captureRequest: number) => {
      wantsNative.current = false;
      releaseKeyboard();
      releasePointer();
      // 先变更目标模式，退出原生全屏的事件不能误判为用户关闭页面全屏。
      update({ mode: 'page', captured: false, issue: true });
      await leaveNative();
      if (mounted.current && request === epoch.current)
        update({
          captured:
            captureRequest === captureEpoch.current && wantsCapture.current,
        });
    },
    [leaveNative, releaseKeyboard, releasePointer, update],
  );

  const enter = useCallback(
    async (enabled: boolean) => {
      const element = ref.current;
      if (!element || state.current.pending || state.current.mode !== 'window')
        return;
      if (
        document.fullscreenElement &&
        document.fullscreenElement !== element
      ) {
        update({ issue: true });
        return;
      }
      ownerElement.current = element;
      wantsCapture.current = enabled;
      const request = ++epoch.current;
      const captureRequest = ++captureEpoch.current;
      update({ pending: true, issue: false });
      const nativeSupported = Boolean(
        window.isSecureContext &&
          typeof keyboardApi()?.lock === 'function' &&
          typeof keyboardApi()?.unlock === 'function' &&
          typeof element.requestFullscreen === 'function' &&
          document.fullscreenEnabled !== false,
      );
      if (!nativeSupported) {
        wantsNative.current = false;
        // HTTP 或未实现 Keyboard Lock 时使用页面全屏，避免 Esc 被原生全屏抢走。
        update({ mode: 'page', captured: enabled, pending: false });
        return;
      }
      wantsNative.current = true;
      if (enabled) requestPointer(request, captureRequest);
      try {
        const fullscreen = element.requestFullscreen();
        await fullscreen;
        if (!mounted.current || request !== epoch.current) {
          if (!wantsNative.current) await leaveNative();
          return;
        }
        if (document.fullscreenElement !== element) {
          await fallbackToPage(request, captureRequest);
          return;
        }
        update({ mode: 'native' });
        if (
          enabled &&
          wantsCapture.current &&
          captureRequest === captureEpoch.current
        )
          await lockKeyboard(request, captureRequest);
      } catch {
        if (
          mounted.current &&
          request === epoch.current &&
          (captureRequest === captureEpoch.current ||
            document.fullscreenElement !== element)
        )
          await fallbackToPage(request, captureRequest);
      } finally {
        if (mounted.current && request === epoch.current)
          update({ pending: false });
      }
    },
    [fallbackToPage, leaveNative, lockKeyboard, requestPointer, update],
  );

  const capture = useCallback(async () => {
    if (
      state.current.mode === 'window' ||
      state.current.pending ||
      state.current.captured
    )
      return;
    wantsCapture.current = true;
    const request = ++epoch.current;
    const captureRequest = ++captureEpoch.current;
    if (state.current.mode === 'page') {
      update({ captured: true });
      return;
    }
    if (document.fullscreenElement !== ownerElement.current) return;
    update({ pending: true, issue: false });
    requestPointer(request, captureRequest);
    try {
      await lockKeyboard(request, captureRequest);
    } catch {
      if (
        mounted.current &&
        request === epoch.current &&
        captureRequest === captureEpoch.current
      )
        await fallbackToPage(request, captureRequest);
    } finally {
      if (mounted.current && request === epoch.current)
        update({ pending: false });
    }
  }, [fallbackToPage, lockKeyboard, requestPointer, update]);

  const exit = useCallback(async () => {
    const request = ++epoch.current;
    wantsNative.current = false;
    unlock();
    update({ mode: 'window', pending: true });
    await leaveNative();
    if (mounted.current && request === epoch.current)
      update({ pending: false });
  }, [leaveNative, unlock, update]);

  useEffect(() => {
    mounted.current = true;
    const fullscreenChange = () => {
      if (document.fullscreenElement === ownerElement.current) {
        // 退出或卸载之后才完成的申请只负责释放自己的归属，不恢复捕获。
        if (!wantsNative.current) void leaveNative();
        else update({ mode: 'native' });
        return;
      }
      if (state.current.mode === 'native') {
        epoch.current++;
        wantsNative.current = false;
        unlock();
        update({ mode: 'window', pending: false });
      }
    };
    const pointerChange = () => {
      const owned = ownsPointer();
      const previouslyOwned = state.current.pointerLocked;
      if (owned && (!wantsCapture.current || !wantsPointer.current)) {
        releasePointer();
        return;
      }
      update({ pointerLocked: owned });
      if (!owned && previouslyOwned) unlock();
    };
    const pointerError = () => {
      if (wantsCapture.current)
        update({ pointerLocked: ownsPointer(), issue: true });
    };
    document.addEventListener('fullscreenchange', fullscreenChange);
    document.addEventListener('pointerlockchange', pointerChange);
    document.addEventListener('pointerlockerror', pointerError);
    return () => {
      mounted.current = false;
      epoch.current++;
      wantsNative.current = false;
      unlock();
      document.removeEventListener('fullscreenchange', fullscreenChange);
      document.removeEventListener('pointerlockchange', pointerChange);
      document.removeEventListener('pointerlockerror', pointerError);
      void leaveNative();
    };
  }, [leaveNative, ownsPointer, releasePointer, unlock, update]);

  return {
    ref,
    ...view,
    fullscreen: view.mode !== 'window',
    enter,
    capture,
    unlock,
    exit,
  };
}
