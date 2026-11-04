import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import type { HistorySeriesDto } from '@/services/oob/models';

const histories: HistorySeriesDto[] = [
  {
    sensorKey: 'system',
    metricCode: 'power.watts',
    name: 'System power',
    unit: 'W',
    points: [
      {
        time: '2026-10-24T00:00:00Z',
        average: 100,
        minimum: 99,
        maximum: 101,
        count: 1,
      },
    ],
  },
  {
    sensorKey: 'fan:1',
    metricCode: 'fan.rpm',
    name: 'Fan 1',
    unit: 'RPM',
    points: [
      {
        time: '2026-10-24T00:00:00Z',
        average: 1200,
        minimum: 1200,
        maximum: 1200,
        count: 1,
      },
    ],
  },
  {
    sensorKey: 'fan:2',
    metricCode: 'fan.rpm',
    name: 'Fan 2',
    unit: 'RPM',
    points: [
      {
        time: '2026-10-24T00:00:00Z',
        average: 1300,
        minimum: 1300,
        maximum: 1300,
        count: 1,
      },
    ],
  },
  {
    sensorKey: 'fan:1',
    metricCode: 'fan.percent',
    name: 'Fan 1转速百分比',
    unit: '%',
    points: [
      {
        time: '2026-10-24T00:00:00Z',
        average: 99,
        minimum: 99,
        maximum: 99,
        count: 1,
      },
      {
        time: '2026-10-24T00:05:00Z',
        average: null,
        minimum: null,
        maximum: null,
        count: 0,
      },
    ],
  },
  {
    sensorKey: 'fan:unsupported',
    metricCode: 'fan.percent',
    name: 'Unsupported percentage',
    unit: '%',
    points: [
      {
        time: '2026-10-24T00:00:00Z',
        average: null,
        minimum: null,
        maximum: null,
        count: 0,
      },
    ],
  },
  {
    sensorKey: 'fan:2',
    metricCode: 'fan.percent',
    name: 'Fan 2转速百分比',
    unit: '%',
    points: [
      {
        time: '2026-10-24T00:00:00Z',
        average: 24,
        minimum: 24,
        maximum: 24,
        count: 1,
      },
    ],
  },
];
vi.mock('@tanstack/react-query', () => ({
  useQuery: () => ({
    data: { success: true, data: histories, status: 200 },
    isLoading: false,
    isFetching: false,
    error: null,
  }),
}));
vi.mock('@umijs/max', () => ({
  useModel: () => ({ initialState: { settings: { navTheme: 'light' } } }),
}));
vi.mock('@ant-design/plots', () => ({
  Line: ({ data }: { data: unknown[] }) => (
    <div data-testid="history-chart">{JSON.stringify(data)}</div>
  ),
}));
vi.mock('../intl', () => ({
  useOobIntl: () => (key: string) =>
    key === 'oob.monitor.fanDetails.speedPercent' ? '转速百分比（%）' : key,
}));
vi.mock('./ManagedTable', () => ({
  default: ({ tableId }: { tableId: string }) => (
    <div data-testid="history-table" data-table-id={tableId} />
  ),
}));
vi.mock('antd', async () => {
  const actual = await vi.importActual<typeof import('antd')>('antd');
  return {
    ...actual,
    Select: ({
      value,
      onChange,
      options,
    }: {
      value: string[];
      onChange: (value: string[]) => void;
      options: { value: string; label: string }[];
    }) => (
      <div>
        <output data-testid="selected-series">{value.join(',')}</output>
        <output data-testid="series-options">{options.length}</output>
        <output data-testid="series-labels">
          {options.map((option) => option.label).join(' | ')}
        </output>
        <button type="button" onClick={() => onChange([])}>
          clear-series
        </button>
      </div>
    ),
  };
});

import HistoryPanel from './HistoryPanel';

describe('历史传感器展示范围与表格隔离', () => {
  it('初次仅挂载整机功率，清空选择才显式展示全部，完整选项仍保留', () => {
    render(<HistoryPanel id="123" />);
    expect(screen.getByTestId('selected-series')).toHaveTextContent(
      'system|power.watts',
    );
    expect(screen.getByTestId('series-options')).toHaveTextContent('5');
    expect(screen.getByTestId('series-labels')).toHaveTextContent(
      'Fan 2 · 转速百分比（%）',
    );
    expect(screen.getByTestId('series-labels')).toHaveTextContent(
      'Fan 1 · 转速百分比（%）',
    );
    expect(screen.getByTestId('series-labels')).not.toHaveTextContent(
      'PWM占空比',
    );
    expect(screen.getByTestId('series-labels')).not.toHaveTextContent(
      'Unsupported percentage',
    );
    expect(screen.getAllByTestId('history-chart')).toHaveLength(1);
    expect(screen.getAllByTestId('history-table')).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: 'clear-series' }));
    expect(screen.getAllByTestId('history-chart')).toHaveLength(3);
    expect(screen.getAllByTestId('history-table')).toHaveLength(5);
    const keys = screen
      .getAllByTestId('history-table')
      .map((element) => element.getAttribute('data-table-id'));
    expect(new Set(keys).size).toBe(5);
    expect(keys).toContain('oob.history.points.v1:123:fan%3A1:fan.rpm');
    expect(keys).toContain('oob.history.points.v1:123:fan%3A2:fan.rpm');
    expect(keys).toContain('oob.history.points.v1:123:fan%3A2:fan.percent');
    expect(keys).toContain('oob.history.points.v1:123:fan%3A1:fan.percent');
    const percentChart = screen
      .getAllByTestId('history-chart')
      .find((chart) => chart.textContent?.includes('转速百分比'));
    expect(percentChart).toHaveTextContent('"value":99');
    expect(percentChart).toHaveTextContent('"value":24');
    expect(percentChart).toHaveTextContent('"value":null');
    expect(percentChart).toHaveTextContent('Fan 1 · 转速百分比（%）');
    expect(percentChart).toHaveTextContent('Fan 2 · 转速百分比（%）');
    expect(percentChart).not.toHaveTextContent('PWM');
  });
  it('同一个传感器在不同服务器上具有不同稳定表编号', () => {
    const view = render(<HistoryPanel id="123" />);
    const first = screen
      .getByTestId('history-table')
      .getAttribute('data-table-id');
    view.rerender(<HistoryPanel id="456" />);
    const second = screen
      .getByTestId('history-table')
      .getAttribute('data-table-id');
    expect(first).toBe('oob.history.points.v1:123:system:power.watts');
    expect(second).toBe('oob.history.points.v1:456:system:power.watts');
  });
});
