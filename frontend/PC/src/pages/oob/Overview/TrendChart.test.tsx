import { G2 } from '@ant-design/plots';
import {
  cleanup,
  fireEvent,
  render,
  screen,
  within,
} from '@testing-library/react';
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { HistorySeriesDto } from '@/services/oob/models';
import { sensorIdentity } from './metrics';

vi.mock('@umijs/max', () => ({
  useModel: () => ({ initialState: { settings: { navTheme: 'light' } } }),
}));
vi.mock('../intl', () => ({
  useOobIntl: () => (key: string, values?: { name?: string }) =>
    ({
      'oob.monitor.recent': 'Last 15 minutes',
      'oob.monitor.legend': 'Legend',
      'oob.monitor.legend.hide': `Hide ${values?.name}`,
      'oob.monitor.legend.show': `Show ${values?.name}`,
      'oob.monitor.legend.allHidden': 'All series hidden',
    })[key] || key,
}));
vi.mock('@ant-design/plots', async () => {
  const actual =
    await vi.importActual<typeof import('@ant-design/plots')>(
      '@ant-design/plots',
    );
  return {
    G2: actual.G2,
    Line: ({
      data,
      scale,
      connectNulls,
      axis,
      tooltip,
      style,
    }: {
      data: Array<{ time: number; value: number | null; dashed: boolean }>;
      scale: unknown;
      connectNulls: boolean;
      axis: {
        x: { labelFormatter: (value: number) => string };
        y: { labelFormatter?: (value: number | string) => string };
      };
      tooltip: {
        title: (datum: { time: number }) => string;
        items: { valueFormatter?: (value: number | null) => string }[];
      };
      style: { lineDash: (data: { dashed: boolean }[]) => number[] };
    }) => (
      <output
        data-testid="trend-canvas"
        data-scale={JSON.stringify(scale)}
        data-connect-nulls={String(connectNulls)}
        data-line-dashes={JSON.stringify(
          data.map((point) => style.lineDash([point])),
        )}
        data-axis-label={axis.x.labelFormatter(data[0].time)}
        data-point-title={tooltip.title(data[0])}
        data-value-label={
          axis.y.labelFormatter?.(String(data[0].value)) ??
          String(data[0].value)
        }
        data-tooltip-value={
          tooltip.items[0].valueFormatter?.(data[0].value) ??
          String(data[0].value)
        }
      >
        {JSON.stringify(data)}
      </output>
    ),
  };
});

import TrendChart from './TrendChart';

function series(
  sensorKey: string,
  name: string,
  values: Array<number | null> = [40, null, 42],
): HistorySeriesDto {
  return {
    sensorKey,
    name,
    metricCode: 'temperature.celsius',
    unit: '°C',
    points: values.map((average, index) => ({
      time: new Date(
        Date.parse('2026-10-25T00:00:00Z') + index * 30000,
      ).toISOString(),
      average,
      minimum: average,
      maximum: average,
      count: average === null ? 0 : 1,
    })),
  };
}
const firstName = 'Intel(R) Xeon(R) CPU E5-2696 v2 @ 2.50GHz (CPU 1)';
const secondName = 'Intel(R) Xeon(R) CPU E5-2696 v2 @ 2.50GHz (CPU 2)';
const initial = [
  series('temperature:2', firstName),
  series('temperature:3', secondName, [45]),
];
function points(): Array<{ series: string; value: number | null }> {
  return JSON.parse(screen.getByTestId('trend-canvas').textContent || '[]');
}
function legend() {
  return screen.getByRole('list', { name: 'Legend' });
}
function button(name: string) {
  return within(legend()).getByRole('button', { name });
}
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('本地趋势图例显示状态与用户交互', () => {
  it('进气按稳定标识显示虚线，排气实线，图例与曲线同步且保留真实缺口', () => {
    const inlet = series('inlet', 'Inlet Ambient');
    const exhaust = series('exhaust', 'Sys Exhaust', [45]);
    render(
      <TrendChart
        scopeKey="airflow"
        series={[inlet, exhaust]}
        unit="°C"
        dashedSeries={[sensorIdentity(inlet)]}
      />,
    );
    expect(screen.getByTestId('trend-canvas')).toHaveAttribute(
      'data-line-dashes',
      JSON.stringify([[6, 4], [6, 4], [6, 4], []]),
    );
    expect(
      button('Inlet Ambient').querySelector('[aria-hidden=true]'),
    ).toHaveStyle({ borderTopStyle: 'dashed' });
    expect(
      button('Sys Exhaust').querySelector('[aria-hidden=true]'),
    ).toHaveStyle({ borderTopStyle: 'solid' });
    expect(points()[1].value).toBeNull();
    fireEvent.click(button('Inlet Ambient'));
    expect(screen.getByTestId('trend-canvas')).toHaveAttribute(
      'data-line-dashes',
      '[[]]',
    );
  });

  it('坐标与悬浮计算值最多两位小数，曲线仍保留未舍入的计算值', () => {
    const value = 123.456789;
    render(
      <TrendChart
        scopeKey="precision"
        series={[series('power', 'Average', [value])]}
        unit="W"
      />,
    );
    const chart = screen.getByTestId('trend-canvas');
    expect(chart).toHaveAttribute('data-value-label', '123.46');
    expect(chart).toHaveAttribute('data-tooltip-value', '123.46');
    expect(points()[0].value).toBe(value);
  });

  it('点击完整纵向图例只隐藏对应曲线，真实颜色与剩余曲线保持一致且不发请求', () => {
    const fetch = vi.spyOn(globalThis, 'fetch');
    render(
      <TrendChart
        scopeKey="server-a:temperatures"
        series={initial}
        unit="°C"
      />,
    );
    expect(
      within(legend())
        .getAllByRole('listitem')
        .map((item) => item.textContent),
    ).toEqual([firstName, secondName]);
    const originalScale = JSON.parse(
      screen.getByTestId('trend-canvas').getAttribute('data-scale') || '{}',
    ).color;
    const ordinal = G2.stdlib()['scale.ordinal'](originalScale);
    expect(button(secondName).querySelector('[aria-hidden=true]')).toHaveStyle({
      borderTopColor: ordinal.map(secondName),
    });
    expect(
      points().some(
        (point) => point.series === firstName && point.value === null,
      ),
    ).toBe(true);
    fireEvent.click(button(firstName));
    expect(button(firstName)).toHaveAttribute('aria-pressed', 'false');
    expect(button(firstName)).not.toBeDisabled();
    expect(points().every((point) => point.series === secondName)).toBe(true);
    expect(screen.getByTestId('trend-canvas')).toHaveAttribute(
      'data-scale',
      JSON.stringify({ x: { type: 'time' }, color: originalScale }),
    );
    expect(button(secondName).querySelector('[aria-hidden=true]')).toHaveStyle({
      borderTopColor: ordinal.map(secondName),
    });
    expect(screen.getByTestId('trend-canvas')).toHaveAttribute(
      'data-connect-nulls',
      'false',
    );
    expect(fetch).not.toHaveBeenCalled();
  });
  it('Enter和Space均可切换，同一按键长按不重复翻转且焦点保留', () => {
    render(
      <TrendChart
        scopeKey="server-a:temperatures"
        series={initial}
        unit="°C"
      />,
    );
    const control = button(firstName);
    control.focus();
    fireEvent.keyDown(control, { key: 'Enter' });
    fireEvent.keyUp(control, { key: 'Enter' });
    expect(control).toHaveAttribute('aria-pressed', 'false');
    fireEvent.keyDown(control, { key: 'Enter', repeat: true });
    expect(control).toHaveAttribute('aria-pressed', 'false');
    fireEvent.keyDown(control, { key: ' ' });
    fireEvent.keyUp(control, { key: ' ' });
    expect(control).toHaveAttribute('aria-pressed', 'true');
    expect(control).toHaveFocus();
    expect(points().some((point) => point.series === firstName)).toBe(true);
  });
  it('全部关闭允许空图，原图例仍可恢复单条曲线', () => {
    render(
      <TrendChart
        scopeKey="server-a:temperatures"
        series={initial}
        unit="°C"
      />,
    );
    fireEvent.click(button(firstName));
    fireEvent.click(button(secondName));
    expect(screen.queryByTestId('trend-canvas')).not.toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('All series hidden');
    expect(within(legend()).getAllByRole('button')).toHaveLength(2);
    fireEvent.click(button(firstName));
    expect(points().every((point) => point.series === firstName)).toBe(true);
    expect(button(secondName)).toHaveAttribute('aria-pressed', 'false');
  });
  it('新增采样或型号显示名变化保留同传感器隐藏状态，新序列默认显示', () => {
    const view = render(
      <TrendChart
        scopeKey="server-a:temperatures"
        series={initial}
        unit="°C"
      />,
    );
    fireEvent.click(button(firstName));
    const renamed = 'Saved CPU model (CPU 1)';
    view.rerender(
      <TrendChart
        scopeKey="server-a:temperatures"
        series={[
          series('temperature:2', renamed, [80]),
          series('temperature:3', secondName, [46]),
          series('raid:1', 'Array controller', [51]),
        ]}
        unit="°C"
      />,
    );
    expect(button(renamed)).toHaveAttribute('aria-pressed', 'false');
    expect(button('Array controller')).toHaveAttribute('aria-pressed', 'true');
    expect(points().map((point) => point.value)).toEqual([46, 51]);
  });
  it('服务器切换重置显示状态，不同面板实例互不共享选择', () => {
    const view = render(
      <TrendChart
        scopeKey="server-a:temperatures"
        series={initial}
        unit="°C"
      />,
    );
    fireEvent.click(button(firstName));
    view.rerender(
      <TrendChart
        scopeKey="server-b:temperatures"
        series={initial}
        unit="°C"
      />,
    );
    expect(button(firstName)).toHaveAttribute('aria-pressed', 'true');
    view.unmount();
    render(
      <>
        <TrendChart
          scopeKey="server-a:temperatures"
          series={initial}
          unit="°C"
        />
        <TrendChart
          scopeKey="server-a:other-panel"
          series={initial}
          unit="°C"
        />
      </>,
    );
    const lists = screen.getAllByRole('list', { name: 'Legend' });
    fireEvent.click(within(lists[0]).getByRole('button', { name: firstName }));
    expect(
      within(lists[0]).getByRole('button', { name: firstName }),
    ).toHaveAttribute('aria-pressed', 'false');
    expect(
      within(lists[1]).getByRole('button', { name: firstName }),
    ).toHaveAttribute('aria-pressed', 'true');
  });
  it('单序列也能隐藏恢复，长历史使用调用方标题、高度和日期刻度', () => {
    render(
      <TrendChart
        scopeKey="server-a:history"
        series={[initial[0]]}
        unit="°C"
        title="Last 7 days"
        height={320}
        xLabelFormatter={() => '2026-10-25 08:00'}
        pointDescription={() => 'Available 2/3'}
      />,
    );
    expect(screen.getByRole('img', { name: 'Last 7 days °C' })).toHaveStyle({
      height: '320px',
    });
    expect(screen.getByTestId('trend-canvas')).toHaveAttribute(
      'data-axis-label',
      '2026-10-25 08:00',
    );
    expect(
      screen.getByTestId('trend-canvas').getAttribute('data-point-title'),
    ).toContain('Available 2/3');
    fireEvent.click(button(firstName));
    expect(screen.getByRole('status')).toHaveTextContent('All series hidden');
    fireEvent.click(button(firstName));
    expect(screen.getByTestId('trend-canvas')).toBeInTheDocument();
  });
});
