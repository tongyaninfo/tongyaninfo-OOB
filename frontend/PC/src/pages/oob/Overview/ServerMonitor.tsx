import {
  DashboardOutlined,
  FireOutlined,
  ThunderboltOutlined,
} from '@ant-design/icons';
import { Link } from '@umijs/max';
import { Alert, Card, Space, Tag, Tooltip, Typography } from 'antd';
import React from 'react';
import type {
  DeviceInventory,
  HistorySeriesDto,
  MetricReading,
  ServerTelemetryDto,
} from '@/services/oob/models';
import ServerStatus from '../components/ServerStatus';
import { formatReading, formatTime, readingCodeLabel } from '../format';
import { useOobIntl } from '../intl';
import { controllerTemperatures } from './controllers';
import { cpuTemperatureLabels } from './cpus';
import FanDetails from './FanDetails';
import { averageFanHistory, summarizeFans } from './fans';
import HistoryTrendDialog from './HistoryTrendDialog';
import {
  availableValue,
  classifyMetric,
  isSnapshotStale,
  sensorIdentity,
} from './metrics';
import useStyles from './ServerMonitor.style';
import TrendChart from './TrendChart';

/** 缺测统一显示短横线；型号/读数质量通过原始指标提示保留，不将百分比当作RPM。 */
function CurrentReading({
  label,
  metric,
  value,
  unit,
}: {
  label: string;
  metric?: MetricReading;
  value?: number | null;
  unit?: string;
}) {
  const t = useOobIntl();
  const { styles } = useStyles();
  const reading =
    value !== undefined ? value : metric ? availableValue(metric) : null;
  const health =
    metric?.health && metric.health !== 'OK'
      ? t('oob.monitor.health', { health: metric.health })
      : null;
  const quality =
    metric && reading === null
      ? readingCodeLabel('availability', metric.availability, t)
      : null;
  return (
    <Tooltip
      title={[metric?.name, quality, health].filter(Boolean).join(' · ')}
    >
      <dl className={styles.reading} aria-label={label}>
        <dt className={styles.readingName}>{label}</dt>
        <dd className={styles.readingValue}>
          {reading === null ? '-' : formatReading(reading)}
          {reading !== null && (
            <span className={styles.unit}>{unit || metric?.unit}</span>
          )}
          {health && <div className={styles.notice}>{health}</div>}
        </dd>
      </dl>
    </Tooltip>
  );
}

export type ServerMonitorProps = {
  snapshot: ServerTelemetryDto;
  inventory?: DeviceInventory | null;
  history: HistorySeriesDto[];
  loading: boolean;
  liveDisconnected: boolean;
  now: number;
};

/** 温度合并展示，阵列卡逐卡显示库存型号；风扇总览只显示均值，具体读数在实时弹窗。 */
export default function ServerMonitor({
  snapshot,
  inventory,
  history,
  loading,
  liveDisconnected,
  now,
}: ServerMonitorProps) {
  const t = useOobIntl();
  const { styles } = useStyles();
  const { server, telemetry } = snapshot;
  const stale = isSnapshotStale(snapshot, now);
  const metrics = telemetry.metrics;
  const cpuMetrics = metrics.filter(
    (metric) => classifyMetric(metric) === 'cpu',
  );
  const cpuLabels = cpuTemperatureLabels(
    inventory,
    [
      ...cpuMetrics,
      ...history.filter((series) => classifyMetric(series) === 'cpu'),
    ],
    (number) => t('oob.monitor.cpuSlot', { number }),
  );
  const cpu = [...cpuMetrics].sort(
    (left, right) =>
      (cpuLabels.get(sensorIdentity(left))?.order || 0) -
      (cpuLabels.get(sensorIdentity(right))?.order || 0),
  );
  const controllers = controllerTemperatures(inventory, metrics);
  const temperatureLabels = new Map<string, string>([
    ...[...cpuLabels].map(([key, value]) => [key, value.legend] as const),
    ...controllers.flatMap((controller) =>
      controller.temperature
        ? [
            [
              sensorIdentity(controller.temperature),
              controller.model === '-' ? controller.name : controller.model,
            ] as const,
          ]
        : [],
    ),
  ]);
  const temperatures = history
    .filter(
      (series) =>
        classifyMetric(series) === 'cpu' ||
        temperatureLabels.has(sensorIdentity(series)),
    )
    .map((series) => ({
      ...series,
      name: temperatureLabels.get(sensorIdentity(series)) || series.name,
    }));
  const fans = summarizeFans(metrics);
  // 两类控制器消费同一百分比模型，RPM 保留独立单位和缺测状态。
  const fanHistory = averageFanHistory(history, {
    rpm: t('oob.monitor.averageRpm'),
    speedPercent: t('oob.monitor.averageSpeedPercent'),
  });
  const visibleFanHistory = fanHistory.filter((series) =>
    series.points.some((point) => point.count > 0 && point.average !== null),
  );
  const power = metrics.find((metric) => classifyMetric(metric) === 'power');
  return (
    <Card
      className={styles.card}
      title={
        <div>
          <div className={styles.heading}>
            <Link
              className={styles.serverName}
              to={`/oob/servers/${server.id}`}
            >
              {server.name}
            </Link>
            <ServerStatus server={server} />
            {(stale ||
              server.connectionStatus !== 'Online' ||
              !server.enabled) && (
              <Tag color="warning">{t('oob.monitor.lastKnown')}</Tag>
            )}
            {stale && <Tag color="warning">{t('oob.stale')}</Tag>}
          </div>
          <div className={styles.metadata}>
            <Space wrap size={[12, 4]}>
              <span>
                {server.identity.model || server.controllerType} ·{' '}
                {server.controllerType}
              </span>
              <span>
                {t('oob.sampled')}：{formatTime(telemetry.collectedAt)}
              </span>
              <span>
                {t('oob.monitor.interval', {
                  seconds: server.samplingIntervalSeconds,
                })}
              </span>
            </Space>
          </div>
        </div>
      }
      extra={<Link to={`/oob/servers/${server.id}`}>{t('oob.details')}</Link>}
    >
      {liveDisconnected && (
        <Alert
          type="warning"
          showIcon
          title={t('oob.monitor.disconnected')}
          style={{ marginBottom: 12 }}
        />
      )}
      <div className={styles.grid}>
        <section
          className={`${styles.panel} ${styles.temperatures}`}
          aria-label={`${server.name} ${t('oob.monitor.temperatures')}`}
        >
          <div className={styles.panelTitle}>
            <FireOutlined className={styles.icon} />
            <span>{t('oob.monitor.temperatures')}</span>
            <span className={styles.panelActions}>
              <HistoryTrendDialog
                serverId={server.id}
                serverName={server.name}
                kind="temperatures"
                inventory={inventory}
              />
            </span>
          </div>
          <div className={styles.temperatureReadings}>
            {cpu.length ? (
              cpu.map((metric) => (
                <CurrentReading
                  label={
                    cpuLabels.get(sensorIdentity(metric))?.label ||
                    t('oob.monitor.cpu')
                  }
                  metric={metric}
                  key={sensorIdentity(metric)}
                />
              ))
            ) : (
              <CurrentReading label={t('oob.monitor.cpu')} />
            )}
            {controllers.length ? (
              controllers.map((controller) => (
                <CurrentReading
                  key={controller.key}
                  label={
                    controller.model === '-'
                      ? controller.name
                      : controller.model
                  }
                  metric={controller.temperature}
                />
              ))
            ) : (
              <CurrentReading label={t('oob.monitor.array')} />
            )}
          </div>
          <TrendChart
            scopeKey={`${server.id}:temperatures`}
            series={temperatures}
            unit="°C"
          />
        </section>
        <section
          className={styles.panel}
          aria-label={`${server.name} ${t('oob.monitor.fans')}`}
        >
          <div className={styles.panelTitle}>
            <DashboardOutlined className={styles.icon} />
            <span>{t('oob.monitor.fans')}</span>
            <span className={styles.panelActions}>
              <FanDetails
                serverId={server.id}
                serverName={server.name}
                metrics={metrics}
              />
              <HistoryTrendDialog
                serverId={server.id}
                serverName={server.name}
                kind="fans"
                inventory={inventory}
              />
            </span>
          </div>
          <div className={styles.averageReadings}>
            <CurrentReading
              label={t('oob.monitor.averageRpm')}
              value={fans.averageRpm}
              unit="RPM"
            />
            <CurrentReading
              label={t('oob.monitor.averageSpeedPercent')}
              value={fans.averageSpeedPercent}
              unit="%"
            />
          </div>
          <Typography.Text className={styles.notice} type="secondary">
            {t('oob.monitor.fanAverageCount', {
              rpm: fans.rpmCount,
              speedPercent: fans.speedPercentCount,
              total: fans.totalFans,
            })}
          </Typography.Text>
          <Typography.Text type="secondary" className={styles.notice}>
            {t('oob.sampled')}：{formatTime(telemetry.collectedAt)}
          </Typography.Text>
          <Tooltip title={t('oob.monitor.percentHelp')}>
            <Typography.Text type="secondary" className={styles.notice}>
              {t('oob.monitor.fanBoth')}
            </Typography.Text>
          </Tooltip>
          {visibleFanHistory.length ? (
            visibleFanHistory.map((series) => (
              <TrendChart
                key={series.metricCode}
                scopeKey={`${server.id}:fans:${series.metricCode}`}
                title={
                  series.unit === '%'
                    ? `${t('oob.monitor.recent')} · ${series.name}`
                    : t('oob.monitor.recent')
                }
                series={[series]}
                unit={series.unit}
              />
            ))
          ) : (
            <TrendChart
              scopeKey={`${server.id}:fans:empty`}
              series={[]}
              unit="RPM / %"
            />
          )}
        </section>
        <section
          className={styles.panel}
          aria-label={`${server.name} ${t('oob.monitor.power')}`}
        >
          <div className={styles.panelTitle}>
            <ThunderboltOutlined className={styles.icon} />
            <span>{t('oob.monitor.power')}</span>
            <span className={styles.panelActions}>
              <HistoryTrendDialog
                serverId={server.id}
                serverName={server.name}
                kind="power"
                inventory={inventory}
              />
            </span>
          </div>
          <CurrentReading label={t('oob.monitor.power')} metric={power} />
          {['power.capacity.watts', 'power.peak.watts'].map((code) => {
            const metric = metrics.find(
              (item) => item.metricCode === code && item.sensorKey === 'system',
            );
            const value = metric ? availableValue(metric) : null;
            return (
              <div className={styles.notice} key={code}>
                {t(
                  code === 'power.capacity.watts'
                    ? 'oob.monitor.capacity'
                    : 'oob.monitor.peak',
                )}
                ：{value === null ? '-' : formatReading(value, metric?.unit)}
              </div>
            );
          })}
          <TrendChart
            scopeKey={`${server.id}:power`}
            series={history.filter(
              (series) => classifyMetric(series) === 'power',
            )}
            unit="W"
          />
        </section>
      </div>
      {loading && (
        <Typography.Paragraph
          type="secondary"
          style={{ marginTop: 8, marginBottom: 0 }}
        >
          {t('oob.monitor.recent')}…
        </Typography.Paragraph>
      )}
    </Card>
  );
}
