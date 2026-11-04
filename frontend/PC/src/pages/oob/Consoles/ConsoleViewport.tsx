import {
  BorderOutlined,
  CloseOutlined,
  ExportOutlined,
  FullscreenExitOutlined,
  FullscreenOutlined,
  MinusOutlined,
} from '@ant-design/icons';
import { Alert, Button, Switch, Typography } from 'antd';
import React, {
  forwardRef,
  useCallback,
  useEffect,
  useImperativeHandle,
  useRef,
  useState,
} from 'react';
import { flushSync } from 'react-dom';
import type { ConsoleSessionDto } from '@/services/oob/models';
import { createUuidV4 } from '@/utils/uuid';
import { useOobIntl } from '../intl';
import {
  changeFloatingWindow,
  type FloatingWindowRect,
  fitFloatingWindow,
  floatingTitleHeight,
  initialFloatingWindow,
} from './floatingWindow';
import { type ConsoleInput, createKeyTracker } from './keyboard';
import useStyles from './style';
import useConsoleFullscreen from './useConsoleFullscreen';

export type ConsoleViewportHandle = {
  send: (event: ConsoleInput) => void;
  release: () => void;
  suspend: () => void;
  localFile: (file: File) => boolean;
  ejectLocal: () => void;
  setInput: (enabled: boolean) => void;
};

/** 只信任同源受控桥接，原厂iframe加载完成不等于协议/输入/会话就绪。 */
export function isConsoleBridgeMessage(
  event: Pick<MessageEvent, 'origin' | 'source' | 'data'>,
  source: Window | null,
  sessionId: string,
  origin: string,
  challenge: string | undefined,
) {
  return (
    source !== null &&
    event.source === source &&
    event.origin === origin &&
    event.data !== null &&
    typeof event.data === 'object' &&
    event.data.sessionId === sessionId &&
    Boolean(challenge) &&
    event.data.challenge === challenge &&
    [
      'oob-console-ready',
      'oob-console-local-media-status',
      'oob-console-error',
      'oob-console-status',
      'oob-console-input-disabled',
    ].includes(event.data.type)
  );
}
export default forwardRef<
  ConsoleViewportHandle,
  {
    session?: ConsoleSessionDto;
    serverName?: string;
    active: boolean;
    floating?: boolean;
    minimized?: boolean;
    zIndex?: number;
    onDetach?: () => void;
    onMinimize?: () => void;
    onRestore?: () => void;
    onClose?: () => void;
    onActivate?: () => void;
    inputEnabled: boolean;
    inputToggleDisabled: boolean;
    onInputToggle: (enabled: boolean) => void;
    onReady: (ready: boolean) => void;
    onRelease: () => void;
    onMediaStatus: (status: string, message?: string) => void;
    onError: (message: string) => void;
    onInputDisabled: () => void;
  }
>(function ConsoleViewport(
  {
    session,
    serverName,
    active: activeProp,
    floating = false,
    minimized = false,
    zIndex = 900,
    onDetach,
    onMinimize,
    onRestore,
    onClose,
    onActivate,
    inputEnabled,
    inputToggleDisabled,
    onInputToggle,
    onReady,
    onRelease,
    onMediaStatus,
    onError,
    onInputDisabled,
  },
  handleRef,
) {
  const t = useOobIntl();
  const { styles } = useStyles();
  const fullscreen = useConsoleFullscreen();
  const collapsed = floating && minimized;
  const active = activeProp && !collapsed;
  const bounds = () => ({
    width: window.innerWidth,
    height: window.innerHeight,
  });
  const [windowRect, setWindowRect] = useState(() =>
    initialFloatingWindow(bounds()),
  );
  const windowState = useRef({ rect: windowRect, minimized: collapsed });
  windowState.current = { rect: windowRect, minimized: collapsed };
  const mounted = useRef(true);
  const gesture = useRef<
    | {
        pointerId: number;
        x: number;
        y: number;
        rect: FloatingWindowRect;
        operation: 'move' | 'resize';
      }
    | undefined
  >(undefined);
  const frame = useRef<HTMLIFrameElement>(null);
  const viewport = useRef<HTMLDivElement>(null);
  const challenge = useRef<string | undefined>(undefined);
  const [ready, setReady] = useState(false);
  const focused = useRef(false);
  const bridgeInputArmed = useRef(false);
  const inputRevoked = useRef(false);
  const cursor = useRef({ x: 0.5, y: 0.5 });
  const [cursorPosition, setCursorPosition] = useState(cursor.current);
  const state = useRef({ active, inputEnabled, ready, session });
  state.current = { active, inputEnabled, ready, session };
  const postBridge = useCallback(
    (message: Record<string, unknown>) => {
      try {
        const target = frame.current?.contentWindow;
        if (!target) return false;
        target.postMessage(
          { ...message, challenge: challenge.current },
          window.location.origin,
        );
        return true;
      } catch {
        // iframe导航或销毁后不再信任旧握手；输入失败不能成为未处理异常或自动重发。
        state.current.ready = false;
        bridgeInputArmed.current = false;
        focused.current = false;
        setReady(false);
        onReady(false);
        return false;
      }
    },
    [onReady],
  );
  let frameUrl: string | undefined;
  if (session?.frameUrl) {
    try {
      const parsed = new URL(session.frameUrl, window.location.href);
      if (
        parsed.origin === window.location.origin &&
        parsed.pathname.startsWith(
          `/api/oob/console-sessions/${encodeURIComponent(session.id)}/frame/`,
        )
      )
        frameUrl = parsed.href;
    } catch {
      /* 后端返回无效地址时不加载未知来源。 */
    }
  }
  const armInput = useCallback(() => {
    const current = state.current;
    if (
      inputRevoked.current ||
      !current.active ||
      !current.inputEnabled ||
      !current.ready ||
      current.session?.status !== 'Connected'
    )
      return false;
    if (!bridgeInputArmed.current)
      bridgeInputArmed.current = postBridge({
        type: 'oob-console-input-state',
        sessionId: current.session.id,
        enabled: true,
      });
    return bridgeInputArmed.current;
  }, [postBridge]);
  const disarmInput = useCallback(
    (force = false) => {
      const wasArmed = bridgeInputArmed.current;
      bridgeInputArmed.current = false;
      const current = state.current;
      if ((wasArmed || force) && current.ready && current.session)
        postBridge({
          type: 'oob-console-input-state',
          sessionId: current.session.id,
          enabled: false,
        });
    },
    [postBridge],
  );
  const post = useCallback(
    (event: ConsoleInput) => {
      const current = state.current;
      const releasing =
        event.type === 'release-all' || (event.type === 'key' && !event.down);
      if (
        !current.ready ||
        !current.session ||
        (!releasing &&
          (!current.active ||
            !current.inputEnabled ||
            inputRevoked.current ||
            current.session.status !== 'Connected' ||
            !bridgeInputArmed.current))
      )
        return;
      postBridge({
        type: 'oob-console-input',
        sessionId: current.session.id,
        event,
      });
    },
    [postBridge],
  );
  const tracker = useRef(createKeyTracker(post));
  const release = useCallback(() => {
    tracker.current.release();
    onRelease();
  }, [onRelease]);
  // 失焦只暂停本地桥接和捕获，保留用户的服务端授权；下一次明确输入动作再恢复桥接。
  const loseControl = useCallback(() => {
    focused.current = false;
    fullscreen.unlock();
    release();
    disarmInput();
  }, [release, disarmInput, fullscreen.unlock]);
  const suspend = useCallback(() => {
    loseControl();
    if (fullscreen.fullscreen || fullscreen.pending) void fullscreen.exit();
  }, [loseControl, fullscreen.fullscreen, fullscreen.pending, fullscreen.exit]);
  useEffect(() => {
    gesture.current = undefined;
    if (floating)
      setWindowRect((rect) => fitFloatingWindow(rect, bounds(), collapsed));
  }, [floating, collapsed]);
  useEffect(() => {
    mounted.current = true;
    const move = (event: PointerEvent) => {
      const current = gesture.current;
      if (!current || current.pointerId !== event.pointerId) return;
      event.preventDefault();
      setWindowRect(
        changeFloatingWindow(
          current.rect,
          event.clientX - current.x,
          event.clientY - current.y,
          current.operation,
          bounds(),
          windowState.current.minimized,
        ),
      );
    };
    const finish = (event: PointerEvent) => {
      if (gesture.current?.pointerId === event.pointerId)
        gesture.current = undefined;
    };
    const resize = () => {
      gesture.current = undefined;
      setWindowRect((rect) =>
        fitFloatingWindow(rect, bounds(), windowState.current.minimized),
      );
    };
    const cancel = () => {
      gesture.current = undefined;
    };
    window.addEventListener('pointermove', move, { passive: false });
    window.addEventListener('pointerup', finish);
    window.addEventListener('pointercancel', finish);
    window.addEventListener('resize', resize);
    window.addEventListener('blur', cancel);
    return () => {
      mounted.current = false;
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', finish);
      window.removeEventListener('pointercancel', finish);
      window.removeEventListener('resize', resize);
      window.removeEventListener('blur', cancel);
    };
  }, []);
  const startGesture = (
    event: React.PointerEvent<HTMLElement>,
    operation: 'move' | 'resize',
  ) => {
    if (!floating || fullscreen.fullscreen || event.button !== 0) return;
    event.preventDefault();
    event.stopPropagation();
    suspend();
    event.currentTarget.focus();
    event.currentTarget.setPointerCapture?.(event.pointerId);
    gesture.current = {
      pointerId: event.pointerId,
      x: event.clientX,
      y: event.clientY,
      rect: windowState.current.rect,
      operation,
    };
  };
  const keyboardGeometry = (
    event: React.KeyboardEvent<HTMLElement>,
    operation: 'move' | 'resize',
  ) => {
    if (!floating || fullscreen.fullscreen || !event.key.startsWith('Arrow'))
      return;
    event.preventDefault();
    event.stopPropagation();
    suspend();
    const step = event.shiftKey ? 1 : 10;
    setWindowRect((rect) =>
      changeFloatingWindow(
        rect,
        event.key === 'ArrowLeft'
          ? -step
          : event.key === 'ArrowRight'
            ? step
            : 0,
        event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0,
        operation,
        bounds(),
        collapsed,
      ),
    );
  };
  useImperativeHandle(
    handleRef,
    () => ({
      send(event) {
        // 屏幕键盘和组合键属于明确输入动作；普通 move/wheel 不能从暂停中自动恢复。
        if (event.type === 'key' && event.down && !armInput()) return;
        post(event);
      },
      release,
      suspend,
      setInput(enabled) {
        inputRevoked.current = !enabled;
        if (!enabled) {
          focused.current = false;
          disarmInput(true);
        } else if (focused.current) armInput();
      },
      ejectLocal() {
        const current = state.current;
        if (current.active && current.ready && current.session)
          postBridge({
            type: 'oob-console-local-media',
            sessionId: current.session.id,
            action: 'eject',
          });
      },
      localFile(file) {
        const current = state.current;
        if (!current.active || !current.ready || !current.session) return false;
        return postBridge({
          type: 'oob-console-local-media',
          sessionId: current.session.id,
          action: 'insert',
          file,
        });
      },
    }),
    [post, release, postBridge, suspend, armInput, disarmInput],
  );
  useEffect(() => {
    focused.current = false;
    bridgeInputArmed.current = false;
    inputRevoked.current = false;
    setReady(false);
    onReady(false);
    const receive = (event: MessageEvent) => {
      if (
        session?.bridgeProtocol !== 'OobConsoleV1' ||
        !isConsoleBridgeMessage(
          event,
          frame.current?.contentWindow || null,
          session.id,
          window.location.origin,
          challenge.current,
        )
      )
        return;
      if (
        event.data.type === 'oob-console-ready' &&
        event.data.bridgeProtocol === 'OobConsoleV1'
      ) {
        setReady(true);
        onReady(true);
      }
      if (
        event.data.type === 'oob-console-local-media-status' &&
        typeof event.data.status === 'string'
      )
        onMediaStatus(
          event.data.status,
          typeof event.data.message === 'string'
            ? event.data.message
            : undefined,
        );
      if (
        event.data.type === 'oob-console-error' &&
        typeof event.data.message === 'string'
      ) {
        loseControl();
        // 协议/传输错误属于真实失效，必须撤销旧授权，不能被迟到 ready 或再次点击恢复。
        inputRevoked.current = true;
        state.current.ready = false;
        setReady(false);
        onReady(false);
        onInputDisabled();
        onError(event.data.message);
      }
      if (event.data.type === 'oob-console-input-disabled') {
        // 两家受控桥接都用该消息表示 iframe blur/hidden，本地暂停不等于撤销服务端授权。
        loseControl();
      }
    };
    window.addEventListener('message', receive);
    return () => {
      window.removeEventListener('message', receive);
      tracker.current.release();
    };
  }, [session?.id, session?.bridgeProtocol, frameUrl]);
  useEffect(() => {
    if (!active || !inputEnabled || !ready || session?.status !== 'Connected')
      loseControl();
  }, [active, inputEnabled, ready, session?.status, loseControl]);
  useEffect(() => {
    // 隐藏标签不能继续占用浏览器全屏，但保留其控制台会话与 iframe。
    if (!active && fullscreen.fullscreen) void fullscreen.exit();
  }, [active, fullscreen.fullscreen, fullscreen.exit]);
  const previousCapture = useRef(false);
  const previousFullscreen = useRef(false);
  useEffect(() => {
    // 浏览器应急退出或指针锁被撤销，沿用同一释放链，绝不自动重新抓取。
    if (
      (previousCapture.current && !fullscreen.captured) ||
      (previousFullscreen.current && !fullscreen.fullscreen)
    )
      loseControl();
    previousCapture.current = fullscreen.captured;
    previousFullscreen.current = fullscreen.fullscreen;
  }, [fullscreen.captured, fullscreen.fullscreen, loseControl]);
  useEffect(() => {
    if (fullscreen.captured && active && inputEnabled && ready) {
      // captured 只由用户明确的全屏/捕获请求成功产生；hook 的 epoch 已过滤失焦后的迟到申请。
      if (!armInput()) return;
      viewport.current?.focus();
      focused.current = true;
    }
  }, [fullscreen.captured, active, inputEnabled, ready, armInput]);
  useEffect(() => {
    const visibility = () => {
      if (document.visibilityState === 'hidden') loseControl();
    };
    window.addEventListener('blur', loseControl);
    document.addEventListener('visibilitychange', visibility);
    return () => {
      window.removeEventListener('blur', loseControl);
      document.removeEventListener('visibilitychange', visibility);
    };
  }, [loseControl]);
  const enabled = active && inputEnabled && ready;
  useEffect(() => {
    const shortcut = (event: KeyboardEvent) => {
      const current = state.current;
      if (
        !current.active ||
        !current.inputEnabled ||
        !current.ready ||
        event.isComposing
      )
        return;
      const target = event.target instanceof Element ? event.target : null;
      const ownSession =
        target
          ?.closest('[data-console-session]')
          ?.getAttribute('data-console-session') === current.session?.id;
      const ownKeyboard =
        ownSession && Boolean(target?.closest('[data-console-keyboard]'));
      const ownViewport =
        focused.current &&
        target !== null &&
        Boolean(viewport.current?.contains(target));
      if (!fullscreen.captured && !ownViewport && !ownKeyboard) return;
      // 只保留物理 Ctrl+Alt 为本地解锁；屏幕键盘组合与 AltGr 仍可发往远端。
      if (
        event.type === 'keydown' &&
        event.ctrlKey &&
        event.altKey &&
        !event.getModifierState('AltGraph') &&
        ['ControlLeft', 'ControlRight', 'AltLeft', 'AltRight'].includes(
          event.code,
        )
      ) {
        event.preventDefault();
        event.stopImmediatePropagation();
        loseControl();
      } else if (event.code === 'Escape' && ownKeyboard) {
        event.preventDefault();
        event.stopImmediatePropagation();
        if (event.type === 'keydown') {
          if (armInput()) tracker.current.down(event.code, event.key);
        } else tracker.current.up(event.code, event.key);
      }
    };
    document.addEventListener('keydown', shortcut, true);
    document.addEventListener('keyup', shortcut, true);
    return () => {
      document.removeEventListener('keydown', shortcut, true);
      document.removeEventListener('keyup', shortcut, true);
    };
  }, [fullscreen.captured, loseControl, armInput]);
  useEffect(() => {
    const element = viewport.current;
    const wheel = (event: WheelEvent) => {
      if (
        !state.current.active ||
        !state.current.inputEnabled ||
        !state.current.ready ||
        !focused.current ||
        document.activeElement !== element
      )
        return;
      event.preventDefault();
      post({ type: 'wheel', deltaX: event.deltaX, deltaY: event.deltaY });
    };
    element?.addEventListener('wheel', wheel, { passive: false });
    return () => element?.removeEventListener('wheel', wheel);
  }, [post]);
  const pointer = (
    event: React.PointerEvent<HTMLDivElement>,
    action: 'down' | 'up' | 'move',
  ) => {
    if (!enabled || (action !== 'down' && !focused.current)) return;
    if (action === 'down') {
      if (!armInput()) return;
      event.currentTarget.focus();
      focused.current = true;
      event.currentTarget.setPointerCapture?.(event.pointerId);
      if (fullscreen.fullscreen && !fullscreen.captured && !fullscreen.pending)
        void fullscreen.capture();
    }
    // 按实际 iframe 内容范围归一化，避免外层边框在高分辨率远端放大成坐标偏差。
    const rect =
      frame.current?.getBoundingClientRect() ||
      event.currentTarget.getBoundingClientRect();
    if (rect.width && rect.height) {
      // Pointer Lock 只提供相对移动；累积成厂商桥接所需的归一化绝对坐标。
      const position = fullscreen.pointerLocked
        ? {
            x:
              cursor.current.x +
              (action === 'move' ? event.movementX / rect.width : 0),
            y:
              cursor.current.y +
              (action === 'move' ? event.movementY / rect.height : 0),
          }
        : {
            x: (event.clientX - rect.left) / rect.width,
            y: (event.clientY - rect.top) / rect.height,
          };
      cursor.current = {
        x: Math.min(1, Math.max(0, position.x)),
        y: Math.min(1, Math.max(0, position.y)),
      };
      if (fullscreen.pointerLocked) setCursorPosition(cursor.current);
      post({
        type: 'pointer',
        ...cursor.current,
        buttons: event.buttons,
        action,
      });
    }
  };
  const fullscreenLabel = t(
    fullscreen.fullscreen
      ? 'oob.console.exitFullscreen'
      : 'oob.console.fullscreen',
  );
  const fullscreenButton = (
    <Button
      type="text"
      shape="circle"
      className={styles.fullscreenButton}
      icon={
        fullscreen.fullscreen ? (
          <FullscreenExitOutlined />
        ) : (
          <FullscreenOutlined />
        )
      }
      disabled={!frameUrl || fullscreen.pending}
      aria-pressed={fullscreen.fullscreen}
      aria-label={fullscreenLabel}
      title={fullscreenLabel}
      onClick={() => {
        release();
        if (fullscreen.fullscreen) {
          loseControl();
          void fullscreen.exit();
        } else {
          if (collapsed) {
            // 浏览器 API 读取 DOM 前先恢复画面；保留同次用户激活，不能延迟到 effect 再申请全屏。
            flushSync(() => onRestore?.());
          }
          void fullscreen.enter(enabled);
        }
      }}
    />
  );
  return (
    <>
      <div
        ref={fullscreen.ref}
        className={styles.viewportShell}
        data-fullscreen-mode={fullscreen.mode}
        data-floating={floating}
        data-minimized={collapsed}
        onPointerDownCapture={() => {
          if (floating) onActivate?.();
        }}
        onFocusCapture={() => {
          if (floating) onActivate?.();
        }}
        style={
          floating && !fullscreen.fullscreen
            ? {
                left: windowRect.x,
                top: windowRect.y,
                width: windowRect.width,
                height: collapsed ? floatingTitleHeight : windowRect.height,
                zIndex,
              }
            : undefined
        }
      >
        <div
          className={styles.floatingTitlebar}
          style={!floating ? { display: 'none' } : undefined}
        >
          <button
            type="button"
            className={styles.floatingTitle}
            aria-label={t('oob.console.moveWindow')}
            title={serverName || session?.serverName}
            onPointerDown={(event) => startGesture(event, 'move')}
            onKeyDown={(event) => keyboardGeometry(event, 'move')}
          >
            {serverName || session?.serverName}
          </button>
          <Button
            type="text"
            shape="circle"
            className={styles.fullscreenButton}
            icon={collapsed ? <BorderOutlined /> : <MinusOutlined />}
            aria-label={t(
              collapsed ? 'oob.console.restore' : 'oob.console.minimize',
            )}
            title={t(
              collapsed ? 'oob.console.restore' : 'oob.console.minimize',
            )}
            onClick={() => {
              suspend();
              if (collapsed) onRestore?.();
              else onMinimize?.();
            }}
          />
          {floating && fullscreenButton}
          <Button
            type="text"
            shape="circle"
            className={styles.fullscreenButton}
            icon={<CloseOutlined />}
            aria-label={t('oob.console.closeFloating')}
            title={t('oob.console.closeFloating')}
            onClick={async () => {
              loseControl();
              // 确认框位于工作区门户；先离开全屏，避免确认框被原生全屏遮挡。
              if (fullscreen.fullscreen || fullscreen.pending)
                await fullscreen.exit();
              if (
                !mounted.current ||
                (fullscreen.ref.current &&
                  document.fullscreenElement === fullscreen.ref.current)
              )
                return;
              onClose?.();
            }}
          />
        </div>
        <div
          ref={viewport}
          className={styles.viewport}
          style={collapsed ? { display: 'none' } : undefined}
          role="application"
          aria-label={`${t('oob.console.title')} ${session?.serverName || ''}`}
          tabIndex={enabled ? 0 : -1}
          onBlur={(event) => {
            focused.current = false;
            const next =
              event.relatedTarget instanceof Element
                ? event.relatedTarget
                : null;
            if (
              next
                ?.closest('[data-console-session]')
                ?.getAttribute('data-console-session') === session?.id
            )
              // 同一会话的屏幕键盘和工具栏属于控制区，换焦点仅释放按键。
              release();
            else loseControl();
          }}
          onPointerDown={(event) => pointer(event, 'down')}
          onPointerMove={(event) => pointer(event, 'move')}
          onPointerUp={(event) => pointer(event, 'up')}
          onPointerCancel={loseControl}
          onContextMenu={(event) => {
            if (enabled) event.preventDefault();
          }}
          onKeyDown={(event) => {
            if (!enabled || !focused.current || event.nativeEvent.isComposing)
              return;
            event.preventDefault();
            event.stopPropagation();
            tracker.current.down(event.code, event.key);
          }}
          onKeyUp={(event) => {
            if (!enabled || !focused.current || event.nativeEvent.isComposing)
              return;
            event.preventDefault();
            event.stopPropagation();
            tracker.current.up(event.code, event.key);
          }}
        >
          {frameUrl ? (
            <iframe
              key={session?.id}
              ref={frame}
              src={frameUrl}
              title={`${t('oob.console.title')} ${session?.serverName}`}
              tabIndex={-1}
              style={{ pointerEvents: 'none' }}
              onLoad={() => {
                tracker.current.release();
                focused.current = false;
                bridgeInputArmed.current = false;
                inputRevoked.current = Boolean(session?.inputEnabled);
                state.current.ready = false;
                if (session?.inputEnabled) onInputDisabled();
                setReady(false);
                onReady(false);
                challenge.current = createUuidV4();
                postBridge({
                  type: 'oob-console-handshake',
                  sessionId: session?.id,
                });
              }}
            />
          ) : (
            <div className={styles.viewportEmpty}>
              <Typography.Paragraph>
                {session?.message || t('oob.console.empty')}
              </Typography.Paragraph>
            </div>
          )}
          {fullscreen.pointerLocked && (
            <span
              className={styles.remoteCursor}
              aria-hidden
              style={{
                left: `${cursorPosition.x * 100}%`,
                top: `${cursorPosition.y * 100}%`,
              }}
            />
          )}
        </div>
        {fullscreen.fullscreen && (
          <div className={styles.captureHint} role="status">
            <div>
              {t(
                fullscreen.captured
                  ? 'oob.console.captureActive'
                  : 'oob.console.captureIdle',
              )}
            </div>
            {fullscreen.mode === 'page' ? (
              <div>{t('oob.console.pageFullscreenHint')}</div>
            ) : (
              <div>
                {t(
                  fullscreen.pointerLocked
                    ? 'oob.console.emergencyExit'
                    : 'oob.console.pointerUnlocked',
                )}
              </div>
            )}
          </div>
        )}
        <div
          className={styles.viewportActions}
          data-fullscreen={fullscreen.fullscreen}
          data-floating={floating}
          style={collapsed ? { display: 'none' } : undefined}
        >
          {(fullscreen.fullscreen || floating) && (
            <>
              <Typography.Text style={{ color: '#fff' }}>
                {t('oob.console.input')}
              </Typography.Text>
              <Switch
                aria-label={t('oob.console.input')}
                checked={inputEnabled}
                disabled={inputToggleDisabled}
                onChange={onInputToggle}
              />
            </>
          )}
          {!floating && fullscreenButton}
          {!floating && !fullscreen.fullscreen && onDetach && (
            <Button
              type="text"
              shape="circle"
              className={styles.fullscreenButton}
              icon={<ExportOutlined />}
              disabled={!frameUrl || fullscreen.pending}
              aria-label={t('oob.console.detach')}
              title={t('oob.console.detach')}
              onClick={() => {
                suspend();
                onDetach();
              }}
            />
          )}
        </div>
        {floating && !collapsed && !fullscreen.fullscreen && (
          <button
            type="button"
            className={styles.floatingResize}
            aria-label={t('oob.console.resizeWindow')}
            title={t('oob.console.resizeWindow')}
            onPointerDown={(event) => startGesture(event, 'resize')}
            onKeyDown={(event) => keyboardGeometry(event, 'resize')}
          />
        )}
      </div>
      {fullscreen.issue && (
        <Alert
          type="warning"
          showIcon
          title={t('oob.console.fullscreenFailed')}
        />
      )}
    </>
  );
});
