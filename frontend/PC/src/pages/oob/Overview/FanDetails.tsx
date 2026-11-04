import { Button, Modal, Space, Tooltip, Typography } from 'antd';
import React, { useState } from 'react';
import type { MetricReading } from '@/services/oob/models';
import ManagedTable from '../components/ManagedTable';
import {
  formatReading,
  formatTime,
  metricDisplayName,
  readingCodeLabel,
} from '../format';
import { useOobIntl } from '../intl';
import { type FanDetailReading, summarizeFans } from './fans';
import { availableValue } from './metrics';

/** 单位与指标同时有效才显示数值，不将厂商缺少的另一种读数补成零。 */
function readingNumber(metric: MetricReading | undefined, unit: string) {
  return metric?.unit === unit ? availableValue(metric) : null;
}

function byName(left: FanDetailReading, right: FanDetailReading) {
  return (
    left.name.localeCompare(right.name, undefined, { numeric: true }) ||
    left.sensorKey.localeCompare(right.sensorKey)
  );
}

function byReading(
  left: FanDetailReading,
  right: FanDetailReading,
  key: 'rpm' | 'speedPercent',
  unit: string,
) {
  const a = readingNumber(left[key], unit);
  const b = readingNumber(right[key], unit);
  return a === b
    ? byName(left, right)
    : a === null
      ? 1
      : b === null
        ? -1
        : a - b;
}

/** 明细只消费父层推送快照，弹窗开关不查询设备、补拉数据或触发任何硬件动作。 */
export default function FanDetails({
  serverId,
  serverName,
  metrics,
}: {
  serverId: string;
  serverName: string;
  metrics: MetricReading[];
}) {
  const t = useOobIntl();
  const [open, setOpen] = useState(false);
  const summary = summarizeFans(metrics);
  const fans = summary.fans;
  const reading = (metric: MetricReading | undefined, unit: string) => {
    const value = readingNumber(metric, unit);
    const quality = metric
      ? readingCodeLabel('availability', metric.availability, t)
      : t('oob.noData');
    return (
      <Tooltip
        title={[
          metric ? metricDisplayName(metric, t) : undefined,
          quality,
          metric?.health,
        ]
          .filter(Boolean)
          .join(' · ')}
      >
        <span>{value === null ? '-' : formatReading(value)}</span>
      </Tooltip>
    );
  };
  const observed = (fan: FanDetailReading) => {
    const times = [fan.rpm?.observedAt, fan.speedPercent?.observedAt].filter(
      (time): time is string =>
        Boolean(time) && Number.isFinite(Date.parse(time || '')),
    );
    return times.sort((a, b) => Date.parse(b) - Date.parse(a))[0];
  };
  return (
    <>
      <Button
        type="link"
        size="small"
        style={{ padding: 0 }}
        aria-label={t('oob.monitor.fanDetails.label', { name: serverName })}
        onClick={() => setOpen(true)}
      >
        {t('oob.monitor.fanDetails')}
      </Button>
      <Modal
        open={open}
        title={t('oob.monitor.fanDetails.title', { name: serverName })}
        width={1040}
        destroyOnHidden
        onCancel={() => setOpen(false)}
        footer={
          <Button
            aria-label={t('oob.monitor.fanDetails.close')}
            onClick={() => setOpen(false)}
          >
            {t('oob.monitor.fanDetails.close')}
          </Button>
        }
      >
        <Typography.Paragraph type="secondary" style={{ fontSize: 12 }}>
          {t('oob.monitor.fanDetails.help')}
        </Typography.Paragraph>
        <ManagedTable<FanDetailReading>
          tableId={`oob.overview.fans.v1:${serverId}`}
          rowKey="sensorKey"
          size="small"
          dataSource={fans}
          pagination={false}
          scroll={{ x: 940, y: 360 }}
          locale={{ emptyText: t('oob.noData') }}
          columns={[
            {
              title: t('oob.monitor.fanDetails.name'),
              dataIndex: 'name',
              width: 210,
              fixed: 'left',
              sorter: byName,
              defaultSortOrder: 'ascend',
              render: (name: string) => (
                <span style={{ overflowWrap: 'anywhere' }}>{name}</span>
              ),
            },
            {
              title: t('oob.monitor.fanDetails.rpm'),
              key: 'rpm',
              width: 110,
              sorter: (a, b) => byReading(a, b, 'rpm', 'RPM'),
              render: (_, fan) => reading(fan.rpm, 'RPM'),
            },
            {
              title: t('oob.monitor.fanDetails.speedPercent'),
              key: 'speedPercent',
              width: 210,
              sorter: (a, b) => byReading(a, b, 'speedPercent', '%'),
              render: (_, fan) => reading(fan.speedPercent, '%'),
            },
            {
              title: t('oob.monitor.fanDetails.health'),
              key: 'health',
              width: 140,
              render: (_, fan) => {
                const rows = [fan.rpm, fan.speedPercent].filter(
                  (metric): metric is MetricReading => metric !== undefined,
                );
                const health = [
                  ...new Set(
                    rows.map((metric) => metric.health).filter(Boolean),
                  ),
                ];
                return (
                  <Space orientation="vertical" size={0}>
                    <span>{health.join(' / ') || '-'}</span>
                    {rows.every(
                      (metric) => readingNumber(metric, metric.unit) === null,
                    ) && (
                      <Typography.Text
                        type="secondary"
                        style={{ fontSize: 12 }}
                      >
                        {[
                          ...new Set(
                            rows.map((metric) =>
                              readingCodeLabel(
                                'availability',
                                metric.availability,
                                t,
                              ),
                            ),
                          ),
                        ].join(' / ')}
                      </Typography.Text>
                    )}
                  </Space>
                );
              },
            },
            {
              title: t('oob.observedAt'),
              key: 'observedAt',
              width: 180,
              sorter: (a, b) =>
                (Date.parse(observed(a) || '') || 0) -
                  (Date.parse(observed(b) || '') || 0) || byName(a, b),
              render: (_, fan) =>
                observed(fan) ? formatTime(observed(fan)) : '-',
            },
          ]}
        />
      </Modal>
    </>
  );
}
