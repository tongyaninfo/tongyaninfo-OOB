import type { Settings } from '@ant-design/pro-components';
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { App } from 'antd';
import React, { StrictMode, useState } from 'react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import {
  clearPreferences,
  restorePreferences,
} from '@/services/oob/preferences';
import defaults from '../../../config/defaultSettings';

const api = vi.hoisted(() => ({ get: vi.fn(), save: vi.fn() }));
vi.mock('@/services/oob/api', () => ({
  getPreferences: api.get,
  savePreferences: api.save,
}));

import AppearanceSettings from './index';
import { resolveTheme, type ThemeMode } from './theme';

vi.mock('@umijs/max', () => ({
  useIntl: () => ({
    formatMessage: ({ defaultMessage }: { defaultMessage: string }) =>
      defaultMessage,
  }),
}));

const pause = () =>
  act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 650));
  });
// 锁版 isBrowser 通过大写 TEST 启用真实浏览器初始化分支。
beforeEach(() => {
  vi.stubEnv('NODE_ENV', 'TEST');
});
afterEach(() => {
  clearPreferences();
  vi.clearAllMocks();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});

it('真实SettingDrawer恢复深色及打开抽屉不保存；用户改主题保存一次，重复点当前主题不重写', async () => {
  api.get.mockResolvedValue({
    success: true,
    data: {
      layoutSettings: { navTheme: 'realDark' },
      tables: { retained: { size: 'small' } },
    },
    status: 200,
  });
  api.save.mockResolvedValue({ success: true, data: true, status: 200 });
  await restorePreferences();
  const changes = vi.fn();
  function Fixture() {
    const [settings, setSettings] = useState<Partial<Settings>>({
      ...defaults,
      navTheme: 'realDark',
    } as Partial<Settings>);
    const [open, setOpen] = useState(false);
    return (
      <App>
        <button type="button" onClick={() => setOpen(true)}>
          open appearance
        </button>
        <AppearanceSettings
          settings={settings}
          open={open}
          onOpenChange={setOpen}
          onChange={(next) => {
            changes(next);
            setSettings(next);
          }}
        />
      </App>
    );
  }
  render(<Fixture />);
  // 使用锁版真实组件的挂载回调复现原缺陷，不能用不触发初始化的替身掩盖自动保存。
  await waitFor(() => expect(changes).toHaveBeenCalled());
  await pause();
  expect(api.save).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'open appearance' }));
  const light = await screen.findByRole('radio', { name: /浅色/ });
  await pause();
  expect(api.save).not.toHaveBeenCalled();
  fireEvent.click(light);
  await waitFor(() => expect(api.save).toHaveBeenCalledTimes(1));
  expect(api.save).toHaveBeenCalledWith(
    expect.objectContaining({
      layoutSettings: expect.objectContaining({ navTheme: 'light' }),
      tables: { retained: { size: 'small' } },
    }),
  );
  fireEvent.click(light);
  await pause();
  expect(api.save).toHaveBeenCalledTimes(1);
});

it('自动主题恢复及系统深浅色变化只更新显示；手动选择才保存账号意图，退出自动后释放监听', async () => {
  let dark = false;
  const listeners = new Set<(event: MediaQueryListEvent) => void>();
  const originalMatchMedia = window.matchMedia.bind(window);
  vi.spyOn(window, 'matchMedia').mockImplementation((query) =>
    query === '(prefers-color-scheme: dark)'
      ? ({
          get matches() {
            return dark;
          },
          addEventListener: (
            _: string,
            listener: (event: MediaQueryListEvent) => void,
          ) => listeners.add(listener),
          removeEventListener: (
            _: string,
            listener: (event: MediaQueryListEvent) => void,
          ) => listeners.delete(listener),
        } as unknown as MediaQueryList)
      : originalMatchMedia(query),
  );
  const systemTheme = (value: boolean) =>
    act(() => {
      dark = value;
      for (const listener of listeners)
        listener({ matches: value } as MediaQueryListEvent);
    });
  api.get.mockResolvedValue({
    success: true,
    data: { layoutSettings: { navTheme: 'auto' }, tables: {} },
    status: 200,
  });
  api.save.mockResolvedValue({ success: true, data: true, status: 200 });
  await restorePreferences();
  function Fixture() {
    const [mode, setMode] = useState<ThemeMode>('auto');
    const [settings, setSettings] = useState<Partial<Settings>>({
      ...defaults,
      navTheme: resolveTheme('auto'),
    } as Partial<Settings>);
    return (
      <App>
        <output>{`${mode}:${settings.navTheme}`}</output>
        <AppearanceSettings
          settings={settings}
          themeMode={mode}
          open
          onOpenChange={vi.fn()}
          onChange={(next, nextMode) => {
            setSettings(next);
            setMode(nextMode);
          }}
        />
      </App>
    );
  }
  const fixture = render(<Fixture />);
  await screen.findByRole('radio', { name: /自动/ });
  await pause();
  expect(screen.getByRole('status')).toHaveTextContent('auto:light');
  systemTheme(true);
  expect(screen.getByRole('status')).toHaveTextContent('auto:realDark');
  systemTheme(false);
  await pause();
  expect(screen.getByRole('status')).toHaveTextContent('auto:light');
  expect(api.save).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('radio', { name: /浅色/ }));
  await waitFor(() => expect(api.save).toHaveBeenCalledTimes(1));
  expect(listeners.size).toBe(0);
  systemTheme(true);
  expect(screen.getByRole('status')).toHaveTextContent('light:light');
  fireEvent.click(screen.getByRole('radio', { name: /自动/ }));
  await waitFor(() => expect(api.save).toHaveBeenCalledTimes(2));
  expect(api.save).toHaveBeenLastCalledWith(
    expect.objectContaining({
      layoutSettings: expect.objectContaining({ navTheme: 'auto' }),
    }),
  );
  expect(screen.getByRole('status')).toHaveTextContent('auto:realDark');
  // 修改其他外观仍保留自动模式，不能把当时解析的深色写回为固定深色。
  const color = document.querySelector(
    '.ant-pro-setting-drawer-theme-color-block',
  );
  expect(color).not.toBeNull();
  fireEvent.click(color as HTMLElement);
  await waitFor(() => expect(api.save).toHaveBeenCalledTimes(3));
  expect(api.save).toHaveBeenLastCalledWith(
    expect.objectContaining({
      layoutSettings: expect.objectContaining({
        navTheme: 'auto',
        colorPrimary: '#1677FF',
      }),
    }),
  );
  fixture.unmount();
  expect(listeners.size).toBe(0);
});

it('首次即打开及StrictMode重复初始化仍不写入默认外观', async () => {
  api.get.mockResolvedValue({ success: true, data: {}, status: 200 });
  await restorePreferences();
  const changes = vi.fn();
  function Fixture() {
    const [settings, setSettings] = useState<Partial<Settings>>(
      defaults as Partial<Settings>,
    );
    return (
      <App>
        <AppearanceSettings
          settings={settings}
          open
          onOpenChange={vi.fn()}
          onChange={(next) => {
            changes(next);
            setSettings(next);
          }}
        />
      </App>
    );
  }
  render(
    <StrictMode>
      <Fixture />
    </StrictMode>,
  );
  await waitFor(() => expect(changes).toHaveBeenCalled());
  await pause();
  expect(api.save).not.toHaveBeenCalled();
});
