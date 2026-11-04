import {
  focusManager,
  onlineManager,
  QueryClient,
  QueryClientProvider,
} from '@tanstack/react-query';
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';

const history = vi.hoisted(() => vi.fn());
vi.mock('@/services/oob/api', () => ({ getHistory: history }));
vi.mock('@ant-design/plots', () => ({ Line: () => null }));
vi.mock('@umijs/max', () => ({
  useModel: () => ({ initialState: { settings: {} } }),
}));
vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));
vi.mock('./ManagedTable', () => ({ default: () => null }));

import HistoryPanel from './HistoryPanel';

describe('历史页不自动补取', () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
    focusManager.setFocused(undefined);
    onlineManager.setOnline(true);
  });
  it('恢复网络及切回不自动查询，只有人工查询重新读取', async () => {
    history.mockResolvedValue({ success: true, data: [], status: 200 });
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    render(
      <QueryClientProvider client={client}>
        <HistoryPanel id="9007199254740993" />
      </QueryClientProvider>,
    );
    await waitFor(() => expect(history).toHaveBeenCalledTimes(1));
    await act(async () => {
      onlineManager.setOnline(false);
      focusManager.setFocused(false);
      onlineManager.setOnline(true);
      focusManager.setFocused(true);
      await Promise.resolve();
    });
    expect(history).toHaveBeenCalledTimes(1);
    const queryButton = screen.getByRole('button', {
      name: /oob\.history\.query/,
    });
    // 等待首次请求的加载反馈结束，再模拟用户查询，避免并行组件测试时点击被加载按钮忽略。
    await waitFor(() => expect(queryButton).not.toHaveClass('ant-btn-loading'));
    fireEvent.click(queryButton);
    await waitFor(() => expect(history).toHaveBeenCalledTimes(2));
    client.clear();
  });
});
