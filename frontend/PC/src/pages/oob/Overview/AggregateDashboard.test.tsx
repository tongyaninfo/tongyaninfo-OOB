import {
  cleanup,
  fireEvent,
  render,
  screen,
  within,
} from '@testing-library/react';
import { ConfigProvider } from 'antd';
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { HistorySeriesDto, MetricReading } from '@/services/oob/models';
import { telemetryFixture } from '../../../../tests/oobFixtures';
import { aggregateDashboard } from './aggregate';
import type {
  AggregateKind,
  AggregatePanel,
  AggregateReading,
  AggregateResult,
} from './aggregate.types';
import { sensorIdentity } from './metrics';
import type { OverviewServerData } from './overviewData.types';

vi.mock('../intl', async () => {
  const { aggregateZhCN } = await import('./aggregateMessages');
  const { historyTrendsZhCN } = await import('./historyMessages');
  const messages: Record<string, string> = {
    ...aggregateZhCN,
    ...historyTrendsZhCN,
    'oob.monitor.cpuSlot': 'CPU {number}',
    'oob.monitor.power': '整机功率',
  };
  return {
    useOobIntl:
      () =>
      (key: string, values: Record<string, string | number> = {}) =>
        Object.entries(values).reduce(
          (text, [name, value]) => text.replaceAll(`{${name}}`, String(value)),
          messages[key] || key,
        ),
  };
});
vi.mock('./TrendChart', () => ({
  default: ({
    unit,
    scopeKey,
    series,
    pointDescription,
    dashedSeries,
  }: {
    unit: string;
    scopeKey: string;
    series: HistorySeriesDto[];
    pointDescription: (time: number) => string | undefined;
    dashedSeries?: readonly string[];
  }) => (
    <output
      data-testid="aggregate-trend"
      data-scope={scopeKey}
      data-unit={unit}
      data-dashed={JSON.stringify(dashedSeries || [])}
      data-series={JSON.stringify(series)}
    >
      {series.map((row) => row.name).join(', ')}
      {pointDescription(Date.parse('2026-10-25T02:00:00Z'))}
    </output>
  ),
}));

import AggregateDashboard from './AggregateDashboard';

function reading(
  key: string,
  serverId: string,
  value: number | null,
  state: AggregateReading['state'] = 'available',
): AggregateReading {
  return {
    key,
    serverId,
    serverName: serverId === '1' ? '演示·计算节点 A' : '演示·计算节点 C',
    label: `CPU ${key}`,
    slot: key,
    model: 'Intel Xeon test',
    value,
    unit: '°C',
    observedAt: '2026-10-25T02:00:00Z',
    state,
    availability: state === 'unsupported' ? 'Unsupported' : 'Available',
    counted: true,
  };
}

function panel(kind: AggregateKind): AggregatePanel {
  const unit =
    kind === 'power'
      ? 'W'
      : kind === 'fanRpm'
        ? 'RPM'
        : kind.startsWith('fan')
          ? '%'
          : '°C';
  return {
    kind,
    unit,
    stats: { average: null, minimum: null, maximum: null, total: null },
    validCount: 0,
    totalCount: 0,
    readings: [],
    series: [],
    coverage: {},
  };
}

function data(): AggregateResult {
  const result = Object.fromEntries(
    [
      'cpu',
      'array',
      'inlet',
      'exhaust',
      'fanRpm',
      'fanSpeedPercent',
      'power',
    ].map((kind) => [kind, panel(kind as AggregateKind)]),
  ) as AggregateResult;
  result.cpu = {
    ...result.cpu,
    readings: [
      reading('1', '1', 0),
      reading('2', '1', 40),
      reading('3', '2', null, 'offline'),
    ],
    validCount: 2,
    totalCount: 3,
    stats: { average: 20, minimum: 0, maximum: 40, total: 40 },
  };
  return result;
}

const view = (value: AggregateResult) => (
  <ConfigProvider theme={{ token: { motion: false } }}>
    <AggregateDashboard data={value} loading={false} servers={[]} />
  </ConfigProvider>
);

describe('聚合数字与只读明细交互', () => {
  afterEach(cleanup);

  it('首页五张图按CPU、功率、风扇、阵列、进排气排序并提供历史搜索入口', () => {
    render(view(data()));
    const names = [
      'CPU 温度',
      '整机功率',
      '风扇转速',
      '阵列卡温度',
      '进气 / 排气温度',
    ];
    expect(
      screen
        .getAllByRole('region')
        .map((section) => section.getAttribute('aria-label')),
    ).toEqual(names);
    for (const name of names) {
      const card = screen.getByRole('region', { name });
      expect(
        within(card).getByRole('button', {
          name: `聚合看板 · ${name} · 历史趋势`,
        }),
      ).toBeInTheDocument();
    }
    expect(
      screen.getByRole('button', { name: 'CPU 温度 · 最低温度' }),
    ).toHaveTextContent('0');
  });

  it('键盘聚焦显示按服务器分组的 CPU 型号、真实零温度及排除原因，并随新数据更新', async () => {
    const value = data();
    const mounted = render(view(value));
    const trigger = screen.getByRole('button', { name: 'CPU 温度 · 平均温度' });
    fireEvent.focus(trigger);
    const tooltip = await screen.findByRole('tooltip');
    const first = within(tooltip).getByRole('region', { name: '演示·计算节点 A' });
    expect(within(first).getByText('CPU 1')).toBeInTheDocument();
    expect(within(first).getByText('CPU 2')).toBeInTheDocument();
    expect(within(first).getAllByText('Intel Xeon test')).toHaveLength(2);
    expect(within(first).getByText('0 °C')).toBeInTheDocument();
    expect(within(first).getByText('40 °C')).toBeInTheDocument();
    const missing = within(tooltip).getByRole('region', { name: '演示·计算节点 C' });
    expect(missing).toHaveTextContent('服务器连接异常 · 未计入统计');
    expect(missing).toHaveTextContent('-');
    expect(first).toHaveTextContent('2026-10-25 10:00:00');

    const updated = {
      ...value,
      cpu: {
        ...value.cpu,
        stats: { ...value.cpu.stats, average: 41, maximum: 42 },
        readings: value.cpu.readings.map((row) =>
          row.key === '1' ? { ...row, value: 42 } : row,
        ),
      },
    };
    mounted.rerender(view(updated));
    expect(within(tooltip).getByText('42 °C')).toBeInTheDocument();
    expect(within(tooltip).queryByText('0 °C')).not.toBeInTheDocument();
  });

  it('所有服务器共用百分比切换、均值和覆盖数量，RPM缺测不被替代', () => {
    const value = data();
    value.fanRpm.totalCount = 7;
    value.fanSpeedPercent = {
      ...value.fanSpeedPercent,
      validCount: 7,
      totalCount: 7,
      stats: { average: 20, minimum: 18, maximum: 22, total: 140 },
    };
    const mounted = render(view(value));
    const rpmPanel = screen.getByRole('region', { name: '风扇转速' });
    expect(within(rpmPanel).getByText('平均转速')).toBeInTheDocument();
    expect(within(rpmPanel).getAllByText('-')).toHaveLength(3);
    expect(rpmPanel).toHaveTextContent('RPM 0/7 · 转速百分比（%）7/7');
    expect(screen.queryByText(/PWM/)).not.toBeInTheDocument();
    fireEvent.click(screen.getByText('转速百分比（%）', { exact: true }));
    const percentage = screen.getByRole('region', { name: '转速百分比（%）' });
    expect(within(percentage).getByText('20')).toBeInTheDocument();
    expect(within(percentage).getByTestId('aggregate-trend')).toHaveAttribute(
      'data-unit',
      '%',
    );
    mounted.rerender(
      view({
        ...value,
        fanSpeedPercent: {
          ...value.fanSpeedPercent,
          stats: { average: 24, minimum: 24, maximum: 24, total: 168 },
        },
      }),
    );
    expect(within(percentage).getAllByText('24')).toHaveLength(3);
    fireEvent.click(screen.getByText('RPM', { exact: true }));
    expect(
      within(screen.getByRole('region', { name: '风扇转速' })).getAllByText(
        '-',
      ),
    ).toHaveLength(3);
  });

  it('没有设备百分比能力或有效历史时不显示百分比切换', () => {
    const value = data();
    value.fanRpm.validCount = 1;
    value.fanRpm.totalCount = 1;
    render(view(value));
    expect(
      screen.queryByText('转速百分比（%）', { exact: true }),
    ).not.toBeInTheDocument();
  });

  it('已发现的设备百分比临时缺测时保留切换并显示空值', () => {
    const value = data();
    value.fanRpm.totalCount = 1;
    value.fanSpeedPercent.totalCount = 1;
    value.fanSpeedPercent.readings = [
      {
        ...reading('fan1', '1', null, 'missing'),
        unit: '%',
        availability: 'TemporarilyUnavailable',
      },
    ];
    render(view(value));
    fireEvent.click(screen.getByText('转速百分比（%）', { exact: true }));
    const percentage = screen.getByRole('region', {
      name: '转速百分比（%）',
    });
    expect(within(percentage).getAllByText('-')).toHaveLength(3);
    expect(percentage).toHaveTextContent('转速百分比（%）0/1');
  });

  it.each([
    'stale',
    'offline',
    'disabled',
  ] as const)('Dell设备百分比不支持时，%s状态不制造百分比能力', (state) => {
    const value = data();
    value.fanSpeedPercent.totalCount = 1;
    value.fanSpeedPercent.readings = [
      {
        ...reading('fan1', '2', null, state),
        unit: '%',
        availability: 'Unsupported',
      },
    ];
    render(view(value));
    expect(
      screen.queryByText('转速百分比（%）', { exact: true }),
    ).not.toBeInTheDocument();
  });

  it('整机功率按服务器显示运行功率、可用容量和压力，超额时文字保留实际百分比', () => {
    const value = data();
    value.power.pressureRows = [
      {
        serverId: '1',
        serverName: '演示·计算节点 A',
        usedWatts: 150,
        capacityWatts: 500,
        percent: 30,
        reason: null,
        powerState: 'available',
      },
      {
        serverId: '2',
        serverName: '演示·计算节点 C',
        usedWatts: 600,
        capacityWatts: 500,
        percent: 120,
        reason: null,
        powerState: 'available',
      },
    ];
    render(view(value));
    const power = screen.getByRole('region', { name: '整机功率' });
    expect(within(power).getByText('当前电源压力')).toBeInTheDocument();
    const first = within(power).getByRole('group', { name: '演示·计算节点 A' });
    expect(first).toHaveTextContent('150 W');
    expect(first).toHaveTextContent('500 W');
    expect(first).toHaveTextContent('压力 30%');
    expect(within(first).getByRole('progressbar')).toHaveAttribute(
      'aria-valuenow',
      '30',
    );
    const over = within(power).getByRole('group', { name: '演示·计算节点 C' });
    expect(over).toHaveTextContent('压力 120%');
    expect(within(over).getByRole('progressbar')).toHaveAttribute(
      'aria-valuenow',
      '100',
    );
  });

  it('容量缺失与离线时保留设备行，不显示伪造的压力进度', () => {
    const value = data();
    value.power.pressureRows = [
      {
        serverId: '1',
        serverName: '演示·计算节点 A',
        usedWatts: 120,
        capacityWatts: null,
        percent: null,
        reason: 'capacity',
        powerState: 'available',
      },
      {
        serverId: '2',
        serverName: '演示·计算节点 C',
        usedWatts: null,
        capacityWatts: null,
        percent: null,
        reason: 'power',
        powerState: 'offline',
      },
    ];
    render(view(value));
    const power = screen.getByRole('region', { name: '整机功率' });
    const missing = within(power).getByRole('group', { name: '演示·计算节点 A' });
    expect(missing).toHaveTextContent('120 W');
    expect(missing).toHaveTextContent('可用功率容量不可用');
    expect(missing).toHaveTextContent('压力不可计算');
    expect(within(missing).queryByRole('progressbar')).not.toBeInTheDocument();
    const offline = within(power).getByRole('group', { name: '演示·计算节点 C' });
    expect(offline).toHaveTextContent('当前功率不可用');
    expect(offline).toHaveTextContent('压力不可计算');
    expect(within(offline).queryByRole('progressbar')).not.toBeInTheDocument();
  });

  it('功率缺读数时明确显示可用合计，并保留全部并列最高来源与图表覆盖提示', async () => {
    const value = data();
    value.power = {
      ...value.power,
      validCount: 2,
      totalCount: 3,
      stats: { average: 120, total: 240, minimum: 120, maximum: 120 },
      readings: [reading('p1', '1', 120), reading('p2', '2', 120)].map(
        (row) => ({
          ...row,
          unit: 'W',
        }),
      ),
      coverage: {
        [Date.parse('2026-10-25T02:00:00Z')]: {
          validCount: 2,
          totalCount: 3,
          serverNames: ['演示·计算节点 A', '演示·计算节点 C'],
        },
      },
    };
    render(view(value));
    const power = screen.getByRole('region', { name: '整机功率' });
    expect(within(power).getByText('可用运行功率合计')).toBeInTheDocument();
    expect(within(power).getByText('240')).toBeInTheDocument();
    expect(within(power).queryByText('总功率')).not.toBeInTheDocument();
    const highest = within(power).getByRole('button', {
      name: '整机功率 · 最高功率',
    });
    expect(highest).toHaveTextContent('来自 演示·计算节点 A, 演示·计算节点 C');
    fireEvent.click(highest);
    const tooltip = await screen.findByRole('tooltip');
    expect(within(tooltip).getAllByText('120 W')).toHaveLength(2);
    expect(within(tooltip).getAllByText('最高值')).toHaveLength(2);
    expect(within(power).getByTestId('aggregate-trend')).toHaveTextContent(
      '有效读数 2/3',
    );
  });
});

const scopeNow = Date.parse('2026-11-01T03:00:00Z');
function thermalServer(id: string, name: string): OverviewServerData {
  const metrics: MetricReading[] = [
    ['CPU 1', 40],
    ['CPU 2', 50],
    ['01-Inlet Ambient', 20],
    ['27-Front Ambient', 23],
    ['05-Sys Exhaust', 30],
    ['06-Sys Exhaust', 35],
    ['P/S 1 Inlet', 99],
  ].map(([sensorName, value]) => ({
    sensorKey: String(sensorName),
    name: String(sensorName),
    value: Number(value),
    metricCode: 'temperature.celsius',
    unit: '°C',
    availability: 'Available',
    source: 'Device',
    health: null,
    deviceTime: null,
    observedAt: new Date(scopeNow).toISOString(),
  }));
  const snapshot = telemetryFixture(new Date(scopeNow).toISOString(), metrics);
  snapshot.server.id = id;
  snapshot.server.name = name;
  return {
    snapshot,
    inventory: null,
    loading: false,
    issues: [],
    gaps: [],
    history: metrics.map((metric) => ({
      sensorKey: metric.sensorKey,
      name: metric.name,
      metricCode: metric.metricCode,
      unit: metric.unit,
      points: [
        {
          time: new Date(scopeNow - 30000).toISOString(),
          count: 1,
          average: metric.value,
          minimum: metric.value,
          maximum: metric.value,
        },
      ],
    })),
  };
}
function thermalView(servers: OverviewServerData[]) {
  return (
    <ConfigProvider theme={{ token: { motion: false } }}>
      <AggregateDashboard
        data={aggregateDashboard(servers, scopeNow)}
        servers={servers}
        loading={false}
        now={scopeNow}
      />
    </ConfigProvider>
  );
}

describe('CPU与进排气卡独立循环范围', () => {
  afterEach(cleanup);
  it('总体与每台服务器循环切换，CPU单机保留每块CPU曲线且搜索只针对本卡范围', () => {
    const servers = [
      thermalServer('9007199254740993', '服务器1'),
      thermalServer('9007199254740994', '服务器2'),
    ];
    render(thermalView(servers));
    const cpu = screen.getByRole('region', { name: 'CPU 温度' });
    const air = screen.getByRole('region', { name: '进气 / 排气温度' });
    expect(within(cpu).getByTestId('aggregate-trend')).toHaveTextContent(
      '最高温度, 平均温度, 最低温度',
    );
    const next = within(cpu).getByRole('button', {
      name: 'CPU 温度 · 下一个范围',
    });
    fireEvent.click(next);
    expect(within(cpu).getByTestId('aggregate-trend')).toHaveTextContent(
      'CPU 1, CPU 2',
    );
    expect(within(cpu).getByTestId('aggregate-trend')).toHaveAttribute(
      'data-scope',
      'cpu:9007199254740993',
    );
    expect(
      within(cpu).getByRole('button', {
        name: '服务器1 · CPU 温度 · 历史趋势',
      }),
    ).toBeInTheDocument();
    expect(within(air).getByTestId('aggregate-trend')).toHaveAttribute(
      'data-scope',
      'airflow:overall',
    );
    fireEvent.click(next);
    expect(
      within(cpu).getByRole('button', {
        name: '服务器2 · CPU 温度 · 历史趋势',
      }),
    ).toBeInTheDocument();
    fireEvent.click(next);
    expect(within(cpu).getByTestId('aggregate-trend')).toHaveAttribute(
      'data-scope',
      'cpu:overall',
    );
    fireEvent.click(
      within(cpu).getByRole('button', { name: 'CPU 温度 · 上一个范围' }),
    );
    expect(within(cpu).getByTestId('aggregate-trend')).toHaveAttribute(
      'data-scope',
      'cpu:9007199254740994',
    );
  });
  it('进排气总体六条统计线，单机多探头独立并且仅进气虚线；键盘只作用于当前导航', () => {
    const servers = [
      thermalServer('1', '服务器1'),
      thermalServer('2', '服务器2'),
    ];
    render(thermalView(servers));
    const cpu = screen.getByRole('region', { name: 'CPU 温度' });
    const air = screen.getByRole('region', { name: '进气 / 排气温度' });
    let chart = within(air).getByTestId('aggregate-trend');
    expect(chart.textContent?.split(', ')).toHaveLength(6);
    expect(JSON.parse(chart.getAttribute('data-dashed') || '[]')).toHaveLength(
      3,
    );
    const navigation = within(air).getByRole('toolbar', {
      name: '进气 / 排气温度 · 切换服务器',
    });
    fireEvent.keyDown(navigation, { key: 'ArrowRight' });
    chart = within(air).getByTestId('aggregate-trend');
    expect(chart).toHaveTextContent(
      '01-Inlet Ambient, 27-Front Ambient, 05-Sys Exhaust, 06-Sys Exhaust',
    );
    expect(chart).not.toHaveTextContent('P/S');
    expect(JSON.parse(chart.getAttribute('data-dashed') || '[]')).toEqual(
      servers[0].history.slice(2, 4).map(sensorIdentity),
    );
    expect(
      within(air).getByRole('button', {
        name: '服务器1 · 进气 / 排气温度 · 历史趋势',
      }),
    ).toBeInTheDocument();
    expect(within(cpu).getByTestId('aggregate-trend')).toHaveAttribute(
      'data-scope',
      'cpu:overall',
    );
    fireEvent.keyDown(navigation, { key: 'ArrowRight', ctrlKey: true });
    fireEvent.keyDown(navigation, { key: 'ArrowRight', repeat: true });
    fireEvent.keyDown(
      within(air).getByRole('button', { name: '进气 / 排气温度 · 下一个范围' }),
      { key: 'ArrowRight' },
    );
    expect(chart).toHaveAttribute('data-scope', 'airflow:1');
    fireEvent.keyDown(navigation, { key: 'ArrowLeft' });
    expect(within(air).getByTestId('aggregate-trend')).toHaveAttribute(
      'data-scope',
      'airflow:overall',
    );
  });
  it('没有服务器时不能翻页，删除当前设备后回到总体并保持服务器ID匹配', () => {
    const one = thermalServer('1', '服务器1');
    const two = thermalServer('2', '服务器2');
    const mounted = render(thermalView([one, two]));
    const cpu = screen.getByRole('region', { name: 'CPU 温度' });
    fireEvent.click(
      within(cpu).getByRole('button', { name: 'CPU 温度 · 下一个范围' }),
    );
    mounted.rerender(thermalView([two, one]));
    expect(within(cpu).getByTestId('aggregate-trend')).toHaveAttribute(
      'data-scope',
      'cpu:1',
    );
    mounted.rerender(thermalView([two]));
    expect(within(cpu).getByTestId('aggregate-trend')).toHaveAttribute(
      'data-scope',
      'cpu:overall',
    );
    mounted.rerender(thermalView([]));
    expect(
      within(cpu).getByRole('button', { name: 'CPU 温度 · 下一个范围' }),
    ).toBeDisabled();
  });
});

function hardwareServer(
  id: string,
  name: string,
  supportsPercent = true,
): OverviewServerData {
  const row = thermalServer(id, name);
  const extras: MetricReading[] = [
    ['System power', 'system', 'power.watts', 'W', 240],
    ['Power capacity', 'system', 'power.capacity.watts', 'W', 800],
    ['PSU power', 'psu:1', 'power.watts', 'W', 99],
    ['Fan', 'fan:1', 'fan.rpm', 'RPM', 1200],
    ['Fan', 'fan:2', 'fan.rpm', 'RPM', 1800],
    ['Fan', 'fan:1', 'fan.percent', '%', supportsPercent ? 20 : null],
    ['Fan', 'fan:2', 'fan.percent', '%', supportsPercent ? 30 : null],
    ['31-HD Controller', 'temperature:31', 'temperature.celsius', '°C', 52],
    ['33-HD Controller', 'temperature:33', 'temperature.celsius', '°C', 54],
    ['32-HD Cntlr Zone', 'temperature:32', 'temperature.celsius', '°C', 90],
  ].map(([sensorName, sensorKey, metricCode, unit, value]) => ({
    sensorKey: String(sensorKey),
    name: String(sensorName),
    metricCode: String(metricCode),
    unit: String(unit),
    value: value === null ? null : Number(value),
    availability: value === null ? 'Unsupported' : 'Available',
    source: 'Device',
    health: null,
    deviceTime: null,
    observedAt: new Date(scopeNow).toISOString(),
  }));
  row.snapshot.telemetry.metrics.push(...extras);
  row.history.push(
    ...extras.map((metric) => ({
      sensorKey: metric.sensorKey,
      name: metric.name,
      metricCode: metric.metricCode,
      unit: metric.unit,
      points: [
        {
          time: new Date(scopeNow - 30000).toISOString(),
          average: metric.value,
          minimum: metric.value,
          maximum: metric.value,
          count: metric.value === null ? 0 : 1,
        },
      ],
    })),
  );
  return row;
}
const cardScopes = [
  ['CPU 温度', 'cpu'],
  ['整机功率', 'power'],
  ['风扇转速', 'fan:fanRpm'],
  ['阵列卡温度', 'array'],
  ['进气 / 排气温度', 'airflow'],
] as const;
const chartSeries = (card: HTMLElement): HistorySeriesDto[] =>
  JSON.parse(
    within(card).getByTestId('aggregate-trend').getAttribute('data-series') ||
      '[]',
  );

describe('五卡单机范围和真实实体明细', () => {
  afterEach(cleanup);
  it.each(
    cardScopes,
  )('%s独立正反向循环，不改变其余四卡；键盘保留修饰键与子按钮含义', (name, scope) => {
    render(
      thermalView([
        hardwareServer('1', '服务器1'),
        hardwareServer('2', '服务器2'),
      ]),
    );
    const card = screen.getByRole('region', { name });
    const chart = () => within(card).getByTestId('aggregate-trend');
    const navigation = within(card).getByRole('toolbar', {
      name: `${name} · 切换服务器`,
    });
    fireEvent.keyDown(navigation, { key: 'ArrowRight' });
    expect(chart()).toHaveAttribute('data-scope', `${scope}:1`);
    for (const [otherName, otherScope] of cardScopes)
      if (otherName !== name)
        expect(
          within(screen.getByRole('region', { name: otherName })).getByTestId(
            'aggregate-trend',
          ),
        ).toHaveAttribute('data-scope', `${otherScope}:overall`);
    fireEvent.keyDown(navigation, { key: 'ArrowRight', ctrlKey: true });
    fireEvent.keyDown(navigation, { key: 'ArrowRight', repeat: true });
    fireEvent.keyDown(
      within(card).getByRole('button', { name: `${name} · 下一个范围` }),
      { key: 'ArrowRight' },
    );
    expect(chart()).toHaveAttribute('data-scope', `${scope}:1`);
    fireEvent.click(
      within(card).getByRole('button', { name: `${name} · 下一个范围` }),
    );
    expect(chart()).toHaveAttribute('data-scope', `${scope}:2`);
    fireEvent.click(
      within(card).getByRole('button', { name: `${name} · 下一个范围` }),
    );
    expect(chart()).toHaveAttribute('data-scope', `${scope}:overall`);
    fireEvent.click(
      within(card).getByRole('button', { name: `${name} · 上一个范围` }),
    );
    expect(chart()).toHaveAttribute('data-scope', `${scope}:2`);
  });
  it('全部卡在无服务器时禁用翻页且键盘不改变总体范围', () => {
    render(thermalView([]));
    for (const [name, scope] of cardScopes) {
      const card = screen.getByRole('region', { name });
      expect(
        within(card).getByRole('button', { name: `${name} · 上一个范围` }),
      ).toBeDisabled();
      expect(
        within(card).getByRole('button', { name: `${name} · 下一个范围` }),
      ).toBeDisabled();
      fireEvent.keyDown(within(card).getByRole('toolbar'), {
        key: 'ArrowRight',
      });
      expect(within(card).getByTestId('aggregate-trend')).toHaveAttribute(
        'data-scope',
        `${scope}:overall`,
      );
    }
  });
  it('功率单机只显示当前服务器system曲线、统计和压力，搜索随服务器切换', () => {
    render(
      thermalView([
        hardwareServer('1', '服务器1'),
        hardwareServer('2', '服务器2'),
      ]),
    );
    const card = screen.getByRole('region', { name: '整机功率' });
    fireEvent.click(
      within(card).getByRole('button', { name: '整机功率 · 下一个范围' }),
    );
    expect(chartSeries(card).map(sensorIdentity)).toEqual([
      JSON.stringify(['system', 'power.watts', 'W']),
    ]);
    expect(
      within(card).getByRole('group', { name: '服务器1' }),
    ).toHaveTextContent('240 W / 800 W');
    expect(
      within(card).queryByRole('group', { name: '服务器2' }),
    ).not.toBeInTheDocument();
    expect(
      within(card).getByRole('button', { name: '整机功率 · 总运行功率' }),
    ).toHaveTextContent('240');
    expect(
      within(card).getByRole('button', {
        name: '服务器1 · 整机功率 · 历史趋势',
      }),
    ).toBeInTheDocument();
  });
  it('单机风扇按当前单位逐探头保留曲线，单位切换不改变服务器且不支持百分比不造值', () => {
    render(
      thermalView([
        hardwareServer('1', '服务器1'),
        hardwareServer('2', '服务器2', false),
      ]),
    );
    let card = screen.getByRole('region', { name: '风扇转速' });
    fireEvent.click(
      within(card).getByRole('button', { name: '风扇转速 · 下一个范围' }),
    );
    expect(chartSeries(card).map(sensorIdentity)).toEqual(
      ['fan:1', 'fan:2'].map((key) => JSON.stringify([key, 'fan.rpm', 'RPM'])),
    );
    expect(
      within(card).getByRole('button', { name: '风扇转速 · 平均转速' }),
    ).toHaveTextContent('1,500');
    expect(
      within(card).getByRole('button', {
        name: '服务器1 · 风扇转速 · 历史趋势',
      }),
    ).toBeInTheDocument();
    fireEvent.click(within(card).getByText('转速百分比（%）', { exact: true }));
    card = screen.getByRole('region', { name: '转速百分比（%）' });
    expect(chartSeries(card).map(sensorIdentity)).toEqual(
      ['fan:1', 'fan:2'].map((key) =>
        JSON.stringify([key, 'fan.percent', '%']),
      ),
    );
    expect(within(card).getByTestId('aggregate-trend')).toHaveAttribute(
      'data-scope',
      'fan:fanSpeedPercent:1',
    );
    expect(
      within(card).getByRole('button', {
        name: '服务器1 · 转速百分比（%） · 历史趋势',
      }),
    ).toBeInTheDocument();
    fireEvent.click(
      within(card).getByRole('button', {
        name: '转速百分比（%） · 下一个范围',
      }),
    );
    card = screen.getByRole('region', { name: '风扇转速' });
    expect(
      within(card).queryByText('转速百分比（%）', { exact: true }),
    ).not.toBeInTheDocument();
    expect(within(card).getByTestId('aggregate-trend')).toHaveAttribute(
      'data-scope',
      'fan:fanRpm:2',
    );
    expect(chartSeries(card)).toHaveLength(2);
  });
  it('阵列单机无库存仍逐控制器显示，明确排除CPU和周边区域，搜索不串到温度总图', () => {
    render(thermalView([hardwareServer('1', '服务器1')]));
    const card = screen.getByRole('region', { name: '阵列卡温度' });
    fireEvent.click(
      within(card).getByRole('button', { name: '阵列卡温度 · 下一个范围' }),
    );
    const series = chartSeries(card);
    expect(series.map((item) => item.name)).toEqual([
      '31-HD Controller',
      '33-HD Controller',
    ]);
    expect(series.map((item) => item.sensorKey)).toEqual([
      'temperature:31',
      'temperature:33',
    ]);
    expect(
      within(card).getByRole('button', { name: '阵列卡温度 · 平均温度' }),
    ).toHaveTextContent('53');
    expect(
      within(card).getByRole('button', {
        name: '服务器1 · 阵列卡温度 · 历史趋势',
      }),
    ).toBeInTheDocument();
  });
  it('有库存时卡温严格按真实控制器关联，快照与历史重复描述不会使映射失效', () => {
    const row = hardwareServer('1', '服务器1');
    row.inventory = {
      collectedAt: row.snapshot.publishedAt,
      identity: row.snapshot.server.identity,
      warnings: [],
      components: [
        {
          key: '/StorageControllers/RAID.1',
          kind: 'StorageController',
          name: 'RAID Controller',
          model: 'PERC H730P Mini',
          manufacturer: null,
          serialNumber: null,
          status: null,
          capacityBytes: null,
          attributes: {},
          children: [],
        },
      ],
    };
    const sensor = row.snapshot.telemetry.metrics.find(
      (metric) => metric.sensorKey === 'temperature:31',
    );
    const historical = row.history.find(
      (series) => series.sensorKey === 'temperature:31',
    );
    if (!sensor || !historical) throw new Error('测试夹具缺少控制器探头');
    sensor.name = historical.name = 'Controller Temperature';
    sensor.sensorKey = historical.sensorKey =
      'temperature:/StorageControllers/RAID.1';
    render(thermalView([row]));
    const card = screen.getByRole('region', { name: '阵列卡温度' });
    fireEvent.click(
      within(card).getByRole('button', { name: '阵列卡温度 · 下一个范围' }),
    );
    expect(
      chartSeries(card).map((series) => [series.sensorKey, series.name]),
    ).toEqual([['temperature:/StorageControllers/RAID.1', 'PERC H730P Mini']]);
  });
});
