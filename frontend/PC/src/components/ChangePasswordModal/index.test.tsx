import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import React from 'react';
import { beforeEach, expect, it, vi } from 'vitest';
import type { UserDto } from '@/services/oob/models';

const api = vi.hoisted(() => ({ change: vi.fn(), reset: vi.fn() }));
vi.mock('@/services/oob/auth', () => ({ changePassword: api.change }));
vi.mock('@/services/oob/users', () => ({ resetUserPassword: api.reset }));
vi.mock('@/pages/oob/intl', () => ({ useOobIntl: () => (key: string) => key }));

import ChangePasswordModal from './index';

beforeEach(() => {
  api.change.mockReset();
  api.reset.mockReset();
});
const fill = (label: string, value: string) =>
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
it('自助改密校验确认密码，原密码错误保留弹窗，不误报成功', async () => {
  const close = vi.fn();
  api.change.mockResolvedValue({
    success: false,
    status: 400,
    errorCode: 'INVALID_CURRENT_PASSWORD',
    errorMessage: 'original password incorrect',
    kind: 'business',
    dispatched: true,
  });
  render(
    <App>
      <ChangePasswordModal open onClose={close} />
    </App>,
  );
  fill('oob.password.current', 'old-password');
  fill('oob.password.new', 'new-password');
  fill('oob.password.confirm', 'different-password');
  fireEvent.click(screen.getByRole('button', { name: 'oob.users.save' }));
  await screen.findByText('oob.password.mismatch');
  expect(api.change).not.toHaveBeenCalled();
  fill('oob.password.confirm', 'new-password');
  fireEvent.click(screen.getByRole('button', { name: 'oob.users.save' }));
  await screen.findByText('oob.users.error.INVALID_CURRENT_PASSWORD');
  expect(api.change).toHaveBeenCalledWith({
    currentPassword: 'old-password',
    newPassword: 'new-password',
    confirmPassword: 'new-password',
  });
  expect(close).not.toHaveBeenCalled();
  expect(api.reset).not.toHaveBeenCalled();
});
it('管理员重设不要求原密码，携带字符串ID和版本；未知结果禁用重复提交', async () => {
  const user = {
    id: '9007199254740993',
    version: '9007199254740995',
    displayName: 'Test User',
  } as UserDto;
  api.reset.mockResolvedValue({
    success: false,
    status: 0,
    errorCode: 'NETWORK_ERROR',
    errorMessage: 'offline',
    kind: 'network',
    dispatched: true,
  });
  render(
    <App>
      <ChangePasswordModal open user={user} onClose={vi.fn()} />
    </App>,
  );
  expect(screen.queryByLabelText('oob.password.current')).toBeNull();
  fill('oob.password.new', 'new-password');
  fill('oob.password.confirm', 'new-password');
  fireEvent.click(screen.getByRole('button', { name: 'oob.users.save' }));
  await screen.findByText('oob.password.unknown');
  expect(api.reset).toHaveBeenCalledWith(user.id, {
    version: user.version,
    newPassword: 'new-password',
    confirmPassword: 'new-password',
  });
  await waitFor(() =>
    expect(
      screen.getByRole('button', { name: 'oob.users.save' }),
    ).toBeDisabled(),
  );
  expect(api.change).not.toHaveBeenCalled();
});
