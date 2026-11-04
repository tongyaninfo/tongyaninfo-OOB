import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { ConfigProvider } from 'antd';
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import HistoryRangeControls from './HistoryRangeControls';
import { type HistoryWindow, historyWindow } from './historyTrends';

vi.mock('../intl', async () => {
  const { historyTrendsZhCN } = await import('./historyMessages');
  const messages: Record<string, string> = {
    ...historyTrendsZhCN,
    'oob.history.query': '查询',
  };
  return { useOobIntl: () => (key: string) => messages[key] || key };
});

const now = Date.parse('2026-10-31T02:03:04Z');
const initialWindow = () => historyWindow('24h', now);

function view(
  onQuery: (window: HistoryWindow) => void,
  window = initialWindow(),
  loading = false,
) {
  return (
    <ConfigProvider theme={{ token: { motion: false } }}>
      <HistoryRangeControls
        window={window}
        loading={loading}
        onQuery={onQuery}
      />
    </ConfigProvider>
  );
}

describe('历史查询时间范围控件', () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });
  it.each([
    ['2026-11-01T05:16:57.929Z', '2026-10-31 13:16:57', '2026-11-01 13:16:57'],
    ['2026-03-08T05:16:57.929Z', '2026-03-07 13:16:57', '2026-03-08 13:16:57'],
  ])('浏览器DST切换日从过去一天转自选仍回显正确上海墙钟：%s', (stamp, from, to) => {
    const onQuery = vi.fn();
    render(view(onQuery, historyWindow('24h', Date.parse(stamp))));
    fireEvent.click(screen.getByText('自选范围'));
    expect(screen.getByPlaceholderText('开始时间')).toHaveValue(from);
    expect(screen.getByPlaceholderText('结束时间')).toHaveValue(to);
    expect(onQuery).not.toHaveBeenCalled();
  });

  it('挂载不查询，预设点击立即查询并固定该次结束时间', () => {
    vi.spyOn(Date, 'now').mockReturnValue(now);
    const onQuery = vi.fn();
    render(view(onQuery));
    expect(onQuery).not.toHaveBeenCalled();
    fireEvent.click(screen.getByText('过去一周'));
    expect(onQuery).toHaveBeenLastCalledWith({
      period: '7d',
      from: '2026-10-24T02:03:04.000Z',
      to: '2026-10-31T02:03:04.000Z',
      bucketSeconds: 900,
    });
    fireEvent.click(screen.getByText('过去一个月'));
    expect(onQuery).toHaveBeenLastCalledWith({
      period: 'month',
      from: '2026-09-30T02:03:04.000Z',
      to: '2026-10-31T02:03:04.000Z',
      bucketSeconds: 3600,
    });
    fireEvent.click(screen.getByText('过去一天'));
    expect(onQuery).toHaveBeenLastCalledWith(initialWindow());
  });

  it('切到自选回显上海时间，编辑只改草稿，查询时才提交UTC区间', async () => {
    const onQuery = vi.fn();
    render(view(onQuery));
    fireEvent.click(screen.getByText('自选范围'));
    const start = screen.getByPlaceholderText('开始时间');
    const end = screen.getByPlaceholderText('结束时间');
    expect(start).toHaveValue('2026-10-30 10:03:04');
    expect(end).toHaveValue('2026-10-31 10:03:04');
    expect(screen.getByText(/上海时区/)).toBeInTheDocument();
    expect(onQuery).not.toHaveBeenCalled();
    fireEvent.focus(start);
    fireEvent.change(start, { target: { value: '2026-10-28 09:01:02' } });
    fireEvent.keyDown(start, { key: 'Enter', code: 'Enter' });
    fireEvent.blur(start);
    expect(onQuery).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: '查询' }));
    await waitFor(() =>
      expect(onQuery).toHaveBeenLastCalledWith({
        period: 'custom',
        from: '2026-10-28T01:01:02.000Z',
        to: '2026-10-31T02:03:04.000Z',
        bucketSeconds: 900,
      }),
    );
  });

  it('范围超过366天时提示且不发送查询', () => {
    const onQuery = vi.fn();
    render(
      view(onQuery, {
        ...initialWindow(),
        period: 'custom',
        from: '2025-01-01T00:00:00Z',
        to: '2026-10-31T00:00:00Z',
      }),
    );
    fireEvent.click(screen.getByRole('button', { name: '查询' }));
    expect(onQuery).not.toHaveBeenCalled();
    expect(screen.getByRole('alert')).toHaveTextContent('范围不超过366天');
  });

  it('手输无效日期不能悄悄沿用原范围，清空范围也不能查询', () => {
    const onQuery = vi.fn();
    render(view(onQuery));
    fireEvent.click(screen.getByText('自选范围'));
    const start = screen.getByPlaceholderText('开始时间');
    fireEvent.focus(start);
    fireEvent.change(start, { target: { value: '2026-02-30 10:00:00' } });
    fireEvent.blur(start);
    fireEvent.click(screen.getByRole('button', { name: '查询' }));
    expect(onQuery).not.toHaveBeenCalled();
    expect(screen.getByRole('alert')).toHaveTextContent('请选择有效');
    fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
    fireEvent.click(screen.getByRole('button', { name: '查询' }));
    expect(onQuery).not.toHaveBeenCalled();
    expect(screen.getByRole('alert')).toHaveTextContent('请选择有效');
  });

  it('开始和结束同刻时提示且不发送查询', () => {
    const onQuery = vi.fn();
    render(
      view(onQuery, {
        ...initialWindow(),
        period: 'custom',
        from: '2026-10-31T00:00:00Z',
        to: '2026-10-31T00:00:00Z',
      }),
    );
    fireEvent.click(screen.getByRole('button', { name: '查询' }));
    expect(onQuery).not.toHaveBeenCalled();
    expect(screen.getByRole('alert')).toHaveTextContent(
      '结束时间须晚于开始时间',
    );
  });

  it('浏览器夏令时跳时不能改变有效的上海墙钟时间', () => {
    const onQuery = vi.fn();
    render(
      view(onQuery, {
        ...initialWindow(),
        period: 'custom',
        from: '2026-03-07T18:30:00Z',
        to: '2026-03-08T18:30:00Z',
      }),
    );
    expect(screen.getByPlaceholderText('开始时间')).toHaveValue(
      '2026-03-08 02:30:00',
    );
    fireEvent.click(screen.getByRole('button', { name: '查询' }));
    expect(onQuery).toHaveBeenCalledWith({
      period: 'custom',
      from: '2026-03-07T18:30:00.000Z',
      to: '2026-03-08T18:30:00.000Z',
      bucketSeconds: 300,
    });
  });
});
