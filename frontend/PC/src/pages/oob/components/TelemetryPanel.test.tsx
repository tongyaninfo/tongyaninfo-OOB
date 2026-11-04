import { render, screen } from '@testing-library/react';
import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import type { MetricReading, TelemetryDto } from '@/services/oob/models';

vi.mock('../intl', () => ({
  useOobIntl: () => (key: string) =>
    key === 'oob.monitor.fanDetails.speedPercent' ? '转速百分比（%）' : key,
}));
vi.mock('./ManagedTable', () => ({
  default: ({
    dataSource,
    columns,
  }: {
    dataSource: MetricReading[];
    columns: {
      dataIndex?: keyof MetricReading;
      render?: (value: unknown, metric: MetricReading) => React.ReactNode;
    }[];
  }) => (
    <div>
      {dataSource.map((metric) => (
        <div key={metric.metricCode}>
          {columns.map((column, index) => {
            const value = column.dataIndex
              ? metric[column.dataIndex]
              : undefined;
            return (
              <span key={column.dataIndex || index}>
                {column.render
                  ? column.render(value, metric)
                  : String(value ?? '')}
              </span>
            );
          })}
        </div>
      ))}
    </div>
  ),
}));

import TelemetryPanel from './TelemetryPanel';

function metric(
  metricCode: string,
  name: string,
  value: number | null,
): MetricReading {
  return {
    sensorKey: 'fan:1',
    metricCode,
    name,
    value,
    unit: metricCode === 'fan.rpm' ? 'RPM' : '%',
    availability: 'Available',
    source: 'Device',
    health: 'OK',
    observedAt: '2026-10-25T00:00:00Z',
    deviceTime: null,
  };
}

describe('实时明细的风扇展示', () => {
  it('功率与能耗卡片和明细表统一最多两位小数', () => {
    const metrics = [
      { ...metric('power.watts', '功率', 123.456789), unit: 'W' },
      { ...metric('power.energy.kwh', '累计能耗', 1.236789), unit: 'kWh' },
    ];
    const { container } = render(
      <TelemetryPanel
        telemetry={{
          collectedAt: '2026-10-25T00:00:00Z',
          stale: false,
          warnings: [],
          metrics,
        }}
      />,
    );
    expect(
      [...container.querySelectorAll('.ant-statistic-content-value')].map(
        (element) => element.textContent,
      ),
    ).toEqual(['123.46', '1.24']);
    expect(screen.getByText('123.46 W')).toBeInTheDocument();
    expect(screen.getByText('1.24 kWh')).toBeInTheDocument();
    expect(metrics[0].value).toBe(123.456789);
    expect(metrics[1].value).toBe(1.236789);
  });

  it('统一百分比仅显示一个指标和物理风扇名称', () => {
    const telemetry: TelemetryDto = {
      collectedAt: '2026-10-25T00:00:00Z',
      stale: false,
      warnings: [],
      metrics: [
        metric('fan.rpm', 'Fan 1转速', 5280),
        metric('fan.percent', 'Fan 1转速百分比', 24),
      ],
    };
    render(<TelemetryPanel telemetry={telemetry} />);
    expect(screen.getByText('5,280 RPM')).toBeInTheDocument();
    expect(screen.getByText('24 %')).toBeInTheDocument();
    expect(screen.getByText('Fan 1 · 转速百分比（%）')).toBeInTheDocument();
    expect(screen.getByText('fan.percent')).toBeInTheDocument();
    expect(screen.queryByText(/PWM/)).not.toBeInTheDocument();
    expect(telemetry.metrics[1].name).toBe('Fan 1转速百分比');
  });
  it('HP百分比真实0和暂时缺测保留，不给Dell增加不支持的百分比空行', () => {
    const reading = metric('fan.percent', 'Fan 1转速百分比', 0);
    const telemetry: TelemetryDto = {
      collectedAt: '2026-10-25T00:00:00Z',
      stale: false,
      warnings: [],
      metrics: [reading],
    };
    const view = render(<TelemetryPanel telemetry={telemetry} />);
    expect(screen.getByText('0 %')).toBeInTheDocument();
    view.rerender(
      <TelemetryPanel
        telemetry={{
          ...telemetry,
          metrics: [
            { ...reading, value: null, availability: 'TemporarilyUnavailable' },
          ],
        }}
      />,
    );
    expect(screen.getByText('fan.percent')).toBeInTheDocument();
    expect(
      screen.getByText('oob.availability.TemporarilyUnavailable'),
    ).toBeInTheDocument();
    view.rerender(
      <TelemetryPanel
        telemetry={{
          ...telemetry,
          metrics: [{ ...reading, value: null, availability: 'Unsupported' }],
        }}
      />,
    );
    expect(screen.queryByText('fan.percent')).not.toBeInTheDocument();
  });
});
