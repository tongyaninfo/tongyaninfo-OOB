import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import React, {
  forwardRef,
  useEffect,
  useImperativeHandle,
  useState,
} from 'react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { ServerDto } from '@/services/oob/models';
import { beginSession, expireSession } from '@/services/oob/session';

const state = vi.hoisted(() => ({
  location: {
    pathname: '/oob/consoles',
    search: '?serverId=9007199254740993',
    key: 'first',
  },
  mounts: new Map<string, number>(),
  unmounts: new Map<string, number>(),
  close: vi.fn(),
  connect: vi.fn(),
  directory: vi.fn(),
  closeRequests: new Map<string, () => void>(),
}));
vi.mock('@umijs/max', () => ({ useLocation: () => state.location }));
vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));
vi.mock('@ant-design/pro-components', () => ({
  PageContainer: ({ children }: { children: React.ReactNode }) => (
    <main>{children}</main>
  ),
}));
vi.mock('@/services/oob/console', () => ({
  getConsoleServers: (...args: unknown[]) => state.directory(...args),
}));
vi.mock('@/services/oob/browserExceptions', () => ({
  captureRuntimeException: vi.fn(),
}));
vi.mock('./ConsoleSession', () => ({
  default: forwardRef(function Session(
    {
      server,
      active,
      floating,
      minimized,
      pageVisible,
      onDetach,
      onMinimize,
      onRestore,
      onDock,
      onClose,
    }: {
      server: ServerDto;
      active: boolean;
      floating: boolean;
      minimized: boolean;
      pageVisible: boolean;
      onDetach: () => void;
      onMinimize: () => void;
      onRestore: () => void;
      onDock: () => void;
      onClose: () => void;
    },
    ref,
  ) {
    const [value, setValue] = useState(0);
    state.closeRequests.set(server.id, onClose);
    useEffect(() => {
      state.mounts.set(server.id, (state.mounts.get(server.id) || 0) + 1);
      return () => {
        state.unmounts.set(server.id, (state.unmounts.get(server.id) || 0) + 1);
      };
    }, [server.id]);
    useImperativeHandle(
      ref,
      () => ({ close: async () => state.close(server.id) }),
      [server.id],
    );
    return (
      <section
        data-testid={`session-${server.id}`}
        data-active={active}
        data-floating={floating}
        data-minimized={minimized}
        data-page-visible={pageVisible}
      >
        <button
          type="button"
          onClick={() => {
            state.connect(server.id);
            setValue((count) => count + 1);
          }}
        >
          {server.name} session {value}
        </button>
        <button type="button" onClick={onDetach}>
          {server.name} detach
        </button>
        <button type="button" onClick={onMinimize}>
          {server.name} minimize
        </button>
        <button type="button" onClick={onRestore}>
          {server.name} restore
        </button>
        <button type="button" onClick={onDock}>
          {server.name} dock
        </button>
        <button type="button" onClick={onClose}>
          {server.name} close
        </button>
        <iframe title={`${server.name} iframe`} src="about:blank" />
      </section>
    );
  }),
}));

import ConsoleWorkspaceHost from './ConsoleWorkspaceHost';

const servers = Array.from({ length: 5 }, (_, index) => ({
  id: (9007199254740993n + BigInt(index * 2)).toString(),
  name: `Server ${String.fromCharCode(65 + index)}`,
  controllerType: 'Ilo4',
  address: `https://${index}.invalid`,
}));
const user = { userid: '100', canUseConsole: true };
const clients: QueryClient[] = [];
beforeEach(() => {
  beginSession();
  state.location = {
    pathname: '/oob/consoles',
    search: `?serverId=${servers[0].id}`,
    key: 'first',
  };
  state.mounts.clear();
  state.unmounts.clear();
  state.close.mockReset().mockResolvedValue(true);
  state.connect.mockReset();
  state.directory.mockReset().mockResolvedValue(servers);
  state.closeRequests.clear();
});
afterEach(() => {
  cleanup();
  for (const client of clients.splice(0)) client.clear();
});
function OtherConfirmation() {
  const { modal } = App.useApp();
  return (
    <button
      type="button"
      onClick={() => modal.confirm({ title: 'Other confirmation' })}
    >
      Open other confirmation
    </button>
  );
}
function mount(
  initialUser: typeof user | undefined = user,
  withOtherConfirmation = false,
) {
  let currentUser: typeof user | undefined = initialUser;
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  clients.push(client);
  const content = () => (
    <ConfigProvider theme={{ token: { motion: false } }}>
      <App>
        {withOtherConfirmation && <OtherConfirmation />}
        <QueryClientProvider client={client}>
          <ConsoleWorkspaceHost user={currentUser} />
        </QueryClientProvider>
      </App>
    </ConfigProvider>
  );
  const view = render(content());
  return {
    ...view,
    route: (pathname: string, index?: number) => {
      state.location = {
        pathname,
        search: index === undefined ? '' : `?serverId=${servers[index].id}`,
        key: `${state.location.key}-next`,
      };
      view.rerender(content());
    },
    identity: (next: typeof user | undefined) => {
      currentUser = next;
      view.rerender(content());
    },
  };
}

it('普通主页首次挂载不查询目录、不建立任何控制台；首次进入后才加载', async () => {
  state.location = { pathname: '/oob/overview', search: '', key: 'overview' };
  const view = mount();
  expect(state.directory).not.toHaveBeenCalled();
  expect(state.mounts.size).toBe(0);
  view.route('/oob/consoles', 0);
  await screen.findByRole('button', { name: 'Server A session 0' });
  expect(state.directory).toHaveBeenCalledTimes(1);
  expect(state.connect).not.toHaveBeenCalled();
});

it('重复打开同台服务器、切标签、提取、最小化、跨路由和归位均保留相同 iframe 与父节点', async () => {
  const view = mount();
  const connect = await screen.findByRole('button', {
    name: 'Server A session 0',
  });
  fireEvent.click(connect);
  const frame = screen.getByTitle('Server A iframe');
  const parent = frame.parentNode;
  fireEvent.click(screen.getByRole('button', { name: 'Server A detach' }));
  view.route('/oob/consoles', 1);
  await screen.findByRole('button', { name: 'Server B session 0' });
  view.route('/oob/consoles', 0);
  await screen.findByRole('button', { name: 'Server A session 1' });
  fireEvent.click(screen.getByRole('button', { name: 'Server A minimize' }));
  expect(screen.getByTestId(`session-${servers[0].id}`)).toHaveAttribute(
    'data-active',
    'false',
  );
  view.route('/oob/overview');
  await waitFor(() => expect(state.close).toHaveBeenCalledWith(servers[1].id));
  expect(state.close).not.toHaveBeenCalledWith(servers[0].id);
  expect(screen.getByTitle('Server A iframe')).toBe(frame);
  expect(frame.parentNode).toBe(parent);
  fireEvent.click(screen.getByRole('button', { name: 'Server A restore' }));
  expect(screen.getByTestId(`session-${servers[0].id}`)).toHaveAttribute(
    'data-active',
    'true',
  );
  view.route('/oob/consoles', 0);
  fireEvent.click(await screen.findByRole('button', { name: 'Server A dock' }));
  expect(screen.getByTestId(`session-${servers[0].id}`)).toHaveAttribute(
    'data-floating',
    'false',
  );
  expect(screen.getByTitle('Server A iframe')).toBe(frame);
  expect(frame.parentNode).toBe(parent);
  expect(state.mounts.get(servers[0].id)).toBe(1);
  expect(state.connect).toHaveBeenCalledTimes(1);
});

it('普通标签离页只关闭一次，成功移除；关闭失败保留隐藏实例并在返回时复用', async () => {
  state.close.mockResolvedValue(false);
  const view = mount();
  await screen.findByRole('button', { name: 'Server A session 0' });
  const frame = screen.getByTitle('Server A iframe');
  view.route('/oob/servers');
  await waitFor(() => expect(state.close).toHaveBeenCalledTimes(1));
  expect(
    screen.queryByRole('button', { name: 'Server A session 0' }),
  ).not.toBeInTheDocument();
  view.route('/oob/overview');
  expect(state.close).toHaveBeenCalledTimes(1);
  view.route('/oob/consoles', 0);
  await screen.findByRole('button', { name: 'Server A session 0' });
  expect(screen.getByTitle('Server A iframe')).toBe(frame);
  state.close.mockResolvedValue(true);
  view.route('/oob/overview');
  await waitFor(() =>
    expect(screen.queryByTitle('Server A iframe')).not.toBeInTheDocument(),
  );
  expect(state.close).toHaveBeenCalledTimes(2);
  expect(state.connect).not.toHaveBeenCalled();
});

it.each([
  'logout',
  'identity',
  'permission',
  'expired',
] as const)('%s直接清空宿主与所有提取窗口，不依赖路由跳转', async (reason) => {
  const view = mount();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Server A detach' }),
  );
  view.route('/oob/overview');
  if (reason === 'logout') view.identity(undefined);
  if (reason === 'identity') view.identity({ ...user, userid: '200' });
  if (reason === 'permission') view.identity({ ...user, canUseConsole: false });
  if (reason === 'expired') act(() => expireSession());
  await waitFor(() =>
    expect(screen.queryByTitle('Server A iframe')).not.toBeInTheDocument(),
  );
  expect(state.unmounts.get(servers[0].id)).toBe(1);
  expect(state.connect).not.toHaveBeenCalled();
});

it('同账号的新认证代次也不继承旧窗口', async () => {
  const view = mount();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Server A detach' }),
  );
  view.route('/oob/overview');
  beginSession();
  view.identity({ ...user });
  expect(screen.queryByTitle('Server A iframe')).not.toBeInTheDocument();
  expect(state.unmounts.get(servers[0].id)).toBe(1);
});

it.each([
  'logout',
  'identity',
  'permission',
  'expired',
  'generation',
] as const)('%s销毁本宿主的真实关闭确认框，旧回调不能再执行关闭', async (reason) => {
  const view = mount();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Server A detach' }),
  );
  view.route('/oob/overview');
  const staleClose = state.closeRequests.get(servers[0].id);
  fireEvent.click(screen.getByRole('button', { name: 'Server A close' }));
  const dialog = await screen.findByRole('dialog');
  expect(dialog).toHaveTextContent('oob.console.closeTitle');
  expect(state.close).not.toHaveBeenCalled();
  if (reason === 'logout') view.identity(undefined);
  if (reason === 'identity') view.identity({ ...user, userid: '200' });
  if (reason === 'permission') view.identity({ ...user, canUseConsole: false });
  if (reason === 'expired') act(() => expireSession());
  if (reason === 'generation') {
    beginSession();
    view.identity({ ...user });
  }
  await waitFor(() =>
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
  );
  act(() => staleClose?.());
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  expect(state.close).not.toHaveBeenCalled();
});

it('宿主销毁仅关闭自己的确认框，不关闭其他页面的确认框', async () => {
  const view = mount(user, true);
  fireEvent.click(
    await screen.findByRole('button', { name: 'Server A close' }),
  );
  fireEvent.click(
    screen.getByRole('button', { name: 'Open other confirmation' }),
  );
  await waitFor(() => expect(screen.getAllByRole('dialog')).toHaveLength(2));
  view.identity(undefined);
  await waitFor(() => expect(screen.getAllByRole('dialog')).toHaveLength(1));
  expect(screen.getByRole('dialog')).toHaveTextContent('Other confirmation');
  expect(state.close).not.toHaveBeenCalled();
});

it('认证代次已改变但视图尚未重渲染时，旧确认的 onOk 仍不能关闭会话', async () => {
  mount();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Server A close' }),
  );
  await screen.findByRole('dialog');
  beginSession();
  fireEvent.click(screen.getByRole('button', { name: 'OK' }));
  await waitFor(() =>
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
  );
  expect(state.close).not.toHaveBeenCalled();
});

it('提取与最小化窗口仍计入四会话上限，多窗口只允许当前窗口 active', async () => {
  const view = mount();
  for (let index = 0; index < 4; index++) {
    if (index) view.route('/oob/consoles', index);
    const name = servers[index].name;
    fireEvent.click(
      await screen.findByRole('button', { name: `${name} detach` }),
    );
    if (index < 3)
      fireEvent.click(screen.getByRole('button', { name: `${name} minimize` }));
  }
  expect(
    screen
      .getAllByTestId(/^session-/)
      .filter((item) => item.dataset.active === 'true'),
  ).toHaveLength(1);
  view.route('/oob/consoles', 4);
  await screen.findByText('oob.console.sessionLimit');
  expect(state.mounts.size).toBe(4);
  expect(screen.queryByTitle('Server E iframe')).not.toBeInTheDocument();
  expect(state.connect).not.toHaveBeenCalled();
});
