import { ReloadOutlined } from '@ant-design/icons';
import { PageContainer } from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import { useLocation } from '@umijs/max';
import {
  Alert,
  App,
  Button,
  ConfigProvider,
  Empty,
  Select,
  Space,
  Tabs,
} from 'antd';
import React, { useCallback, useEffect, useRef, useState } from 'react';
import { captureRuntimeException } from '@/services/oob/browserExceptions';
import { getConsoleServers } from '@/services/oob/console';
import type { CurrentUserDto, ServerDto } from '@/services/oob/models';
import {
  isSessionExpired,
  sessionGeneration,
  subscribeSessionExpiry,
} from '@/services/oob/session';
import { useOobIntl } from '../intl';
import ConsoleSession, { type ConsoleSessionHandle } from './ConsoleSession';
import useStyles from './style';

type HostIdentity = Pick<CurrentUserDto, 'userid' | 'canUseConsole'>;
type WindowEntry = { server: ServerDto; floating: boolean; minimized: boolean };
const consolePath = '/oob/consoles';

/** 放在应用布局的固定位置；身份与权限直接决定宿主生存期，不依赖路由守卫清理。 */
export default function ConsoleWorkspaceHost({
  user,
}: {
  user?: HostIdentity;
}) {
  if (!user?.canUseConsole) return null;
  const identity = `${user.userid}:${sessionGeneration()}`;
  return <IdentityWorkspace key={identity} identity={identity} />;
}

function IdentityWorkspace({ identity }: { identity: string }) {
  const location = useLocation();
  const onConsolePage = location.pathname === consolePath;
  const [entered, setEntered] = useState(onConsolePage);
  const [expired, setExpired] = useState(isSessionExpired);
  useEffect(() => subscribeSessionExpiry(() => setExpired(true)), []);
  useEffect(() => {
    if (onConsolePage) setEntered(true);
  }, [onConsolePage]);
  if (expired || (!entered && !onConsolePage)) return null;
  return <ConsoleWorkspace identity={identity} onConsolePage={onConsolePage} />;
}

/** 标签只负责导航；服务器实例始终留在同一个 DOM 父级，不移动或重新创建 iframe。 */
function ConsoleWorkspace({
  identity,
  onConsolePage,
}: {
  identity: string;
  onConsolePage: boolean;
}) {
  const t = useOobIntl();
  const { styles } = useStyles();
  const { message, modal } = App.useApp();
  const location = useLocation();
  const root = useRef<HTMLDivElement>(null);
  const popupContainer = useCallback(() => root.current || document.body, []);
  const [entries, setEntries] = useState<WindowEntry[]>([]);
  const entriesRef = useRef<WindowEntry[]>([]);
  const [selectedId, setSelectedId] = useState<string>();
  const [activeId, setActiveId] = useState<string>();
  const [stack, setStack] = useState<string[]>([]);
  const handles = useRef(new Map<string, ConsoleSessionHandle>());
  const closing = useRef(new Set<string>());
  const confirmations = useRef(
    new Map<string, ReturnType<typeof modal.confirm>>(),
  );
  const live = useRef(true);
  const generation = useRef(sessionGeneration());
  const isLiveIdentity = () =>
    live.current &&
    !isSessionExpired() &&
    generation.current === sessionGeneration();
  const openedRequest = useRef<string | undefined>(undefined);
  useEffect(() => {
    live.current = true;
    return () => {
      live.current = false;
      // App 的确认框 holder 比工作区活得久，只销毁本身份创建的窗口，不能误关其他页面弹层。
      for (const confirmation of confirmations.current.values())
        confirmation.destroy();
      confirmations.current.clear();
    };
  }, []);
  const query = useQuery({
    queryKey: ['oob', 'console-servers', identity],
    queryFn: ({ signal }) => getConsoleServers(signal),
    enabled: onConsolePage,
    retry: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  const update = (next: WindowEntry[]) => {
    entriesRef.current = next;
    setEntries(next);
  };
  const activate = (id: string) => {
    const entry = entriesRef.current.find((item) => item.server.id === id);
    if (!entry || entry.minimized || closing.current.has(id)) return;
    setActiveId(id);
    setStack((previous) => [...previous.filter((value) => value !== id), id]);
  };
  const open = (id: string) => {
    if (!onConsolePage || closing.current.has(id)) return;
    const existing = entriesRef.current.find((item) => item.server.id === id);
    if (!existing) {
      const server = query.data?.find((item) => item.id === id);
      if (!server) return;
      if (entriesRef.current.length >= 4) {
        message.warning(t('oob.console.sessionLimit'));
        return;
      }
      update([
        ...entriesRef.current,
        { server, floating: false, minimized: false },
      ]);
    } else if (existing.minimized) {
      update(
        entriesRef.current.map((item) =>
          item.server.id === id ? { ...item, minimized: false } : item,
        ),
      );
    }
    setSelectedId(id);
    activate(id);
  };
  const requested = new URLSearchParams(location.search).get('serverId');
  useEffect(() => {
    const key = `${location.key || ''}:${requested || ''}`;
    if (
      onConsolePage &&
      requested &&
      query.data &&
      openedRequest.current !== key
    ) {
      openedRequest.current = key;
      open(requested);
    }
  }, [onConsolePage, location.key, requested, query.data]);
  const remove = (id: string) => {
    const next = entriesRef.current.filter((item) => item.server.id !== id);
    update(next);
    setStack((previous) => previous.filter((value) => value !== id));
    setSelectedId((value) => (value === id ? next.at(-1)?.server.id : value));
    setActiveId((value) => (value === id ? undefined : value));
  };
  const closeSession = async (id: string) => {
    if (!isLiveIdentity() || closing.current.has(id)) return false;
    const handle = handles.current.get(id);
    if (!handle) return false;
    closing.current.add(id);
    try {
      if (!(await handle.close())) return false;
      if (isLiveIdentity()) remove(id);
      return true;
    } catch (error) {
      void captureRuntimeException(error);
      return false;
    } finally {
      closing.current.delete(id);
    }
  };
  useEffect(() => {
    if (onConsolePage) return;
    // 普通标签保持原离页关闭语义；关闭失败保留隐藏实例，返回后可继续核对。
    const regular = entriesRef.current.filter((item) => !item.floating);
    for (const item of regular) void closeSession(item.server.id);
    setActiveId((value) =>
      entriesRef.current.some(
        (item) => item.server.id === value && item.floating && !item.minimized,
      )
        ? value
        : undefined,
    );
  }, [onConsolePage]);
  const close = (id: string) => {
    if (
      !isLiveIdentity() ||
      confirmations.current.has(id) ||
      !entriesRef.current.some((item) => item.server.id === id)
    )
      return;
    const confirmation = modal.confirm({
      title: t('oob.console.closeTitle'),
      content: t('oob.console.closeHint'),
      getContainer: popupContainer,
      onOk: async () => {
        if (!isLiveIdentity()) return;
        const closed = await closeSession(id);
        if (isLiveIdentity() && !closed)
          throw new Error('CONSOLE_CLOSE_FAILED');
      },
      afterClose: () => {
        if (confirmations.current.get(id) === confirmation)
          confirmations.current.delete(id);
      },
    });
    confirmations.current.set(id, confirmation);
  };
  const changeWindow = (
    id: string,
    values: Partial<Pick<WindowEntry, 'floating' | 'minimized'>>,
  ) => {
    if (closing.current.has(id)) return;
    update(
      entriesRef.current.map((item) =>
        item.server.id === id ? { ...item, ...values } : item,
      ),
    );
    if (values.minimized)
      setActiveId((value) => (value === id ? undefined : value));
    else activate(id);
  };
  return (
    <div ref={root} className={styles.root} data-console-workspace={identity}>
      <ConfigProvider getPopupContainer={popupContainer}>
        <div hidden={!onConsolePage}>
          <PageContainer
            title={t('oob.console.title')}
            subTitle={t('oob.console.subtitle')}
          >
            <Space
              orientation="vertical"
              size="middle"
              style={{ width: '100%' }}
            >
              <div className={styles.toolbar}>
                <Select
                  className={styles.selector}
                  showSearch
                  optionFilterProp="label"
                  placeholder={t('oob.console.choose')}
                  aria-label={t('oob.console.choose')}
                  value={undefined}
                  loading={query.isLoading}
                  disabled={query.isError}
                  options={query.data?.map((server) => ({
                    value: server.id,
                    label: `${server.name} · ${server.controllerType} · ${server.address}`,
                  }))}
                  onChange={open}
                />
                <Button
                  icon={<ReloadOutlined />}
                  loading={query.isFetching}
                  onClick={() => {
                    if (onConsolePage) void query.refetch();
                  }}
                >
                  {t('oob.refresh')}
                </Button>
              </div>
              {query.isError && (
                <Alert
                  type="error"
                  showIcon
                  title={t('oob.console.directoryFailed')}
                />
              )}
              {entries.length ? (
                <Tabs
                  type="editable-card"
                  hideAdd
                  activeKey={selectedId}
                  onChange={open}
                  onEdit={(target, action) => {
                    if (action === 'remove' && typeof target === 'string')
                      close(target);
                  }}
                  items={entries.map(({ server }) => ({
                    key: server.id,
                    label: server.name,
                  }))}
                />
              ) : (
                <Empty description={t('oob.console.empty')} />
              )}
            </Space>
          </PageContainer>
        </div>
        <div data-console-session-list>
          {entries.map(({ server, floating, minimized }) => {
            const pageVisible = onConsolePage && selectedId === server.id;
            const visible = floating || pageVisible;
            return (
              <div
                key={server.id}
                hidden={!visible}
                data-console-window={server.id}
                onPointerDownCapture={() => {
                  if (!minimized) activate(server.id);
                }}
                onFocusCapture={() => {
                  if (!minimized) activate(server.id);
                }}
              >
                <ConsoleSession
                  ref={(handle) => {
                    if (handle) handles.current.set(server.id, handle);
                    else handles.current.delete(server.id);
                  }}
                  server={server}
                  active={visible && !minimized && activeId === server.id}
                  pageVisible={pageVisible}
                  floating={floating}
                  minimized={minimized}
                  zIndex={900 + Math.max(0, stack.indexOf(server.id))}
                  getContainer={popupContainer}
                  onActivate={() => activate(server.id)}
                  onDetach={() =>
                    changeWindow(server.id, {
                      floating: true,
                      minimized: false,
                    })
                  }
                  onMinimize={() =>
                    changeWindow(server.id, { minimized: true })
                  }
                  onRestore={() =>
                    changeWindow(server.id, { minimized: false })
                  }
                  onDock={() => {
                    changeWindow(server.id, {
                      floating: false,
                      minimized: false,
                    });
                    setSelectedId(server.id);
                  }}
                  onClose={() => close(server.id)}
                />
              </div>
            );
          })}
        </div>
      </ConfigProvider>
    </div>
  );
}
