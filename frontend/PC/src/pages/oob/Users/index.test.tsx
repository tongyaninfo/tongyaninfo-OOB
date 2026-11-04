import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import { App } from 'antd';
import React from 'react';
import { beforeEach, expect, it, vi } from 'vitest';
import type { UserDto } from '@/services/oob/models';

const api = vi.hoisted(() => ({
  list: vi.fn(),
  save: vi.fn(),
  remove: vi.fn(),
  reset: vi.fn(),
  identity: vi.fn(),
  access: { canAdmin: false, canManageUsers: true, canUseConsole: false },
}));
vi.mock('@/services/oob/users', () => ({
  getUsers: api.list,
  saveUser: api.save,
  deleteUser: api.remove,
  resetUserPassword: api.reset,
}));
vi.mock('@/services/oob/auth', () => ({
  currentUser: api.identity,
  changePassword: vi.fn(),
}));
vi.mock('@umijs/max', () => ({
  useAccess: () => api.access,
  useModel: () => ({
    initialState: { currentUser: { userid: '9007199254740997' } },
    setInitialState: vi.fn(),
  }),
}));
vi.mock('@ant-design/pro-components', () => ({
  PageContainer: ({ children }: { children: React.ReactNode }) => (
    <div>{children}</div>
  ),
}));
vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));
vi.mock('@/services/oob/preferences', () => ({
  getTablePreference: () => undefined,
  updateTablePreference: vi.fn(),
}));

import Users from './index';
import UserEditor from './UserEditor';

const user: UserDto = {
  id: '9007199254740993',
  version: '9007199254740995',
  username: 'member',
  displayName: 'Member',
  enabled: true,
  deletedAt: null,
  isAdministrator: false,
  canManageUsers: false,
  canUseConsole: true,
  canManageServers: true,
  canOperatePower: true,
  createdAt: '2026-11-01T00:00:00Z',
  updatedAt: '2026-11-01T00:00:00Z',
};
beforeEach(() => {
  api.list.mockReset();
  api.save.mockReset();
  api.remove.mockReset();
  api.access.canAdmin = false;
  api.access.canManageUsers = true;
  api.list.mockImplementation(async (params) => ({
    success: true,
    status: 200,
    data: {
      items: [
        user,
        {
          ...user,
          id: '9007199254740994',
          username: 'admin',
          displayName: 'Admin',
          isAdministrator: true,
        },
      ],
      total: 41,
      current: params.current,
      pageSize: params.pageSize,
    },
  }));
});
it('普通用户管理员无法编辑系统管理员，筛选排序和键盘分页提交服务端全集条件', async () => {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const view = render(
    <App>
      <QueryClientProvider client={client}>
        <Users />
      </QueryClientProvider>
    </App>,
  );
  const adminRow = (await screen.findByText('Admin')).closest('tr');
  expect(adminRow).not.toBeNull();
  for (const button of within(adminRow as HTMLElement).getAllByRole('button'))
    expect(button).toBeDisabled();
  const section = view.container.querySelector('section[tabindex="0"]');
  fireEvent.keyDown(section as HTMLElement, { key: 'ArrowRight' });
  await waitFor(() =>
    expect(api.list).toHaveBeenLastCalledWith(
      expect.objectContaining({ current: 2, pageSize: 20 }),
      expect.any(AbortSignal),
    ),
  );
  fireEvent.click(screen.getByRole('columnheader', { name: /oob.users.name/ }));
  await waitFor(() =>
    expect(api.list).toHaveBeenLastCalledWith(
      expect.objectContaining({
        current: 1,
        sortField: 'displayName',
        sortOrder: 'ascend',
      }),
      expect.any(AbortSignal),
    ),
  );
  fireEvent.change(screen.getByLabelText('oob.users.search'), {
    target: { value: 'second' },
  });
  fireEvent.keyDown(screen.getByLabelText('oob.users.search'), {
    key: 'Enter',
    code: 'Enter',
  });
  await waitFor(() =>
    expect(api.list).toHaveBeenLastCalledWith(
      expect.objectContaining({ current: 1, keyword: 'second' }),
      expect.any(AbortSignal),
    ),
  );
  client.clear();
});
it('新增默认不授予权限，明确勾选控制台并保留后端重名错误', async () => {
  api.save.mockResolvedValue({
    success: false,
    status: 409,
    errorCode: 'ACCOUNT_EXISTS',
    errorMessage: 'duplicate account',
    kind: 'business',
    dispatched: true,
  });
  render(
    <App>
      <UserEditor open onClose={vi.fn()} onSaved={vi.fn()} />
    </App>,
  );
  expect(
    screen.getByRole('checkbox', { name: 'oob.users.manage' }),
  ).not.toBeChecked();
  expect(
    screen.getByRole('checkbox', { name: 'oob.users.console' }),
  ).not.toBeChecked();
  expect(
    screen.getByRole('checkbox', { name: 'oob.users.servers' }),
  ).not.toBeChecked();
  expect(
    screen.getByRole('checkbox', { name: 'oob.users.power' }),
  ).not.toBeChecked();
  fireEvent.change(screen.getByLabelText('oob.users.name'), {
    target: { value: ' New User ' },
  });
  fireEvent.change(screen.getByLabelText('oob.users.username'), {
    target: { value: ' Example ' },
  });
  fireEvent.change(screen.getByLabelText('oob.password.initial'), {
    target: { value: 'new-password' },
  });
  fireEvent.click(screen.getByRole('checkbox', { name: 'oob.users.console' }));
  fireEvent.click(screen.getByRole('button', { name: 'oob.users.save' }));
  await screen.findByText('oob.users.error.ACCOUNT_EXISTS');
  expect(api.save).toHaveBeenCalledWith(
    {
      displayName: 'New User',
      username: 'Example',
      password: 'new-password',
      enabled: true,
      canManageUsers: false,
      canUseConsole: true,
      canManageServers: false,
      canOperatePower: false,
    },
    undefined,
  );
});
it('编辑不发送密码，使用读取版本；并发冲突后禁止覆盖重试', async () => {
  api.save.mockResolvedValue({
    success: false,
    status: 409,
    errorCode: 'USER_VERSION_CONFLICT',
    errorMessage: 'record changed; refresh',
    kind: 'business',
    dispatched: true,
  });
  render(
    <App>
      <UserEditor open user={user} onClose={vi.fn()} onSaved={vi.fn()} />
    </App>,
  );
  expect(screen.queryByLabelText('oob.password.initial')).toBeNull();
  expect(
    screen.getByRole('checkbox', { name: 'oob.users.servers' }),
  ).toBeChecked();
  expect(
    screen.getByRole('checkbox', { name: 'oob.users.power' }),
  ).toBeChecked();
  fireEvent.click(screen.getByRole('checkbox', { name: 'oob.users.power' }));
  fireEvent.change(screen.getByLabelText('oob.users.name'), {
    target: { value: 'Renamed' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'oob.users.save' }));
  await screen.findByText('oob.users.error.USER_VERSION_CONFLICT');
  expect(api.save).toHaveBeenCalledWith(
    expect.objectContaining({
      displayName: 'Renamed',
      version: '9007199254740995',
      password: undefined,
      canManageServers: true,
      canOperatePower: false,
    }),
    '9007199254740993',
  );
  expect(screen.getByRole('button', { name: 'oob.users.save' })).toBeDisabled();
});

it('启停账号保留四项权限，不因更新状态清除服务器和电源授权', async () => {
  api.save.mockResolvedValue({
    success: true,
    data: { ...user, enabled: false },
    status: 200,
  });
  api.identity.mockResolvedValue({
    success: true,
    data: { userid: '9007199254740997', canManageUsers: true },
    status: 200,
  });
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const view = render(
    <App>
      <QueryClientProvider client={client}>
        <Users />
      </QueryClientProvider>
    </App>,
  );
  const row = (await screen.findByText('Member')).closest('tr') as HTMLElement;
  expect(within(row).getByText('oob.users.servers')).toBeInTheDocument();
  expect(within(row).getByText('oob.users.power')).toBeInTheDocument();
  expect(within(row).queryByText('oob.users.readonly')).toBeNull();
  fireEvent.click(
    within(row).getByRole('button', { name: 'oob.users.disabled' }),
  );
  fireEvent.click(
    within(await screen.findByRole('dialog')).getByRole('button', {
      name: 'oob.users.save',
    }),
  );
  await waitFor(() =>
    expect(api.save).toHaveBeenCalledWith(
      {
        username: user.username,
        displayName: user.displayName,
        enabled: false,
        canManageUsers: false,
        canUseConsole: true,
        canManageServers: true,
        canOperatePower: true,
        version: user.version,
      },
      user.id,
    ),
  );
  view.unmount();
  client.clear();
});
