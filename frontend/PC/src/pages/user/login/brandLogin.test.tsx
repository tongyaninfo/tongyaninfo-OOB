import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { App } from 'antd';
import React from 'react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';

const api = vi.hoisted(() => ({
  login: vi.fn(),
  user: vi.fn(),
  setState: vi.fn(),
}));
vi.mock('@/services/oob/auth', () => ({ login: api.login }));
vi.mock('@/pages/oob/intl', () => ({ useOobIntl: () => (key: string) => key }));
vi.mock('@/components', () => ({ Footer: () => <footer>通研技术</footer> }));
vi.mock('@umijs/max', () => ({
  Helmet: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  SelectLang: () => <button type="button">Language</button>,
  useModel: () => ({
    initialState: { fetchUserInfo: api.user },
    setInitialState: api.setState,
  }),
}));

import Login from './index';

beforeEach(() => {
  vi.clearAllMocks();
  window.location.href = 'http://localhost:3000/user/login';
  api.user.mockResolvedValue({
    userid: '9007199254740993',
    name: 'Sample user',
  });
});
afterEach(cleanup);

const mount = () =>
  render(
    <App>
      <Login />
    </App>,
  );
const submit = () => {
  fireEvent.change(screen.getByLabelText('oob.brand.login.account'), {
    target: { value: 'sample-user' },
  });
  fireEvent.change(screen.getByLabelText('oob.brand.login.password'), {
    target: { value: 'sample-password' },
  });
  fireEvent.click(
    screen.getByRole('button', { name: 'oob.brand.login.submit' }),
  );
};

it('账号密码必填，移除未接入入口；认证失败保留表单且不恢复身份', async () => {
  api.login.mockResolvedValue({ status: 'error', type: 'account' });
  mount();
  fireEvent.click(
    screen.getByRole('button', { name: 'oob.brand.login.submit' }),
  );
  await screen.findByText('oob.brand.login.accountRequired');
  expect(api.login).not.toHaveBeenCalled();
  expect(screen.queryByRole('tab')).not.toBeInTheDocument();
  submit();
  await screen.findByText('oob.brand.login.invalid');
  expect(api.login).toHaveBeenCalledWith({
    username: 'sample-user',
    password: 'sample-password',
    type: 'account',
  });
  expect(api.user).not.toHaveBeenCalled();
  expect(window.location.pathname).toBe('/user/login');
});

it.each([
  [
    '/oob/servers?keyword=sample#details',
    '/oob/servers?keyword=sample#details',
  ],
  ['//external.invalid/path', '/'],
  ['/\\external.invalid/path', '/'],
  ['https://external.invalid/path', '/'],
])('登录成功后安全处理回跳 %s', async (redirect, expected) => {
  window.location.href = `http://localhost:3000/user/login?redirect=${encodeURIComponent(redirect)}`;
  api.login.mockResolvedValue({ status: 'ok', type: 'account' });
  mount();
  submit();
  await waitFor(() => expect(api.user).toHaveBeenCalledTimes(1));
  await waitFor(() =>
    expect(
      `${window.location.pathname}${window.location.search}${window.location.hash}`,
    ).toBe(expected),
  );
  expect(window.location.origin).toBe('http://localhost:3000');
});
