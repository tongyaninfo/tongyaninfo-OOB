import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { MetricReading } from '@/services/oob/models';

vi.mock('@/services/oob/preferences', () => ({
  getTablePreference: () => ({ size: 'middle', columnsState: {} }),
  updateTablePreference: vi.fn(),
}));
vi.mock('../intl', async () => {
  const { fanDetailsZhCN } = await import('./fanMessages');
  const messages: Record<string, string> = {
    ...fanDetailsZhCN,
    'oob.noData': '暂无数据',
    'oob.table.settings': '表格设置',
    'oob.observedAt': '观测时间',
    'oob.availability.Available': '可用',
    'oob.availability.Unsupported': '不支持',
  };
  return {
    useOobIntl:
      () =>
      (key: string, values: Record<string, string> = {}) =>
        Object.entries(values).reduce(
          (text, [name, value]) => text.replaceAll(`{${name}}`, value),
          messages[key] || key,
        ),
  };
});

import FanDetails from './FanDetails';

function metric(
  index: number,
  code: string,
  value: number | null,
  availability = 'Available',
): MetricReading {
  return {
    sensorKey: `fan:0x${index}||Fan.Embedded.${index}A`,
    name: `Fan ${index}`,
    metricCode: code,
    value,
    unit: code === 'fan.rpm' ? 'RPM' : '%',
    availability,
    source: 'Device',
    health: value === null ? null : 'OK',
    observedAt: '2026-10-25T00:00:00Z',
    deviceTime: null,
  };
}
const view = (metrics: MetricReading[]) => (
  <ConfigProvider theme={{ token: { motion: false } }}>
    <App>
      <FanDetails
        serverId="9007199254740993"
        serverName="Dell test"
        metrics={metrics}
      />
    </App>
  </ConfigProvider>
);

function fanRows(dialog: HTMLElement) {
  return within(dialog)
    .getAllByRole('row')
    .filter((row) => within(row).queryAllByRole('cell').length > 0);
}

describe('风扇详情真实弹窗与表格', () => {
  afterEach(cleanup);

  it('点击才展开全部14路，零转速保留、缺测为横线，并可按完整数据排序关闭', async () => {
    const metrics: MetricReading[] = [];
    for (let index = 1; index <= 14; index++) {
      metrics.push(metric(index, 'fan.rpm', index === 2 ? 0 : 5000 + index));
      metrics.push(
        metric(
          index,
          'fan.percent',
          index === 1 ? null : 25,
          index === 1 ? 'Unsupported' : 'Available',
        ),
      );
    }
    render(view(metrics));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Dell test 风扇详情' }));
    const dialog = await screen.findByRole('dialog');
    expect(
      within(dialog).getByText('风扇详情 · Dell test'),
    ).toBeInTheDocument();
    expect(fanRows(dialog)).toHaveLength(14);
    const zeroRow = fanRows(dialog).find((row) =>
      within(row).queryByRole('cell', { name: 'Fan 2' }),
    );
    expect(zeroRow).toBeDefined();
    expect(within(zeroRow as HTMLElement).getByText('0')).toBeInTheDocument();
    const missingRow = fanRows(dialog).find((row) =>
      within(row).queryByRole('cell', { name: 'Fan 1' }),
    );
    // 所有品牌只显示一个统一百分比列，缺测仍保持短横线。
    expect(within(missingRow as HTMLElement).getAllByText('-')).toHaveLength(1);
    expect(
      within(dialog).queryByRole('columnheader', {
        name: /转速百分比（PWM\/%）/,
      }),
    ).not.toBeInTheDocument();
    expect(
      within(dialog).getByRole('columnheader', {
        name: /转速百分比（%）/,
      }),
    ).toBeInTheDocument();
    expect(within(dialog).getAllByText('25', { exact: true })).toHaveLength(13);
    expect(
      within(dialog).getByRole('button', { name: '表格设置' }),
    ).toBeInTheDocument();
    fireEvent.click(
      within(dialog).getAllByRole('columnheader', { name: /转速（RPM）/ })[0],
    );
    await waitFor(() =>
      expect(
        within(fanRows(dialog)[0]).getByRole('cell', { name: 'Fan 2' }),
      ).toBeInTheDocument(),
    );
    fireEvent.click(within(dialog).getByRole('button', { name: '关闭' }));
    await waitFor(() =>
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
    );
  });

  it('打开期间直接响应新推送的props，不重新打开或补取旧数据', async () => {
    const initial = [metric(1, 'fan.rpm', 0), metric(1, 'fan.percent', 40)];
    const mounted = render(view(initial));
    fireEvent.click(screen.getByRole('button', { name: 'Dell test 风扇详情' }));
    let dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('0')).toBeInTheDocument();
    mounted.rerender(
      view([
        metric(1, 'fan.rpm', 6200),
        metric(1, 'fan.percent', 55),
        metric(2, 'fan.rpm', null, 'Unsupported'),
        metric(2, 'fan.percent', null, 'Unsupported'),
      ]),
    );
    dialog = screen.getByRole('dialog');
    expect(within(dialog).getByText('6,200')).toBeInTheDocument();
    expect(within(dialog).getByText('55')).toBeInTheDocument();
    expect(within(dialog).queryByText('40')).not.toBeInTheDocument();
    expect(fanRows(dialog)).toHaveLength(2);
    expect(within(dialog).getAllByText('-').length).toBeGreaterThan(0);
  });

  it('HP使用同一百分比列，真实0有效，RPM不补造且支持缺测', async () => {
    render(
      view([
        metric(1, 'fan.rpm', null, 'Unsupported'),
        metric(1, 'fan.percent', 0),
        metric(2, 'fan.rpm', null, 'Unsupported'),
        metric(2, 'fan.percent', 39),
        metric(3, 'fan.rpm', null, 'Unsupported'),
        metric(3, 'fan.percent', null, 'TemporarilyUnavailable'),
      ]),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Dell test 风扇详情' }));
    const dialog = await screen.findByRole('dialog');
    expect(
      within(dialog).getByRole('columnheader', { name: /转速百分比（%）/ }),
    ).toBeInTheDocument();
    expect(within(dialog).getByText('0')).toBeInTheDocument();
    expect(within(dialog).getByText('39')).toBeInTheDocument();
    for (const row of fanRows(dialog))
      expect(within(row).getAllByText('-').length).toBeGreaterThanOrEqual(1);
    expect(fanRows(dialog)).toHaveLength(3);
  });
});
