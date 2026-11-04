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
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import zhCN from '@/locales/zh-CN/oob';
import type { HistorySeriesDto } from '@/services/oob/models';
import { telemetryFixture } from '../../../../tests/oobFixtures';
import type { AggregateKind } from './aggregate.types';
import type { HistoryPanelKind } from './historyTrends';
import { sensorIdentity } from './metrics';
import type { OverviewServerData } from './overviewData.types';

const history = vi.hoisted(() => vi.fn());
vi.mock('@/services/oob/api', () => ({ getHistory: history }));
vi.mock('../intl', () => ({
  useOobIntl:
    () => (key: keyof typeof zhCN, values?: Record<string, string | number>) =>
      zhCN[key].replace(/\{(\w+)\}/g, (_, name: string) =>
        String(values?.[name] ?? ''),
      ),
}));
vi.mock('./TrendChart', () => ({
  default: ({
    series,
    unit,
    dashedSeries,
  }: {
    series: HistorySeriesDto[];
    unit: string;
    dashedSeries?: readonly string[];
  }) => (
    <output
      data-testid="local-history-chart"
      data-unit={unit}
      data-dashed-series={JSON.stringify(dashedSeries || [])}
    >
      {JSON.stringify(series)}
    </output>
  ),
}));

import HistoryTrendDialog from './HistoryTrendDialog';

const now = Date.parse('2026-10-25T02:00:00Z');
const series = (code: string): HistorySeriesDto => ({
  sensorKey: 'system',
  name: code === 'power.watts' ? '本地功率' : 'CPU1 Temp',
  metricCode: code,
  unit: code === 'power.watts' ? 'W' : '°C',
  points: [
    {
      time: '2026-10-25T01:00:00Z',
      average: 123,
      minimum: 123,
      maximum: 123,
      count: 1,
    },
  ],
});
const clients: QueryClient[] = [];
function mount(kind: HistoryPanelKind = 'power') {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  clients.push(client);
  return render(
    <QueryClientProvider client={client}>
      <HistoryTrendDialog
        serverId="9007199254740993"
        serverName="测试服务器"
        kind={kind}
      />
    </QueryClientProvider>,
  );
}

function servers(): OverviewServerData[] {
  return ['9007199254740993', '9007199254740995'].map((id, index) => {
    const snapshot = telemetryFixture(new Date(now).toISOString(), []);
    snapshot.server.id = id;
    snapshot.server.name = `服务器${index + 1}`;
    return {
      snapshot,
      inventory: null,
      history: [],
      gaps: [],
      loading: false,
      issues: [],
    };
  });
}
function mountAggregate(rows = servers(), kind: HistoryPanelKind = 'power') {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  clients.push(client);
  const view = (
    nextRows = rows,
    aggregateKind: AggregateKind = kind === 'fans'
      ? 'fanSpeedPercent'
      : kind === 'temperatures'
        ? 'array'
        : kind === 'airflow'
          ? 'inlet'
          : kind === 'cpu'
            ? 'cpu'
            : 'power',
  ) => (
    <QueryClientProvider client={client}>
      <HistoryTrendDialog
        servers={nextRows}
        aggregateKind={aggregateKind}
        kind={kind}
      />
    </QueryClientProvider>
  );
  const mounted = render(view());
  return {
    ...mounted,
    update: (nextRows: OverviewServerData[], aggregateKind?: AggregateKind) =>
      mounted.rerender(view(nextRows, aggregateKind)),
  };
}
describe('本地历史查询弹窗', () => {
  beforeEach(() => {
    vi.spyOn(Date, 'now').mockReturnValue(now);
    history.mockReset();
    history.mockImplementation((_id, params) =>
      Promise.resolve({
        success: true,
        data: [series(params.metricCode)],
        status: 200,
      }),
    );
  });
  afterEach(() => {
    cleanup();
    for (const client of clients.splice(0)) client.clear();
    vi.restoreAllMocks();
    focusManager.setFocused(undefined);
    onlineManager.setOnline(true);
  });

  const thermalSeries = () =>
    [
      ['cpu1', 'CPU1 Temp', 40],
      ['cpu2', 'CPU2 Temp', 42],
      ['array', '31-HD Controller', 53],
      ['inlet', '01-Inlet Ambient', 20],
      ['exhaust1', '02-Sys Exhaust', 32],
      ['exhaust2', '03-Sys Exhaust', 34],
      ['psu', 'P/S 1 Inlet', 28],
    ].map(([sensorKey, name, value]) => ({
      ...series('temperature.celsius'),
      sensorKey: String(sensorKey),
      name: String(name),
      points: [
        {
          ...series('temperature.celsius').points[0],
          average: Number(value),
          minimum: Number(value),
          maximum: Number(value),
        },
      ],
    }));

  it('单机CPU搜索范围不混入阵列卡与进排气', async () => {
    history.mockResolvedValue({
      success: true,
      data: thermalSeries(),
      status: 200,
    });
    mount('cpu');
    fireEvent.click(
      screen.getByRole('button', { name: '测试服务器 · CPU 温度 · 历史趋势' }),
    );
    const chart = await screen.findByTestId('local-history-chart');
    const rows: HistorySeriesDto[] = JSON.parse(chart.textContent || '[]');
    expect(rows.map((row) => row.sensorKey)).toEqual(['cpu1', 'cpu2']);
    expect(history).toHaveBeenCalledTimes(1);
  });

  it('单机阵列卡搜索仅返回本机真实卡温，不能混入CPU或机箱探头', async () => {
    history.mockResolvedValue({
      success: true,
      data: thermalSeries(),
      status: 200,
    });
    mount('array');
    fireEvent.click(
      screen.getByRole('button', {
        name: '测试服务器 · 阵列卡温度 · 历史趋势',
      }),
    );
    const chart = await screen.findByTestId('local-history-chart');
    const rows: HistorySeriesDto[] = JSON.parse(chart.textContent || '[]');
    expect(rows.map((row) => row.sensorKey)).toEqual(['array']);
    expect(history).toHaveBeenCalledTimes(1);
    expect(history.mock.calls[0][0]).toBe('9007199254740993');
    expect(history.mock.calls[0][1].metricCode).toBe('temperature.celsius');
  });

  it.each([
    ['fanRpmDetails', 'fan.rpm', 'RPM', '风扇转速'],
    ['fanPercentDetails', 'fan.percent', '%', '转速百分比（%）'],
  ] as const)('单机风扇历史只请求所选单位并保留逐风扇曲线：%s', async (kind, code, unit, title) => {
    history.mockResolvedValue({
      success: true,
      status: 200,
      data: [
        ...['fan1', 'fan2'].map((sensorKey) => ({
          ...series(code),
          sensorKey,
          name: 'Fan',
          unit,
        })),
        { ...series('temperature.celsius'), sensorKey: 'cpu' },
      ],
    });
    mount(kind);
    fireEvent.click(
      screen.getByRole('button', { name: `测试服务器 · ${title} · 历史趋势` }),
    );
    const chart = await screen.findByTestId('local-history-chart');
    const rows: HistorySeriesDto[] = JSON.parse(chart.textContent || '[]');
    expect(rows.map((row) => row.sensorKey)).toEqual(['fan1', 'fan2']);
    expect(chart).toHaveAttribute('data-unit', unit);
    expect(history).toHaveBeenCalledTimes(1);
    expect(history.mock.calls[0][0]).toBe('9007199254740993');
    expect(history.mock.calls[0][1].metricCode).toBe(code);
  });

  it('单机进排气历史保留独立探头与进气虚线，排除电源进气', async () => {
    history.mockResolvedValue({
      success: true,
      data: thermalSeries(),
      status: 200,
    });
    mount('airflow');
    fireEvent.click(
      screen.getByRole('button', {
        name: `测试服务器 · ${zhCN['oob.aggregate.airflow']} · 历史趋势`,
      }),
    );
    const chart = await screen.findByTestId('local-history-chart');
    const rows: HistorySeriesDto[] = JSON.parse(chart.textContent || '[]');
    expect(rows.map((row) => row.sensorKey)).toEqual([
      'inlet',
      'exhaust1',
      'exhaust2',
    ]);
    expect(chart).toHaveAttribute(
      'data-dashed-series',
      JSON.stringify([sensorIdentity(rows[0])]),
    );
  });

  it('总体进排气同图六条曲线分别统计，两台服务器只各发一次温度历史查询', async () => {
    history.mockResolvedValue({
      success: true,
      data: thermalSeries(),
      status: 200,
    });
    mountAggregate(servers(), 'airflow');
    fireEvent.click(
      screen.getByRole('button', {
        name: `聚合看板 · ${zhCN['oob.aggregate.airflow']} · 历史趋势`,
      }),
    );
    const chart = await screen.findByTestId('local-history-chart');
    const rows: HistorySeriesDto[] = JSON.parse(chart.textContent || '[]');
    expect(screen.getAllByTestId('local-history-chart')).toHaveLength(1);
    expect(rows).toHaveLength(6);
    expect(
      rows.map((row) => row.points.find((point) => point.count > 0)?.average),
    ).toEqual([20, 20, 20, 34, 33, 32]);
    expect(chart).toHaveAttribute(
      'data-dashed-series',
      JSON.stringify(rows.slice(0, 3).map(sensorIdentity)),
    );
    expect(history).toHaveBeenCalledTimes(2);
    expect(
      history.mock.calls.every(
        ([, params]) => params.metricCode === 'temperature.celsius',
      ),
    ).toBe(true);
  });

  it('挂载不查询，点击后仅读本地历史，切换24小时/7天/月且保留原服务器字符串ID', async () => {
    mount();
    expect(history).not.toHaveBeenCalled();
    fireEvent.click(
      screen.getByRole('button', { name: '测试服务器 · 整机功率 · 历史趋势' }),
    );
    await waitFor(() => expect(history).toHaveBeenCalledTimes(1));
    expect(history.mock.calls[0][0]).toBe('9007199254740993');
    expect(history.mock.calls[0][1]).toEqual({
      from: '2026-10-24T02:00:00.000Z',
      to: '2026-10-25T02:00:00.000Z',
      bucketSeconds: 300,
      metricCode: 'power.watts',
    });
    expect(await screen.findByTestId('local-history-chart')).toHaveTextContent(
      '123',
    );
    expect(
      screen.getByText('仅查询本地数据库已入库的采集数据，缺失时段保持空白。'),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByText('过去一周'));
    await waitFor(() => expect(history).toHaveBeenCalledTimes(2));
    expect(history.mock.calls[1][1].from).toBe('2026-10-18T02:00:00.000Z');
    expect(history.mock.calls[1][1].bucketSeconds).toBe(900);
    fireEvent.click(screen.getByText('过去一个月'));
    await waitFor(() => expect(history).toHaveBeenCalledTimes(3));
    expect(history.mock.calls[2][1].from).toBe('2026-09-25T02:00:00.000Z');
    expect(history.mock.calls[2][1].bucketSeconds).toBe(3600);
  });

  it('空数据不生成曲线，业务失败显示错误，不用模拟读数补齐', async () => {
    history.mockResolvedValue({
      success: false,
      errorMessage: '本地查询失败',
      kind: 'business',
    });
    mount('temperatures');
    fireEvent.click(
      screen.getByRole('button', {
        name: '测试服务器 · CPU / 阵列卡温度 · 历史趋势',
      }),
    );
    expect(
      await screen.findByText('测试服务器 · 本地查询失败'),
    ).toBeInTheDocument();
    expect(screen.queryByTestId('local-history-chart')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '关闭' }));
    history.mockResolvedValue({ success: true, data: [], status: 200 });
    fireEvent.click(
      screen.getByRole('button', {
        name: '测试服务器 · CPU / 阵列卡温度 · 历史趋势',
      }),
    );
    expect(
      await screen.findByText('所选时间范围内，本地没有可用采样数据'),
    ).toBeInTheDocument();
    expect(screen.queryByTestId('local-history-chart')).not.toBeInTheDocument();
  });

  it('切回窗口及重连不自动重新查询，关闭弹窗取消旧请求', async () => {
    mount();
    fireEvent.click(
      screen.getByRole('button', { name: '测试服务器 · 整机功率 · 历史趋势' }),
    );
    await screen.findByTestId('local-history-chart');
    await act(async () => {
      focusManager.setFocused(false);
      onlineManager.setOnline(false);
      focusManager.setFocused(true);
      onlineManager.setOnline(true);
      await Promise.resolve();
    });
    expect(history).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: /查 询|查询/ }));
    await waitFor(() => expect(history).toHaveBeenCalledTimes(2));
    fireEvent.click(screen.getByRole('button', { name: '关闭' }));
    let signal: AbortSignal | undefined;
    history.mockImplementation((_id, _params, nextSignal) => {
      signal = nextSignal;
      return new Promise(() => {});
    });
    fireEvent.click(
      screen.getByRole('button', { name: '测试服务器 · 整机功率 · 历史趋势' }),
    );
    await waitFor(() => expect(signal).toBeDefined());
    expect(signal?.aborted).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: '关闭' }));
    await waitFor(() => expect(signal?.aborted).toBe(true));
  });

  it('聚合查询覆盖全部服务器，预设与自选共用范围，实时数据不触发重查', async () => {
    const mounted = mountAggregate();
    expect(history).not.toHaveBeenCalled();
    fireEvent.click(
      screen.getByRole('button', { name: '聚合看板 · 整机功率 · 历史趋势' }),
    );
    const chart = await screen.findByTestId('local-history-chart');
    expect(history.mock.calls.map(([id]) => id)).toEqual([
      '9007199254740993',
      '9007199254740995',
    ]);
    expect(chart).toHaveTextContent('246');
    fireEvent.click(screen.getByText('过去一周'));
    await waitFor(() => expect(history).toHaveBeenCalledTimes(4));
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: /查 询|查询/ }),
      ).not.toHaveClass('ant-btn-loading'),
    );
    fireEvent.click(screen.getByText('自选范围'));
    expect(history).toHaveBeenCalledTimes(4);
    fireEvent.click(screen.getByRole('button', { name: /查 询|查询/ }));
    await waitFor(() => expect(history).toHaveBeenCalledTimes(6));
    expect(history.mock.calls[4][1]).toMatchObject({
      from: '2026-10-18T02:00:00.000Z',
      to: '2026-10-25T02:00:00.000Z',
      bucketSeconds: 900,
    });
    mounted.update(
      servers().map((row) => ({
        ...row,
        snapshot: { ...row.snapshot, publishedAt: '2026-10-25T03:00:00Z' },
      })),
    );
    await act(async () => {
      await Promise.resolve();
    });
    expect(history).toHaveBeenCalledTimes(6);
  });

  it('部分服务器失败仍展示其余有效历史，并明确失败服务器', async () => {
    history.mockImplementation((id) =>
      Promise.resolve(
        id === '9007199254740995'
          ? { success: false, errorMessage: '读取失败', kind: 'business' }
          : { success: true, data: [series('power.watts')], status: 200 },
      ),
    );
    mountAggregate();
    fireEvent.click(
      screen.getByRole('button', { name: '聚合看板 · 整机功率 · 历史趋势' }),
    );
    expect(await screen.findByText('服务器2 · 读取失败')).toBeInTheDocument();
    expect(screen.getByTestId('local-history-chart')).toHaveTextContent('123');
    expect(screen.getByTestId('local-history-chart')).not.toHaveTextContent(
      '246',
    );
  });

  it('快速切换区间取消旧请求，迟到结果不覆盖当前区间', async () => {
    const pending: {
      signal: AbortSignal;
      resolve: (value: unknown) => void;
    }[] = [];
    history.mockImplementation(
      (_id, _params, signal) =>
        new Promise((resolve) => pending.push({ signal, resolve })),
    );
    mountAggregate();
    fireEvent.click(
      screen.getByRole('button', { name: '聚合看板 · 整机功率 · 历史趋势' }),
    );
    await waitFor(() => expect(pending).toHaveLength(2));
    history.mockResolvedValue({
      success: true,
      data: [series('power.watts')],
      status: 200,
    });
    fireEvent.click(screen.getByText('过去一周'));
    await screen.findByTestId('local-history-chart');
    expect(pending.every((item) => item.signal.aborted)).toBe(true);
    await act(async () => {
      const late = series('power.watts');
      late.points[0].average = 98765;
      pending.forEach((item) => {
        item.resolve({ success: true, data: [late], status: 200 });
      });
    });
    expect(screen.getByTestId('local-history-chart')).toHaveTextContent('246');
    expect(screen.getByTestId('local-history-chart')).not.toHaveTextContent(
      '98765',
    );
  });

  it('库存加载时等待，库存失败时在温度弹窗内显示原因', async () => {
    const rows = servers();
    rows[0].loading = true;
    const mounted = mountAggregate(rows, 'temperatures');
    const trigger = screen.getByRole('button', {
      name: '聚合看板 · 阵列卡温度 · 历史趋势',
    });
    expect(trigger).toBeDisabled();
    rows[0] = {
      ...rows[0],
      loading: false,
      issues: [{ kind: 'inventory', message: '库存读取失败' }],
    };
    mounted.update([...rows]);
    fireEvent.click(trigger);
    expect(
      await screen.findByText('服务器1 · 库存读取失败'),
    ).toBeInTheDocument();
  });

  it('风扇实时能力回退不会重查已打开的历史', async () => {
    history.mockResolvedValue({ success: true, data: [], status: 200 });
    const mounted = mountAggregate(servers(), 'fans');
    fireEvent.click(
      screen.getByRole('button', { name: '聚合看板 · 风扇转速 · 历史趋势' }),
    );
    await waitFor(() => expect(history).toHaveBeenCalledTimes(2));
    mounted.update(servers(), 'fanRpm');
    await act(async () => {
      await Promise.resolve();
    });
    expect(history).toHaveBeenCalledTimes(2);
    expect(history.mock.calls.map(([, params]) => params.metricCode)).toEqual([
      'fan.percent',
      'fan.percent',
    ]);
  });

  it('单机风扇打开后固定单位，主卡能力回退不改变历史标题、曲线或重查', async () => {
    history.mockImplementation((_id, params) =>
      Promise.resolve({
        success: true,
        status: 200,
        data: [
          {
            ...series(params.metricCode),
            sensorKey: 'fan1',
            name: 'Fan 1',
            unit: '%',
          },
        ],
      }),
    );
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    clients.push(client);
    const view = (kind: HistoryPanelKind) => (
      <QueryClientProvider client={client}>
        <HistoryTrendDialog
          serverId="9007199254740993"
          serverName="测试服务器"
          kind={kind}
        />
      </QueryClientProvider>
    );
    const mounted = render(view('fanPercentDetails'));
    fireEvent.click(
      screen.getByRole('button', {
        name: '测试服务器 · 转速百分比（%） · 历史趋势',
      }),
    );
    await screen.findByTestId('local-history-chart');
    mounted.rerender(view('fanRpmDetails'));
    await act(async () => {
      await Promise.resolve();
    });
    expect(history).toHaveBeenCalledTimes(1);
    expect(history.mock.calls[0][1].metricCode).toBe('fan.percent');
    expect(screen.getByTestId('local-history-chart')).toHaveAttribute(
      'data-unit',
      '%',
    );
    expect(screen.getByRole('dialog')).toHaveAccessibleName(
      '测试服务器 · 转速百分比（%） · 历史趋势',
    );
  });

  it('历史图例排除整段没有有效点的旧风扇，保留有效零值和曲线内部缺口', async () => {
    const point = series('fan.rpm').points[0];
    const valid = {
      ...series('fan.rpm'),
      sensorKey: 'current',
      name: 'Fan 1',
      unit: 'RPM',
      points: [
        { ...point, average: 0, minimum: 0, maximum: 0 },
        {
          ...point,
          time: '2026-10-25T01:05:00Z',
          average: null,
          minimum: null,
          maximum: null,
          count: 0,
        },
      ],
    };
    const empty = Array.from({ length: 40 }, (_, i) => ({
      ...valid,
      sensorKey: `retired:${i}`,
      points: [
        { ...point, average: null, minimum: null, maximum: null, count: 0 },
      ],
    }));
    history.mockResolvedValue({
      success: true,
      status: 200,
      data: [...empty, valid],
    });
    mount('fanRpmDetails');
    fireEvent.click(
      screen.getByRole('button', { name: '测试服务器 · 风扇转速 · 历史趋势' }),
    );
    const chart = await screen.findByTestId('local-history-chart');
    const rows: HistorySeriesDto[] = JSON.parse(chart.textContent || '[]');
    expect(rows.map((row) => row.sensorKey)).toEqual(['current']);
    expect(rows[0].points.map((row) => row.average)).toEqual([0, null]);
  });
});
