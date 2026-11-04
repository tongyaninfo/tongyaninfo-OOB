import {
  Alert,
  Card,
  Col,
  Empty,
  Row,
  Space,
  Statistic,
  Tag,
  Typography,
} from 'antd';
import React from 'react';
import type { MetricReading, TelemetryDto } from '@/services/oob/models';
import {
  formatReading,
  formatTime,
  metricDisplayName,
  readingCodeLabel,
} from '../format';
import { useOobIntl } from '../intl';
import ManagedTable from './ManagedTable';

/** 同一风扇只消费后端统一百分比与 RPM；缺测不换算，不补零。 */
export default function TelemetryPanel({
  telemetry,
}: {
  telemetry: TelemetryDto;
}) {
  const t = useOobIntl();
  const metrics = telemetry.metrics.filter(
    (metric) =>
      metric.metricCode !== 'fan.percent' ||
      !['Unsupported', 'NotInstalled'].includes(metric.availability),
  );
  if (!metrics.length)
    return (
      <Empty
        description={
          <>
            <div>{t('oob.noData')}</div>
            <Typography.Text type="secondary">
              {t('oob.noData.help')}
            </Typography.Text>
          </>
        }
      />
    );
  return (
    <Space orientation="vertical" size="large" style={{ width: '100%' }}>
      {telemetry.stale && (
        <Alert showIcon type="warning" title={t('oob.stale')} />
      )}
      <Typography.Text type="secondary">
        {t('oob.telemetry.help')} · {t('oob.sampled')}：
        {formatTime(telemetry.collectedAt)}
      </Typography.Text>
      <Row gutter={[16, 16]}>
        {metrics
          .filter((metric) => metric.metricCode.startsWith('power.'))
          .map((metric) => (
            <Col
              xs={24}
              sm={12}
              xl={6}
              key={metric.sensorKey + metric.metricCode}
            >
              <Card size="small">
                <Statistic
                  title={metric.name}
                  value={metric.value ?? '—'}
                  suffix={metric.unit}
                  formatter={() => formatReading(metric.value)}
                />
                <Tag
                  color={
                    metric.availability === 'Available' ? 'success' : 'default'
                  }
                >
                  {readingCodeLabel('availability', metric.availability, t)}
                </Tag>
                <Typography.Text type="secondary">
                  {readingCodeLabel('source', metric.source, t)}
                </Typography.Text>
              </Card>
            </Col>
          ))}
      </Row>
      <ManagedTable<MetricReading>
        tableId="oob.telemetry.v1"
        rowKey={(metric) => `${metric.sensorKey}|${metric.metricCode}`}
        dataSource={metrics}
        scroll={{ x: 1000 }}
        pagination={{ defaultPageSize: 20, showSizeChanger: true }}
        columns={[
          {
            title: t('oob.sensor'),
            dataIndex: 'name',
            fixed: 'left',
            render: (_, metric) => metricDisplayName(metric, t),
          },
          { title: t('oob.metric'), dataIndex: 'metricCode' },
          {
            title: t('oob.value'),
            key: 'value',
            render: (_, metric) => formatReading(metric.value, metric.unit),
          },
          {
            title: t('oob.quality'),
            dataIndex: 'availability',
            render: (value: string) => (
              <Tag color={value === 'Available' ? 'success' : 'default'}>
                {readingCodeLabel('availability', value, t)}
              </Tag>
            ),
          },
          {
            title: t('oob.health'),
            dataIndex: 'health',
            render: (value) => value || '—',
          },
          {
            title: t('oob.source'),
            dataIndex: 'source',
            render: (value: string) => readingCodeLabel('source', value, t),
          },
          {
            title: t('oob.observedAt'),
            dataIndex: 'observedAt',
            render: (value) => formatTime(value),
          },
          {
            title: t('oob.deviceTime'),
            dataIndex: 'deviceTime',
            render: (value) => formatTime(value),
          },
        ]}
      />
    </Space>
  );
}
