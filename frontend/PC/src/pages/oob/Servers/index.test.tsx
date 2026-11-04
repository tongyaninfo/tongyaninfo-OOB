import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { telemetryFixture } from '../../../../tests/oobFixtures';

const servers = vi.fn();
const exported = vi.fn();
const imported = vi.fn();
const persist = vi.fn();
const permission = vi.hoisted(() => ({
  canAdmin: true,
  canManageServers: true,
}));
vi.mock('@/services/oob/api', () => ({
  getServers: (...args: unknown[]) => servers(...args),
  exportServerConfiguration: (...args: unknown[]) => exported(...args),
  importServerConfiguration: (...args: unknown[]) => imported(...args),
}));
vi.mock('@/services/oob/preferences', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/services/oob/preferences')>()),
  getTablePreference: () => ({ size: 'middle', columnsState: {} }),
  updateTablePreference: (...args: unknown[]) => persist(...args),
}));
vi.mock('@umijs/max', () => ({
  useAccess: () => ({
    canAdmin: permission.canAdmin,
    canManageServers: permission.canManageServers,
    canOperatePower: false,
    canUseConsole: true,
    canManageUsers: true,
  }),
  Link: ({ to, children }: { to: string; children: React.ReactNode }) => (
    <a href={to}>{children}</a>
  ),
}));
vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));
vi.mock('../components/ServerEditor', () => ({ default: () => null }));
vi.mock('../components/ServerStatus', () => ({ default: () => null }));
vi.mock('@ant-design/pro-components', async () => {
  const actual = await vi.importActual<
    typeof import('@ant-design/pro-components')
  >('@ant-design/pro-components');
  return {
    ...actual,
    PageContainer: ({ children }: { children: React.ReactNode }) => (
      <div>{children}</div>
    ),
  };
});

import Servers from './index';

describe('真实 ProTable 分页与设置生命周期', () => {
  afterEach(() => vi.restoreAllMocks());

  it('请求总数更新、分页与重新挂载不在 ProTable 渲染阶段更新父状态', async () => {
    const errors = vi.spyOn(console, 'error');
    servers.mockImplementation(
      async (params: { current?: number; pageSize?: number }) => ({
        success: true,
        status: 200,
        data: {
          items: [],
          total: 21,
          current: params.current || 1,
          pageSize: params.pageSize || 10,
        },
      }),
    );
    const mount = () =>
      render(
        <App>
          <Servers />
        </App>,
      );
    let view = mount();
    await waitFor(() => expect(servers).toHaveBeenCalled());
    expect(persist).not.toHaveBeenCalled();
    const next = await waitFor(() => {
      const button = view.container.querySelector<HTMLButtonElement>(
        '.ant-pagination-next button',
      );
      if (!button) throw new Error('Pagination is not ready');
      expect(button.closest('li')).not.toHaveClass('ant-pagination-disabled');
      return button;
    });
    fireEvent.click(next);
    await waitFor(() =>
      expect(servers.mock.calls.some(([params]) => params.current === 2)).toBe(
        true,
      ),
    );
    expect(screen.getByText('oob.servers')).toBeInTheDocument();
    // 密度仍走真实工具栏事件，验证修复没有移除账号设置持久化能力。
    fireEvent.click(screen.getByRole('img', { name: 'column-height' }));
    fireEvent.click(await screen.findByText(/^(紧凑|Compact)$/));
    await waitFor(() =>
      expect(
        persist.mock.calls.some(([, value]) => value.size === 'small'),
      ).toBe(true),
    );
    // 列显隐同样经过真实 ProTable 设置控件，初始化去重不能吞掉用户勾选。
    fireEvent.click(screen.getByRole('img', { name: 'setting' }));
    const columnsCheckbox = await waitFor(() => {
      const input = document.querySelector<HTMLInputElement>(
        '.ant-pro-table-column-setting-title input[type="checkbox"]',
      );
      expect(input).not.toBeNull();
      return input as HTMLInputElement;
    });
    fireEvent.click(columnsCheckbox);
    await waitFor(() =>
      expect(
        persist.mock.calls.some(([, value]) =>
          Object.values(value.columnsState).some(
            (column) => (column as { show?: boolean }).show === false,
          ),
        ),
      ).toBe(true),
    );

    view.unmount();
    persist.mockClear();
    view = mount();
    await waitFor(() => expect(servers.mock.calls.length).toBeGreaterThan(2));
    expect(persist).not.toHaveBeenCalled();
    expect(
      errors.mock.calls
        .map((args) => args.map(String).join(' '))
        .some((value) => value.includes('Cannot update a component')),
    ).toBe(false);
    view.unmount();
  });
  it.each([
    [false, true],
    [true, false],
    [false, false],
  ])('新增编辑由服务器权限决定：admin=%s，服务器=%s', async (admin, manageServers) => {
    permission.canAdmin = admin;
    permission.canManageServers = manageServers;
    servers.mockResolvedValue({
      success: true,
      status: 200,
      data: {
        items: [telemetryFixture('2026-10-25T00:00:00Z', []).server],
        current: 1,
        pageSize: 10,
        total: 1,
      },
    });
    const view = render(
      <App>
        <Servers />
      </App>,
    );
    await screen.findByRole('link', { name: 'oob.details' });
    expect(Boolean(screen.queryByRole('button', { name: /oob.add/ }))).toBe(
      manageServers,
    );
    expect(Boolean(screen.queryByRole('link', { name: 'oob.edit' }))).toBe(
      manageServers,
    );
    expect(
      Boolean(
        screen.queryByRole('button', { name: /oob.configuration.export/ }),
      ),
    ).toBe(manageServers);
    expect(
      Boolean(
        screen.queryByRole('button', { name: /oob.configuration.import/ }),
      ),
    ).toBe(manageServers);
    view.unmount();
    permission.canAdmin = true;
    permission.canManageServers = true;
  });

  it('打开导入或导出弹窗不发送配置请求', async () => {
    exported.mockClear();
    imported.mockClear();
    servers.mockResolvedValue({
      success: true,
      status: 200,
      data: { items: [], current: 1, pageSize: 10, total: 0 },
    });
    render(
      <App>
        <Servers />
      </App>,
    );
    fireEvent.click(
      screen.getByRole('button', { name: /oob.configuration.export/ }),
    );
    await screen.findByRole('dialog', { name: 'oob.configuration.export' });
    expect(exported).not.toHaveBeenCalled();
    expect(imported).not.toHaveBeenCalled();
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.configuration.close' }),
    );
    fireEvent.click(
      screen.getByRole('button', { name: /oob.configuration.import/ }),
    );
    await screen.findByRole('dialog', { name: 'oob.configuration.import' });
    expect(exported).not.toHaveBeenCalled();
    expect(imported).not.toHaveBeenCalled();
  });
});
