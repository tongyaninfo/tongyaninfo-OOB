import { Alert, App, Button, Space, Switch, Tag, Typography } from 'antd';
import React, {
  forwardRef,
  useCallback,
  useEffect,
  useImperativeHandle,
  useRef,
  useState,
} from 'react';
import {
  closeConsoleSession,
  createConsoleSession,
  findConsoleSession,
  getConsoleCapabilities,
  getConsoleTraffic,
  setConsoleInput,
} from '@/services/oob/console';
import { registerConsoleCleanup } from '@/services/oob/consoleLifecycle';
import type {
  ConsoleCapabilitiesDto,
  ConsoleSessionDto,
  ServerDto,
} from '@/services/oob/models';
import { isSessionExpired, sessionGeneration } from '@/services/oob/session';
import { createUuidV4 } from '@/utils/uuid';
import { useOobIntl } from '../intl';
import ConsolePowerPanel from './ConsolePowerPanel';
import ConsoleViewport, { type ConsoleViewportHandle } from './ConsoleViewport';
import type { ConsoleInput } from './keyboard';
import ScreenKeyboard from './ScreenKeyboard';
import useStyles from './style';
import TrafficPanel from './TrafficPanel';
import { appendTraffic, type TrafficPoint } from './traffic';
import VirtualMediaDrawer from './VirtualMediaDrawer';

export type ConsoleSessionHandle = { close: () => Promise<boolean> };
export type ConsoleSessionProps = {
  server: ServerDto;
  active: boolean;
  getContainer: () => HTMLElement;
  pageVisible?: boolean;
  floating?: boolean;
  minimized?: boolean;
  zIndex?: number;
  onActivate?: () => void;
  onDetach?: () => void;
  onMinimize?: () => void;
  onRestore?: () => void;
  onDock?: () => void;
  onClose?: () => void;
};
const liveStatus = new Set(['Connecting', 'Connected']);
const mediaStatusKeys = {
  Connecting: 'oob.console.local.Connecting',
  Inserted: 'oob.console.local.Inserted',
  Ejected: 'oob.console.local.Ejected',
  Failed: 'oob.console.local.Failed',
} as const;
export default forwardRef<ConsoleSessionHandle, ConsoleSessionProps>(
  function ConsoleSession(
    {
      server,
      active,
      getContainer,
      pageVisible = true,
      floating = false,
      minimized = false,
      zIndex,
      onActivate,
      onDetach,
      onMinimize,
      onRestore,
      onDock,
      onClose,
    },
    handleRef,
  ) {
    const t = useOobIntl();
    const { styles } = useStyles();
    const { message } = App.useApp();
    const [capabilities, setCapabilities] = useState<ConsoleCapabilitiesDto>();
    const [session, setSession] = useState<ConsoleSessionDto>();
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string>();
    const [unknownRequest, setUnknownRequest] = useState<string>();
    const [bridgeReady, setBridgeReady] = useState(false);
    const [inputSuspended, setInputSuspended] = useState(false);
    const suspended = useRef(false);
    const inputVersion = useRef(0);
    const [keyboardOpen, setKeyboardOpen] = useState(false);
    const [mediaOpen, setMediaOpen] = useState(false);
    const [mediaStatus, setMediaStatus] = useState<{
      status: string;
      detail?: string;
    }>();
    const [releaseVersion, setReleaseVersion] = useState(0);
    const [points, setPoints] = useState<TrafficPoint[]>([]);
    const [mediaPoints, setMediaPoints] = useState<TrafficPoint[]>([]);
    const frame = useRef<ConsoleViewportHandle>(null);
    const sessionRoot = useRef<HTMLDivElement>(null);
    const pendingInputEnable = useRef(false);
    const live = useRef(true);
    const ownerGeneration = useRef(sessionGeneration());
    const ownsIdentity = useCallback(
      () =>
        ownerGeneration.current === sessionGeneration() && !isSessionExpired(),
      [],
    );
    const inFlight = useRef(false);
    const currentSession = useRef<ConsoleSessionDto | undefined>(undefined);
    const currentRequest = useRef<string | undefined>(undefined);
    currentSession.current = session;
    const release = useCallback(
      () => setReleaseVersion((value) => value + 1),
      [],
    );
    const onError = useCallback((detail: string) => setError(detail), []);
    const onInputDisabled = useCallback(() => {
      frame.current?.release();
      frame.current?.setInput(false);
      // 真实失效时仍可能有开启请求在途，必须使其失效；普通失焦不走授权关闭链。
      const version = ++inputVersion.current;
      if (suspended.current) return;
      suspended.current = true;
      setInputSuspended(true);
      const latest = currentSession.current;
      if (latest?.status !== 'Connected' || !ownsIdentity()) return;
      void setConsoleInput(latest.id, false).then((result) => {
        if (
          !live.current ||
          !ownsIdentity() ||
          currentSession.current?.id !== latest.id ||
          inputVersion.current !== version
        )
          return;
        if (result.success) {
          currentSession.current = result.data;
          setSession(result.data);
        } else setError(result.errorMessage);
      });
    }, [ownsIdentity]);
    const onReady = useCallback(
      (ready: boolean) => {
        setBridgeReady(ready);
        // 画面重载或桥接失败会使在途开启失效，即使服务器尚未回显 inputEnabled。
        if (!ready && pendingInputEnable.current) onInputDisabled();
      },
      [onInputDisabled],
    );
    const pauseInteraction = useCallback(() => {
      // 输入开关表示用户授权；离开画面仅释放按键与捕获，点击画面后可继续。
      frame.current?.suspend();
    }, []);
    const previousPageVisible = useRef(pageVisible);
    useEffect(() => {
      if (previousPageVisible.current !== pageVisible) pauseInteraction();
      previousPageVisible.current = pageVisible;
    }, [pageVisible, pauseInteraction]);
    useEffect(() => {
      if (!active) pauseInteraction();
    }, [active, pauseInteraction]);
    const onMediaStatus = useCallback(
      (status: string, detail?: string) => setMediaStatus({ status, detail }),
      [],
    );
    const input = useCallback(
      (event: ConsoleInput) => frame.current?.send(event),
      [],
    );
    const close = useCallback(async () => {
      inputVersion.current++;
      frame.current?.release();
      frame.current?.setInput(false);
      suspended.current = true;
      if (live.current) setInputSuspended(true);
      if (!ownsIdentity()) return true;
      let latest = currentSession.current;
      if (currentRequest.current) {
        const found = await findConsoleSession(currentRequest.current);
        if (!ownsIdentity()) return true;
        if (found.success) {
          latest = found.data;
          currentRequest.current = undefined;
        } else if (found.status === 404) currentRequest.current = undefined;
        else {
          if (live.current) setError(found.errorMessage);
          return false;
        }
      }
      if (!latest || !liveStatus.has(latest.status)) return true;
      const result = await closeConsoleSession(latest.id);
      if (!ownsIdentity()) return true;
      if (result.success) {
        currentSession.current = result.data;
        if (live.current) {
          setSession(result.data);
          setPoints((previous) =>
            previous.map((point, index) =>
              index === previous.length - 1
                ? { ...point, receiveRate: null, sendRate: null }
                : point,
            ),
          );
          setMediaPoints((previous) =>
            previous.map((point, index) =>
              index === previous.length - 1
                ? { ...point, receiveRate: null, sendRate: null }
                : point,
            ),
          );
        }
        return true;
      }
      if (live.current) setError(result.errorMessage);
      return false;
    }, [ownsIdentity]);
    useImperativeHandle(handleRef, () => ({ close }), [close]);
    useEffect(() => {
      live.current = true;
      const abort = new AbortController();
      void getConsoleCapabilities(server.id, abort.signal).then((result) => {
        if (!live.current || !ownsIdentity() || abort.signal.aborted) return;
        if (result.success) setCapabilities(result.data);
        else setError(result.errorMessage);
      });
      const unregister = registerConsoleCleanup(async (expired) => {
        if (expired) {
          inputVersion.current++;
          frame.current?.release();
          frame.current?.setInput(false);
          currentRequest.current = undefined;
          currentSession.current = undefined;
          setSession(undefined);
        } else await close();
        live.current = false;
      });
      return () => {
        live.current = false;
        abort.abort();
        unregister();
        void close();
      };
    }, [server.id, close, ownsIdentity]);
    useEffect(() => {
      if (!session || !liveStatus.has(session.status)) return;
      const abort = new AbortController();
      let timer: ReturnType<typeof setTimeout>;
      const poll = async () => {
        const result = await getConsoleTraffic(session.id, abort.signal);
        if (!live.current || !ownsIdentity() || abort.signal.aborted) return;
        if (result.success) {
          currentSession.current = result.data;
          setSession(result.data);
          const available =
            result.data.trafficAvailable && liveStatus.has(result.data.status);
          setPoints((previous) =>
            appendTraffic(
              previous,
              Date.now(),
              available ? Number(result.data.bytesReceived) : null,
              available ? Number(result.data.bytesSent) : null,
            ),
          );
          setMediaPoints((previous) =>
            appendTraffic(
              previous,
              Date.now(),
              available ? Number(result.data.mediaBytesReceived) : null,
              available ? Number(result.data.mediaBytesSent) : null,
            ),
          );
        } else {
          setError(result.errorMessage);
          onInputDisabled();
          setPoints((previous) =>
            appendTraffic(previous, Date.now(), null, null),
          );
          setMediaPoints((previous) =>
            appendTraffic(previous, Date.now(), null, null),
          );
        }
        timer = setTimeout(poll, 1000);
      };
      void poll();
      return () => {
        abort.abort();
        clearTimeout(timer);
      };
    }, [session?.id, session?.status]);
    const connect = async () => {
      if (inFlight.current || unknownRequest || !ownsIdentity()) return;
      inFlight.current = true;
      setBusy(true);
      setError(undefined);
      if (!(await close())) {
        inFlight.current = false;
        setBusy(false);
        return;
      }
      // 重连等待旧会话关闭期间可能已退出或切换账号，不得用新身份创建旧服务器会话。
      if (!live.current || !ownsIdentity()) {
        inFlight.current = false;
        return;
      }
      const requestId = createUuidV4();
      currentRequest.current = requestId;
      const result = await createConsoleSession(server.id, requestId);
      inFlight.current = false;
      if (!live.current || !ownsIdentity()) {
        if (result.success && ownsIdentity())
          void closeConsoleSession(result.data.id);
        return;
      }
      setBusy(false);
      if (result.success) {
        currentRequest.current = undefined;
        currentSession.current = result.data;
        setSession(result.data);
        setPoints([]);
        setMediaPoints([]);
        setBridgeReady(false);
        suspended.current = false;
        setInputSuspended(false);
      } else {
        setError(result.errorMessage);
        if (
          result.dispatched &&
          ['network', 'invalid-response'].includes(result.kind)
        )
          setUnknownRequest(requestId);
        else currentRequest.current = undefined;
      }
    };
    const verify = async () => {
      if (!unknownRequest || inFlight.current || !ownsIdentity()) return;
      inFlight.current = true;
      setBusy(true);
      const result = await findConsoleSession(unknownRequest);
      inFlight.current = false;
      if (!live.current || !ownsIdentity()) {
        if (result.success && ownsIdentity())
          void closeConsoleSession(result.data.id);
        return;
      }
      setBusy(false);
      if (result.success) {
        currentRequest.current = undefined;
        currentSession.current = result.data;
        setSession(result.data);
        setUnknownRequest(undefined);
      } else {
        setError(result.errorMessage);
        if (result.status === 404) {
          setUnknownRequest(undefined);
          currentRequest.current = undefined;
        }
      }
    };
    const toggleInput = async (enabled: boolean) => {
      if (!session || inFlight.current || !ownsIdentity()) return;
      if (!enabled) frame.current?.release();
      const version = ++inputVersion.current;
      if (!enabled) {
        suspended.current = true;
        setInputSuspended(true);
      }
      inFlight.current = true;
      pendingInputEnable.current = enabled;
      setBusy(true);
      try {
        const result = await setConsoleInput(session.id, enabled);
        if (!live.current || !ownsIdentity()) return;
        // 轮询可能已先收到会话终态；不等待 React 提交，也不让旧的 Connected 响应覆盖它。
        if (
          currentSession.current?.id !== session.id ||
          currentSession.current.status !== 'Connected'
        )
          return;
        if (inputVersion.current !== version) {
          if (enabled && currentSession.current?.id === session.id) {
            // 迟到的开启可能已在服务端生效；保持本地关闭，并在其结束后同步同一会话的禁用状态。
            const disableVersion = inputVersion.current;
            const disabled = await setConsoleInput(session.id, false);
            if (
              !live.current ||
              !ownsIdentity() ||
              currentSession.current?.id !== session.id ||
              currentSession.current.status !== 'Connected' ||
              inputVersion.current !== disableVersion
            )
              return;
            if (disabled.success) {
              currentSession.current = disabled.data;
              setSession(disabled.data);
            } else setError(disabled.errorMessage);
          }
          return;
        }
        if (result.success) {
          currentSession.current = result.data;
          setSession(result.data);
          frame.current?.setInput(enabled);
          suspended.current = !enabled;
          setInputSuspended(!enabled);
        } else setError(result.errorMessage);
      } finally {
        // 禁用核对完成前保持按钮忙碌，避免新一轮开启与旧请求的收尾交错。
        pendingInputEnable.current = false;
        inFlight.current = false;
        if (live.current) setBusy(false);
      }
    };
    const inputAuthorized = Boolean(
      bridgeReady &&
        !inputSuspended &&
        session?.status === 'Connected' &&
        session.inputEnabled &&
        capabilities?.keyboardSupported,
    );
    const inputReady = active && inputAuthorized;
    const inputToggleDisabled = Boolean(
      busy ||
        !bridgeReady ||
        session?.status !== 'Connected' ||
        !capabilities?.keyboardSupported,
    );
    useEffect(() => {
      if (!active || !bridgeReady) return;
      const leaveSession = (event: PointerEvent) => {
        if (!inputReady && !pendingInputEnable.current) return;
        if (
          event.target instanceof Node &&
          !sessionRoot.current?.contains(event.target)
        )
          pauseInteraction();
      };
      // 点击不可聚焦的会话外区域同样释放输入；不依赖 relatedTarget 为 null 的模糊失焦。
      document.addEventListener('pointerdown', leaveSession, true);
      return () =>
        document.removeEventListener('pointerdown', leaveSession, true);
    }, [active, bridgeReady, inputReady, pauseInteraction]);
    const sendCombination = () => {
      if (!inputReady) return;
      input({ type: 'key', code: 'ControlLeft', key: 'Control', down: true });
      input({ type: 'key', code: 'AltLeft', key: 'Alt', down: true });
      input({ type: 'key', code: 'Delete', key: 'Delete', down: true });
      input({ type: 'key', code: 'Delete', key: 'Delete', down: false });
      input({ type: 'key', code: 'AltLeft', key: 'Alt', down: false });
      input({ type: 'key', code: 'ControlLeft', key: 'Control', down: false });
    };
    return (
      <Space
        ref={sessionRoot}
        orientation="vertical"
        size="middle"
        style={{
          width: '100%',
          boxSizing: 'border-box',
          padding: pageVisible ? '0 24px 24px' : undefined,
          ...(!pageVisible ? { display: 'block', height: 0 } : {}),
        }}
        data-console-session={session?.id}
        onBlur={(event) => {
          // 从屏幕键盘或工具栏离开会话只释放当前按键，保留用户输入开关。
          // 输入开关忙碌禁用也会产生 null 失焦，由明确焦点目标或外部点击区分真实离开。
          if (
            event.relatedTarget instanceof Node &&
            !event.currentTarget.contains(event.relatedTarget)
          )
            pauseInteraction();
        }}
      >
        <div hidden={!pageVisible} className={styles.pageControls}>
          <div className={styles.toolbar}>
            <Space wrap>
              <Tag
                color={
                  session?.status === 'Connected' && bridgeReady
                    ? 'success'
                    : 'default'
                }
              >
                {session
                  ? t(
                      `oob.console.status.${session.status}` as 'oob.console.status.Connecting',
                    )
                  : t('oob.console.notConnected')}
              </Tag>
              <Typography.Text type="secondary">
                {server.controllerType} · {server.address}
              </Typography.Text>
            </Space>
            <Space wrap>
              <Button
                type="primary"
                loading={busy}
                disabled={
                  Boolean(unknownRequest) ||
                  capabilities?.status !== 'Available' ||
                  session?.status === 'Connecting'
                }
                onClick={() => void connect()}
              >
                {session
                  ? t('oob.console.reconnect')
                  : t('oob.console.connect')}
              </Button>
              <Button
                loading={busy}
                disabled={!session || !liveStatus.has(session.status)}
                onClick={() => {
                  setBusy(true);
                  void close().finally(() => {
                    if (live.current) setBusy(false);
                  });
                }}
              >
                {t('oob.console.disconnect')}
              </Button>
              <Switch
                aria-label={t('oob.console.input')}
                checked={Boolean(session?.inputEnabled && !inputSuspended)}
                disabled={inputToggleDisabled}
                onChange={(enabled) => void toggleInput(enabled)}
              />
              <Typography.Text>{t('oob.console.input')}</Typography.Text>
              <Button
                aria-pressed={keyboardOpen}
                onClick={() => setKeyboardOpen((value) => !value)}
              >
                {t('oob.console.keyboard')}
              </Button>
              <Button onClick={() => setMediaOpen(true)}>
                {t('oob.console.media')}
              </Button>
            </Space>
          </div>
          {error && <Alert type="error" showIcon title={error} />}
          {unknownRequest && (
            <Alert
              type="warning"
              showIcon
              title={t('oob.console.unknown')}
              action={
                <Button loading={busy} onClick={() => void verify()}>
                  {t('oob.console.verify')}
                </Button>
              }
            />
          )}
          {capabilities && capabilities.status !== 'Available' && (
            <Alert
              type="warning"
              showIcon
              title={capabilities.message || t('oob.console.noBridge')}
              description={capabilities.reasonCode}
            />
          )}
          {floating && (
            <Alert
              type="info"
              showIcon
              title={t('oob.console.floatingHint')}
              action={
                <Button
                  onClick={() => {
                    pauseInteraction();
                    onDock?.();
                  }}
                >
                  {t('oob.console.dock')}
                </Button>
              }
            />
          )}
        </div>
        <ConsoleViewport
          ref={frame}
          session={session}
          active={active}
          inputEnabled={inputAuthorized}
          inputToggleDisabled={inputToggleDisabled}
          onInputToggle={(enabled) => void toggleInput(enabled)}
          onReady={onReady}
          onRelease={release}
          onMediaStatus={onMediaStatus}
          onError={onError}
          onInputDisabled={onInputDisabled}
          serverName={server.name}
          floating={floating}
          minimized={minimized}
          zIndex={zIndex}
          onActivate={onActivate}
          onDetach={onDetach}
          onMinimize={onMinimize}
          onRestore={onRestore}
          onClose={onClose}
        />
        <div hidden={!pageVisible} className={styles.pageControls}>
          <Space wrap>
            <Typography.Text type="secondary">
              {!session?.frameUrl
                ? t('oob.console.inputConnectFirst')
                : !bridgeReady
                  ? t('oob.console.framePending')
                  : inputAuthorized
                    ? t('oob.console.focus')
                    : t('oob.console.inputOff')}
            </Typography.Text>
            <Button
              size="small"
              disabled={!inputReady}
              onClick={() => frame.current?.release()}
            >
              {t('oob.console.release')}
            </Button>
            <Button
              size="small"
              disabled={!inputReady}
              onClick={sendCombination}
            >
              {t('oob.console.ctrlAltDel')}
            </Button>
          </Space>
          {keyboardOpen && (
            <ScreenKeyboard
              enabled={inputReady}
              onInput={input}
              releaseVersion={releaseVersion}
            />
          )}
          <ConsolePowerPanel
            server={server}
            active={active && pageVisible}
            getContainer={getContainer}
            beforeInteraction={pauseInteraction}
          />
          <TrafficPanel points={points} mediaPoints={mediaPoints} />
          <VirtualMediaDrawer
            server={server}
            open={mediaOpen && active && pageVisible}
            onClose={() => setMediaOpen(false)}
            getContainer={getContainer}
            localSupported={Boolean(capabilities?.localMediaSupported)}
            localReady={Boolean(
              bridgeReady && active && session?.status === 'Connected',
            )}
            localStatus={
              mediaStatus
                ? `${t(mediaStatusKeys[mediaStatus.status as keyof typeof mediaStatusKeys] || 'oob.console.local.Failed')}${mediaStatus.detail ? `: ${mediaStatus.detail}` : ''}`
                : undefined
            }
            onLocalEject={() => frame.current?.ejectLocal()}
            onLocalFile={(file) => {
              const accepted = frame.current?.localFile(file) || false;
              if (!accepted) message.error(t('oob.console.localUnavailable'));
              return accepted;
            }}
          />
        </div>
      </Space>
    );
  },
);
