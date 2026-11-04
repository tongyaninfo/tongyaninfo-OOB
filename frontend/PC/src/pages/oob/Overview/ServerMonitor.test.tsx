import {
  cleanup,
  fireEvent,
  render,
  screen,
  within,
} from '@testing-library/react';
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type {
  DeviceInventory,
  HistorySeriesDto,
  MetricReading,
  ServerTelemetryDto,
} from '@/services/oob/models';

vi.mock('@umijs/max', () => ({
  Link: ({
    to,
    children,
    className,
  }: {
    to: string;
    children: React.ReactNode;
    className?: string;
  }) => (
    <a href={to} className={className}>
      {children}
    </a>
  ),
  useModel: () => ({ initialState: { settings: { navTheme: 'light' } } }),
}));
vi.mock('../intl', () => ({
  useOobIntl: () => (key: string, values?: { number?: string }) =>
    key === 'oob.monitor.cpuSlot' ? `CPU ${values?.number}` : key,
}));
vi.mock('@ant-design/plots', async (importOriginal) => ({
  G2: (await importOriginal<typeof import('@ant-design/plots')>()).G2,
  Line: ({
    data,
    connectNulls,
    scale,
    legend,
  }: {
    data: unknown[];
    connectNulls: boolean;
    scale: unknown;
    legend: unknown;
  }) => (
    <output
      data-testid="monitor-chart"
      data-connect-nulls={String(connectNulls)}
      data-color-scale={JSON.stringify(scale)}
      data-native-legend={String(legend)}
    >
      {JSON.stringify(data)}
    </output>
  ),
}));

import { G2 } from '@ant-design/plots';
import ServerMonitor from './ServerMonitor';

function reading(
  name: string,
  metricCode: string,
  value: number | null,
  unit: string,
  availability = 'Available',
): MetricReading {
  return {
    sensorKey: name,
    name,
    metricCode,
    value,
    unit,
    availability,
    source: 'Device',
    health: null,
    observedAt: '2026-10-24T00:00:00Z',
    deviceTime: null,
  };
}
function snapshot(metrics: MetricReading[]): ServerTelemetryDto {
  return {
    server: {
      id: '9007199254740993',
      name: 'HP test',
      brand: 'HP',
      controllerType: 'iLO4',
      address: 'https://example.invalid/',
      username: 'placeholder',
      samplingIntervalSeconds: 30,
      enabled: true,
      certificateFingerprint: null,
      ignoreCertificateErrors: true,
      identity: {
        model: 'DL360p Gen8',
        manufacturer: null,
        serialNumber: null,
        hostName: null,
        controllerHostName: null,
        controllerType: null,
        firmwareVersion: null,
        powerState: null,
      },
      connectionStatus: 'Online',
      lastCollectedAt: '2026-10-24T00:00:00Z',
      lastInventoryAt: null,
      lastError: null,
      createdAt: '2026-10-24T00:00:00Z',
      updatedAt: '2026-10-24T00:00:00Z',
    },
    telemetry: {
      collectedAt: '2026-10-24T00:00:00Z',
      stale: false,
      metrics,
      warnings: [],
    },
    publishedAt: '2026-10-24T00:00:00Z',
  };
}
const now = Date.parse('2026-10-24T00:00:30Z');

describe('服务器监控卡真实单位和缺测展示', () => {
  afterEach(cleanup);

  it('HP统一百分比按原值显示，缺少RPM仍显示短横线', () => {
    const metrics = [
      reading('CPU 1', 'temperature.celsius', 40, '°C'),
      reading('CPU 2', 'temperature.celsius', 41, '°C'),
    ];
    for (let index = 1; index <= 8; index++) {
      metrics.push({
        ...reading(`Fan ${index}`, 'fan.percent', 39, '%'),
        sensorKey: `fan:${index}`,
      });
      metrics.push({
        ...reading(`Fan ${index} RPM`, 'fan.rpm', null, 'RPM', 'Unsupported'),
        sensorKey: `fan:${index}`,
      });
    }
    render(
      <ServerMonitor
        snapshot={snapshot(metrics)}
        history={[]}
        loading={false}
        liveDisconnected={false}
        now={now}
      />,
    );
    const cpu = screen.getByRole('region', {
      name: 'HP test oob.monitor.temperatures',
    });
    expect(within(cpu).getByText('CPU 1')).toBeInTheDocument();
    expect(within(cpu).getByText('CPU 2')).toBeInTheDocument();
    const fans = screen.getByRole('region', {
      name: 'HP test oob.monitor.fans',
    });
    expect(
      within(fans).getByText('oob.monitor.averageRpm'),
    ).toBeInTheDocument();
    expect(
      within(fans).getByText('oob.monitor.averageSpeedPercent'),
    ).toBeInTheDocument();
    expect(
      within(fans).getByLabelText('oob.monitor.averageSpeedPercent'),
    ).toHaveTextContent('39%');
    expect(
      within(fans).queryByLabelText('oob.monitor.averagePwm'),
    ).not.toBeInTheDocument();
    expect(
      within(within(fans).getByLabelText('oob.monitor.averageRpm')).getByText(
        '-',
      ),
    ).toBeInTheDocument();
    expect(within(fans).queryByText('Fan 1')).not.toBeInTheDocument();
    expect(within(fans).queryByText('Fan 1 RPM')).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'HP test' })).toHaveAttribute(
      'href',
      '/oob/servers/9007199254740993',
    );
  });

  it('Dell与HP使用相同百分比标题和模型', () => {
    render(
      <ServerMonitor
        snapshot={snapshot([
          reading('Fan 1', 'fan.rpm', 5280, 'RPM'),
          reading('Fan 1', 'fan.percent', 24, '%'),
        ])}
        history={[]}
        loading={false}
        liveDisconnected={false}
        now={now}
      />,
    );
    const fans = screen.getByRole('region', {
      name: 'HP test oob.monitor.fans',
    });
    expect(
      within(fans).getByLabelText('oob.monitor.averageSpeedPercent'),
    ).toHaveTextContent('24%');
    expect(within(fans).queryByText(/PWM/)).not.toBeInTheDocument();
  });

  it('只有阵列区域时卡本体温度显示短横线，不展示区域冒充卡温，并保留真实0W', () => {
    const data = snapshot([
      reading('32-HD Cntlr Zone', 'temperature.celsius', 33, '°C'),
      reading('Power', 'power.watts', 0, 'W'),
    ]);
    data.telemetry.metrics[1].sensorKey = 'system';
    render(
      <ServerMonitor
        snapshot={data}
        history={[]}
        loading={false}
        liveDisconnected={false}
        now={now}
      />,
    );
    const array = screen.getByRole('region', {
      name: 'HP test oob.monitor.temperatures',
    });
    expect(within(array).getByText('oob.monitor.array')).toBeInTheDocument();
    expect(within(array).getAllByText('-')).toHaveLength(3);
    expect(
      within(array).queryByText('32-HD Cntlr Zone'),
    ).not.toBeInTheDocument();
    const power = screen.getByRole('region', {
      name: 'HP test oob.monitor.power',
    });
    expect(within(power).getByText('0')).toBeInTheDocument();
  });

  it('多块阵列卡直接显示各自型号，明确匹配的温度独立显示，缺测卡保留短横线', () => {
    const metrics = [
      reading('PERC H730P Controller', 'temperature.celsius', 51, '°C'),
    ];
    const inventory: DeviceInventory = {
      identity: snapshot([]).server.identity,
      collectedAt: '2026-10-24T00:00:00Z',
      warnings: [],
      components: ['PERC H730P', 'Smart Array P420i'].map((model, index) => ({
        key: `controller:${index}`,
        kind: 'StorageController',
        name: model,
        model,
        manufacturer: null,
        serialNumber: null,
        status: null,
        capacityBytes: null,
        attributes: {},
        children: [],
      })),
    };
    render(
      <ServerMonitor
        snapshot={snapshot(metrics)}
        inventory={inventory}
        history={[]}
        loading={false}
        liveDisconnected={false}
        now={now}
      />,
    );
    const temperatures = screen.getByRole('region', {
      name: 'HP test oob.monitor.temperatures',
    });
    expect(within(temperatures).getByText('PERC H730P')).toBeInTheDocument();
    expect(
      within(temperatures).getByText('Smart Array P420i'),
    ).toBeInTheDocument();
    expect(within(temperatures).getByText('51')).toBeInTheDocument();
    expect(within(temperatures).getAllByText('-').length).toBeGreaterThan(0);
  });

  it('断开及过期保留上次读数，近期断线空值传到图表且禁止跨空值连线', () => {
    const metrics = [reading('CPU 1', 'temperature.celsius', 40, '°C')];
    const history: HistorySeriesDto[] = [
      {
        sensorKey: 'CPU 1',
        name: 'CPU 1',
        metricCode: 'temperature.celsius',
        unit: '°C',
        points: [
          {
            time: '2026-10-24T00:00:00Z',
            average: 40,
            minimum: 40,
            maximum: 40,
            count: 1,
          },
          {
            time: '2026-10-24T00:00:30Z',
            average: null,
            minimum: null,
            maximum: null,
            count: 0,
          },
        ],
      },
    ];
    render(
      <ServerMonitor
        snapshot={snapshot(metrics)}
        history={history}
        loading={false}
        liveDisconnected
        now={now + 100_000}
      />,
    );
    expect(screen.getByText('oob.monitor.disconnected')).toBeInTheDocument();
    expect(screen.getByText('oob.stale')).toBeInTheDocument();
    expect(screen.getByText('oob.monitor.lastKnown')).toBeInTheDocument();
    const chart = screen.getByTestId('monitor-chart');
    expect(chart).toHaveAttribute('data-connect-nulls', 'false');
    expect(chart).toHaveTextContent('"value":null');
    expect(screen.getByText('40')).toBeInTheDocument();
  });
});

it('CPU主标签显示库存型号，同型号双CPU按槽号排序、图例独立且Tooltip保留原探头', async () => {
  const model = 'Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz';
  const first = {
    ...reading('02-CPU 1', 'temperature.celsius', 40, '°C'),
    sensorKey: 'temperature:2',
  };
  const second = {
    ...reading('03-CPU 2', 'temperature.celsius', 41, '°C'),
    sensorKey: 'temperature:3',
  };
  const saved: DeviceInventory = {
    identity: snapshot([]).server.identity,
    collectedAt: '2026-10-24T00:00:00Z',
    warnings: [],
    components: [2, 1].map((slot) => ({
      key: `/redfish/v1/Systems/1/Processors/${slot}/`,
      name: 'Processors',
      kind: 'Processor',
      model: ` ${model} `,
      manufacturer: null,
      serialNumber: null,
      status: 'OK',
      capacityBytes: null,
      attributes: { Socket: `Proc ${slot}` },
      children: [],
    })),
  };
  const history: HistorySeriesDto[] = [second, first].map((metric) => ({
    sensorKey: metric.sensorKey,
    metricCode: metric.metricCode,
    name: metric.name,
    unit: metric.unit,
    points: [
      {
        time: metric.observedAt,
        average: metric.value,
        minimum: metric.value,
        maximum: metric.value,
        count: 1,
      },
    ],
  }));
  render(
    <ServerMonitor
      snapshot={snapshot([second, first])}
      inventory={saved}
      history={history}
      loading={false}
      liveDisconnected={false}
      now={now}
    />,
  );
  const panel = screen.getByRole('region', {
    name: 'HP test oob.monitor.temperatures',
  });
  const readings = within(panel).getAllByLabelText(model);
  expect(readings).toHaveLength(2);
  expect(within(readings[0]).getByText('40')).toBeInTheDocument();
  expect(within(readings[1]).getByText('41')).toBeInTheDocument();
  expect(within(panel).queryByText('02-CPU 1')).not.toBeInTheDocument();
  const points = JSON.parse(
    screen.getByTestId('monitor-chart').textContent || '[]',
  ) as Array<{ series: string; value: number }>;
  expect(points).toEqual(
    expect.arrayContaining([
      {
        time: Date.parse(first.observedAt),
        value: 40,
        series: `${model} (CPU 1)`,
        dashed: false,
      },
      {
        time: Date.parse(second.observedAt),
        value: 41,
        series: `${model} (CPU 2)`,
        dashed: false,
      },
    ]),
  );
  expect(new Set(points.map((point) => point.series)).size).toBe(2);
  const legend = screen.getByRole('list', { name: 'oob.monitor.legend' });
  expect(
    within(legend)
      .getAllByRole('listitem')
      .map((item) => item.textContent),
  ).toEqual([`${model} (CPU 1)`, `${model} (CPU 2)`]);
  expect(within(legend).getAllByRole('button', { pressed: true })).toHaveLength(
    2,
  );
  expect(points.every((point) => !point.series.includes('temperature:'))).toBe(
    true,
  );
  fireEvent.mouseOver(readings[0]);
  expect(await screen.findByRole('tooltip')).toHaveTextContent('02-CPU 1');
});

it('长型号及全部温度序列的完整图例同时可见，顺序变化后颜色仍与真实图表尺度一致', () => {
  const cpuModel = 'Intel(R) Xeon(R) CPU E5-2696 v2 @ 2.50GHz';
  const metrics = [
    reading('CPU 1', 'temperature.celsius', 40, '°C'),
    reading('CPU 2', 'temperature.celsius', 41, '°C'),
    reading('PERC H730P Mini Controller', 'temperature.celsius', 52, '°C'),
  ];
  const history: HistorySeriesDto[] = metrics.map((metric) => ({
    sensorKey: metric.sensorKey,
    name: metric.name,
    metricCode: metric.metricCode,
    unit: metric.unit,
    points: [
      {
        time: metric.observedAt,
        average: metric.value,
        minimum: metric.value,
        maximum: metric.value,
        count: 1,
      },
    ],
  }));
  const inventory: DeviceInventory = {
    identity: snapshot([]).server.identity,
    collectedAt: metrics[0].observedAt,
    warnings: [],
    components: [1, 2, 3].map((slot) => ({
      key: slot < 3 ? `/Processors/${slot}` : 'controller:H730P',
      kind: slot < 3 ? 'Processor' : 'StorageController',
      name: slot < 3 ? `CPU ${slot}` : 'PERC H730P Mini',
      model: slot < 3 ? cpuModel : 'PERC H730P Mini',
      manufacturer: null,
      serialNumber: null,
      status: 'OK',
      capacityBytes: null,
      attributes: { Socket: slot < 3 ? `Proc ${slot}` : null },
      children: [],
    })),
  };
  const props = {
    snapshot: snapshot(metrics),
    inventory,
    history,
    loading: false,
    liveDisconnected: false,
    now,
  };
  const { rerender } = render(<ServerMonitor {...props} />);
  const verify = () => {
    const legend = screen.getByRole('list', { name: 'oob.monitor.legend' });
    const items = within(legend).getAllByRole('listitem');
    expect(items.map((item) => item.textContent)).toEqual([
      `${cpuModel} (CPU 1)`,
      `${cpuModel} (CPU 2)`,
      'PERC H730P Mini',
    ]);
    const chart = screen.getByTestId('monitor-chart');
    expect(chart).toHaveAttribute('data-native-legend', 'false');
    const { color } = JSON.parse(
      chart.getAttribute('data-color-scale') || '{}',
    ) as { color: { domain: string[]; range: string[] } };
    const ordinal = G2.stdlib()['scale.ordinal'](color);
    for (const item of items) {
      expect(item).toBeVisible();
      expect(item.querySelector('[aria-hidden="true"]')).toHaveStyle({
        borderTopColor: ordinal.map(item.textContent),
        borderTopStyle: 'solid',
      });
    }
    expect(
      within(legend).getAllByRole('button', { pressed: true }),
    ).toHaveLength(items.length);
    return items.map((item) =>
      item.querySelector('[aria-hidden="true"]')?.getAttribute('style'),
    );
  };
  const colors = verify();
  rerender(<ServerMonitor {...props} history={[...history].reverse()} />);
  expect(verify()).toEqual(colors);
});
