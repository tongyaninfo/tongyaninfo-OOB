import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, waitFor } from '@testing-library/react';
import React from 'react';
import { beforeEach, expect, it, vi } from 'vitest';
import type { CurrentUserDto } from '@/services/oob/models';
import { beginSession, expireSession } from '@/services/oob/session';

const mocks = vi.hoisted(() => ({
  identity: vi.fn(),
  clear: vi.fn(),
  replace: vi.fn(),
}));
vi.mock('@umijs/max', () => ({
  history: {
    location: { pathname: '/oob/users', search: '?page=2', hash: '' },
    replace: mocks.replace,
  },
}));
vi.mock('@/services/oob/auth', () => ({
  currentUser: mocks.identity,
  clearExpiredSession: mocks.clear,
}));

import SessionGuard from './index';

const user: CurrentUserDto = {
  userid: '9007199254740993',
  name: 'Member',
  username: 'member',
  avatar: '',
  access: 'user',
  isAdministrator: false,
  canManageUsers: true,
  canUseConsole: false,
  canManageServers: false,
  canOperatePower: false,
};
beforeEach(() => {
  vi.clearAllMocks();
  beginSession();
  mocks.clear.mockResolvedValue(undefined);
});
it('会话失效立即清除身份和账号缓存，停止连接，并回到保留原路径的登录页', async () => {
  const client = new QueryClient();
  client.setQueryData(['oob', 'users'], ['private data']);
  const changed = vi.fn();
  const view = render(
    <QueryClientProvider client={client}>
      <SessionGuard user={user} onChanged={changed} />
    </QueryClientProvider>,
  );
  act(() => expireSession());
  await waitFor(() => expect(changed).toHaveBeenCalledWith(undefined));
  expect(client.getQueryData(['oob', 'users'])).toBeUndefined();
  expect(mocks.clear).toHaveBeenCalledTimes(1);
  expect(mocks.replace).toHaveBeenCalledWith(
    '/user/login?redirect=%2Foob%2Fusers%3Fpage%3D2',
  );
  view.unmount();
  client.clear();
});
it('离开旧身份后迟到的身份查询不能覆写新账号', async () => {
  let resolve!: (value: unknown) => void;
  mocks.identity.mockImplementation(
    () =>
      new Promise((done) => {
        resolve = done;
      }),
  );
  const client = new QueryClient();
  const changed = vi.fn();
  const view = render(
    <QueryClientProvider client={client}>
      <SessionGuard user={user} onChanged={changed} />
    </QueryClientProvider>,
  );
  fireEvent.focus(window);
  await waitFor(() => expect(mocks.identity).toHaveBeenCalledTimes(1));
  const signal = mocks.identity.mock.calls[0][0] as AbortSignal;
  view.unmount();
  beginSession();
  await act(async () => {
    resolve({ success: true, data: { ...user, name: 'Stale' } });
  });
  expect(signal.aborted).toBe(true);
  expect(changed).not.toHaveBeenCalled();
  client.clear();
});
