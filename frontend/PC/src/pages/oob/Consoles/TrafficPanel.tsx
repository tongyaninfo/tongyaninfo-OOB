import { Card, Space, Statistic, Typography } from 'antd';
import React from 'react';
import { useOobIntl } from '../intl';
import useStyles from './style';
import { formatBytes, type TrafficPoint } from './traffic';

/** 图中仅绘制连续实测速率，缺测不会连接补零。 */
export default function TrafficPanel({
  points,
  mediaPoints = [],
}: {
  points: TrafficPoint[];
  mediaPoints?: TrafficPoint[];
}) {
  const t = useOobIntl();
  const { styles } = useStyles();
  const last = points.at(-1);
  const media = mediaPoints.at(-1);
  const maximum = Math.max(
    1,
    ...points.flatMap((point) => [point.receiveRate || 0, point.sendRate || 0]),
  );
  const paths = (field: 'receiveRate' | 'sendRate') => {
    const segments: string[] = [];
    let current = '';
    for (const point of points) {
      if (point[field] === null) {
        if (current) segments.push(current);
        current = '';
        continue;
      }
      const x =
        points.length < 2
          ? 0
          : ((point.at - points[0].at) /
              Math.max(1000, (points.at(-1)?.at || point.at) - points[0].at)) *
            600;
      const y = 57 - ((point[field] || 0) / maximum) * 54;
      current += `${current ? ' L' : 'M'}${x.toFixed(1)},${y.toFixed(1)}`;
    }
    if (current) segments.push(current);
    return segments;
  };
  return (
    <Card size="small" title={t('oob.console.traffic')}>
      <div className={styles.traffic}>
        <Statistic
          title={t('oob.console.receiveRate')}
          value={formatBytes(last?.receiveRate, true)}
        />
        <Statistic
          title={t('oob.console.sendRate')}
          value={formatBytes(last?.sendRate, true)}
        />
        <Statistic
          title={t('oob.console.received')}
          value={formatBytes(last?.received)}
        />
        <Statistic
          title={t('oob.console.sent')}
          value={formatBytes(last?.sent)}
        />
      </div>
      <div
        className={styles.chart}
        role="img"
        aria-label={t('oob.console.trafficTrend')}
      >
        <svg viewBox="0 0 600 60" preserveAspectRatio="none">
          <title>{t('oob.console.trafficTrend')}</title>
          {paths('receiveRate').map((path) => (
            <path
              key={path}
              d={path}
              className={styles.ingress}
              stroke="currentColor"
              strokeWidth="2"
              fill="none"
            />
          ))}
          {paths('sendRate').map((path) => (
            <path
              key={path}
              d={path}
              className={styles.egress}
              stroke="currentColor"
              strokeWidth="2"
              fill="none"
            />
          ))}
        </svg>
        <Space>
          <span className={styles.ingress}>
            ↓ {t('oob.console.receiveRate')}
          </span>
          <span className={styles.egress}>↑ {t('oob.console.sendRate')}</span>
        </Space>
      </div>
      <Typography.Text type="secondary">
        {t('oob.console.trafficHint')}
      </Typography.Text>
      {mediaPoints.length > 0 && (
        <>
          <Typography.Paragraph strong style={{ margin: '12px 0 8px' }}>
            {t('oob.console.mediaChannel')}
          </Typography.Paragraph>
          <div className={styles.traffic}>
            <Statistic
              title={t('oob.console.receiveRate')}
              value={formatBytes(media?.receiveRate, true)}
            />
            <Statistic
              title={t('oob.console.sendRate')}
              value={formatBytes(media?.sendRate, true)}
            />
            <Statistic
              title={t('oob.console.received')}
              value={formatBytes(media?.received)}
            />
            <Statistic
              title={t('oob.console.sent')}
              value={formatBytes(media?.sent)}
            />
          </div>
        </>
      )}
    </Card>
  );
}
