import {
  DashboardOutlined,
  FireOutlined,
  InfoCircleOutlined,
  LeftOutlined,
  RightOutlined,
  ThunderboltOutlined,
} from '@ant-design/icons';
import { Button, Card, Popover, Progress, Segmented, Typography } from 'antd';
import React, { useState } from 'react';
import type { HistorySeriesDto } from '@/services/oob/models';
import { formatReading, formatTime } from '../format';
import { useOobIntl } from '../intl';
import useStyles from './AggregateDashboard.style';
import { aggregateDashboard } from './aggregate';
import type {
  AggregatePanel,
  AggregateReading,
  AggregateReadingState,
  AggregateResult,
  AggregateStat,
  PowerPressureRow,
} from './aggregate.types';
import { controllerTemperatures } from './controllers';
import { cpuTemperatureLabels } from './cpus';
import HistoryTrendDialog from './HistoryTrendDialog';
import { classifyMetric, sensorIdentity } from './metrics';
import type { OverviewServerData } from './overviewData.types';
import TrendChart from './TrendChart';

const titleKeys = {
  cpu: 'oob.aggregate.cpu',
  array: 'oob.aggregate.array',
  inlet: 'oob.aggregate.inlet',
  exhaust: 'oob.aggregate.exhaust',
  fanRpm: 'oob.aggregate.fan',
  fanSpeedPercent: 'oob.aggregate.speedPercent',
  power: 'oob.aggregate.power',
} as const;
const stateKeys: Record<
  AggregateReadingState,
  `oob.aggregate.state.${AggregateReadingState}`
> = {
  available: 'oob.aggregate.state.available',
  missing: 'oob.aggregate.state.missing',
  unsupported: 'oob.aggregate.state.unsupported',
  stale: 'oob.aggregate.state.stale',
  offline: 'oob.aggregate.state.offline',
  disabled: 'oob.aggregate.state.disabled',
  ambiguous: 'oob.aggregate.state.ambiguous',
  invalid: 'oob.aggregate.state.invalid',
};

function isStat(value: string): value is AggregateStat {
  return ['average', 'minimum', 'maximum', 'total'].includes(value);
}

/** 原始实体读数按服务器组织；排除值仍在明细中说明，不因不参与均值而消失。 */
function ReadingDetails({
  panel,
  stat,
}: {
  panel: AggregatePanel;
  stat: AggregateStat;
}) {
  const t = useOobIntl();
  const { styles } = useStyles();
  const groups = new Map<
    string,
    { name: string; readings: AggregateReading[] }
  >();
  for (const reading of panel.readings) {
    const group = groups.get(reading.serverId) || {
      name: reading.serverName,
      readings: [],
    };
    group.readings.push(reading);
    groups.set(reading.serverId, group);
  }
  return (
    <div className={styles.details}>
      {groups.size === 0 && <span>{t('oob.aggregate.noDetails')}</span>}
      {[...groups.entries()].map(([serverId, group]) => (
        <section
          className={styles.server}
          key={serverId}
          aria-label={group.name}
        >
          <h4 className={styles.serverName}>{group.name}</h4>
          <dl className={styles.readings}>
            {group.readings.map((reading) => {
              const label =
                panel.kind === 'cpu' && reading.slot
                  ? t('oob.monitor.cpuSlot', { number: reading.slot })
                  : reading.label;
              const extreme =
                (stat === 'minimum' || stat === 'maximum') &&
                reading.state === 'available' &&
                reading.value !== null &&
                reading.value === panel.stats[stat];
              return (
                <div key={reading.key} className={styles.reading}>
                  <dt className={styles.readingName}>
                    {label}
                    {reading.model && reading.model !== label && (
                      <span className={styles.readingModel}>
                        {reading.model}
                      </span>
                    )}
                  </dt>
                  <dd className={styles.readingValue}>
                    {reading.value === null
                      ? '-'
                      : formatReading(reading.value, reading.unit)}
                    {extreme && (
                      <span className={styles.source}>
                        {t(`oob.aggregate.${stat}`)}
                      </span>
                    )}
                  </dd>
                  <dd className={styles.readingMeta}>
                    {t('oob.aggregate.sampledAt')}：
                    {reading.observedAt ? formatTime(reading.observedAt) : '-'}
                    <br />
                    <span
                      className={
                        reading.state === 'available' ? '' : styles.excluded
                      }
                    >
                      {t(stateKeys[reading.state])}
                      {reading.state !== 'available' &&
                        ` · ${t('oob.aggregate.excluded')}`}
                    </span>
                    {reading.health && reading.health !== 'OK' && (
                      <>
                        {' · '}
                        {t('oob.aggregate.health')}：{reading.health}
                      </>
                    )}
                  </dd>
                </div>
              );
            })}
          </dl>
        </section>
      ))}
    </div>
  );
}

/** 一台服务器只用自己的整机功率和可用容量计算压力；缺测时保留行但不画零压力。 */
function PowerPressureRows({ rows }: { rows: PowerPressureRow[] }) {
  const t = useOobIntl();
  const { styles } = useStyles();
  if (rows.length === 0) return null;
  return (
    <div className={styles.pressureSection}>
      <div className={styles.pressureHeading}>
        <Typography.Text strong>
          {t('oob.aggregate.powerPressure')}
        </Typography.Text>
        <Typography.Text type="secondary">
          {t('oob.monitor.power')} / {t('oob.aggregate.powerCapacity')}
        </Typography.Text>
      </div>
      <div className={styles.pressureRows}>
        {rows.map((row) => {
          const pressureText =
            row.percent === null
              ? t('oob.aggregate.powerPressureUnavailable')
              : t('oob.aggregate.powerPressureValue', {
                  percent: formatReading(row.percent),
                });
          return (
            <fieldset key={row.serverId} className={styles.pressureRow}>
              <legend className={styles.pressureLegend}>
                {row.serverName}
              </legend>
              <div className={styles.pressureRowHeading}>
                <Typography.Text strong className={styles.pressureServerName}>
                  {row.serverName}
                </Typography.Text>
                <span className={styles.pressurePowerValues}>
                  {formatReading(row.usedWatts, 'W')} /{' '}
                  {formatReading(row.capacityWatts, 'W')}
                </span>
              </div>
              <div className={styles.pressureBarLine}>
                {row.percent === null ? (
                  <span
                    className={styles.pressureUnavailableBar}
                    aria-hidden="true"
                  />
                ) : (
                  <Progress
                    className={styles.pressureBar}
                    size="small"
                    showInfo={false}
                    percent={Math.min(100, Math.max(0, row.percent))}
                    status={row.percent > 100 ? 'exception' : undefined}
                    aria-label={`${row.serverName} ${t('oob.aggregate.powerPressure')}`}
                    aria-valuetext={pressureText}
                  />
                )}
                <span className={styles.pressurePercent}>{pressureText}</span>
              </div>
              {row.reason && (
                <Typography.Text
                  type="secondary"
                  className={styles.pressureReason}
                >
                  {row.reason === 'capacity'
                    ? t('oob.aggregate.capacityUnavailable')
                    : `${t('oob.aggregate.powerUnavailable')} · ${t(stateKeys[row.powerState])}`}
                </Typography.Text>
              )}
            </fieldset>
          );
        })}
      </div>
    </div>
  );
}

function statLabel(
  panel: AggregatePanel,
  stat: AggregateStat,
  t: ReturnType<typeof useOobIntl>,
) {
  if (panel.unit === '°C')
    return t(
      stat === 'average'
        ? 'oob.aggregate.averageTemperature'
        : stat === 'minimum'
          ? 'oob.aggregate.minimumTemperature'
          : 'oob.aggregate.maximumTemperature',
    );
  if (panel.kind === 'fanRpm')
    return t(
      stat === 'average'
        ? 'oob.aggregate.averageRpm'
        : stat === 'minimum'
          ? 'oob.aggregate.minimumRpm'
          : 'oob.aggregate.maximumRpm',
    );
  if (panel.kind === 'power') {
    if (stat === 'total')
      return t(
        panel.validCount < panel.totalCount
          ? 'oob.aggregate.availableTotal'
          : 'oob.aggregate.total',
      );
    return t(
      stat === 'average'
        ? 'oob.aggregate.averagePower'
        : stat === 'minimum'
          ? 'oob.aggregate.minimumPower'
          : 'oob.aggregate.maximumPower',
    );
  }
  return t(`oob.aggregate.${stat}`);
}

/** 温度与其他面板共用同一明细、精度和排除原因，双气流分组不混算。 */
function PanelStatistics({
  panel,
  coverageText,
}: {
  panel: AggregatePanel;
  coverageText?: string;
}) {
  const t = useOobIntl();
  const { styles } = useStyles();
  const title = t(titleKeys[panel.kind]);
  const stats: AggregateStat[] =
    panel.kind === 'array'
      ? ['average', 'maximum']
      : panel.kind === 'power'
        ? ['total', 'average', 'minimum', 'maximum']
        : panel.unit === '°C'
          ? ['maximum', 'average', 'minimum']
          : ['average', 'minimum', 'maximum'];
  return (
    <>
      <div
        className={`${styles.metrics} ${panel.kind === 'power' ? styles.powerMetrics : ''}`}
      >
        {stats.map((stat) => {
          const value = panel.stats[stat];
          const label = statLabel(panel, stat, t);
          const sources =
            stat === 'minimum' || stat === 'maximum'
              ? [
                  ...new Set(
                    panel.readings
                      .filter(
                        (reading) =>
                          reading.state === 'available' &&
                          reading.value !== null &&
                          reading.value === value,
                      )
                      .map((reading) => reading.serverName),
                  ),
                ]
              : [];
          return (
            <Popover
              key={stat}
              placement="right"
              trigger={['hover', 'focus', 'click']}
              title={`${title} · ${label} · ${t('oob.aggregate.details')}`}
              content={<ReadingDetails panel={panel} stat={stat} />}
            >
              <button
                type="button"
                className={styles.metric}
                aria-label={`${title} · ${label}`}
              >
                <span className={styles.metricLabel}>
                  {label} <InfoCircleOutlined aria-hidden="true" />
                </span>
                <span className={styles.value}>
                  {value === null ? '-' : formatReading(value)}
                  {value !== null && (
                    <span className={styles.unit}>{panel.unit}</span>
                  )}
                </span>
                {sources.length > 0 && (
                  <span className={styles.source}>
                    {t('oob.aggregate.extremeSource', {
                      servers: sources.join(', '),
                    })}
                  </span>
                )}
              </button>
            </Popover>
          );
        })}
      </div>
      <div className={styles.coverage}>
        {coverageText ||
          t('oob.aggregate.coverage', {
            valid: panel.validCount,
            total: panel.totalCount,
          })}
      </div>
    </>
  );
}

function aggregateSeries(
  panel: AggregatePanel,
  t: ReturnType<typeof useOobIntl>,
  prefix = '',
): HistorySeriesDto[] {
  return panel.series
    .filter((series) => isStat(series.name))
    .map((series) => ({
      ...series,
      // 图例名称不随覆盖数量变化，避免同一曲线来回换色。
      name:
        prefix +
        (series.name === 'total'
          ? t('oob.aggregate.total')
          : statLabel(panel, series.name as AggregateStat, t)),
    }));
}

/** 数字与趋势使用同一聚合结果；搜索入口可以独立跟随当前卡片的服务器范围。 */
function AggregateCard({
  panel,
  loading,
  children,
  coverageText,
  servers,
  title: customTitle,
  history,
  series,
  scopeKey,
  additionalPanel,
  dashedSeries,
}: {
  panel: AggregatePanel;
  loading: boolean;
  children?: React.ReactNode;
  coverageText?: string;
  servers: OverviewServerData[];
  title?: string;
  history?: React.ReactNode;
  series?: HistorySeriesDto[];
  scopeKey?: string;
  additionalPanel?: AggregatePanel;
  dashedSeries?: readonly string[];
}) {
  const t = useOobIntl();
  const { styles } = useStyles();
  const title = customTitle || t(titleKeys[panel.kind]);
  const icon =
    panel.kind === 'power' ? (
      <ThunderboltOutlined className={styles.icon} />
    ) : panel.kind.startsWith('fan') ? (
      <DashboardOutlined className={styles.icon} />
    ) : (
      <FireOutlined className={styles.icon} />
    );
  return (
    <section
      aria-label={title}
      className={additionalPanel ? styles.airflowCard : undefined}
    >
      <Card
        className={styles.card}
        size="small"
        loading={loading}
        extra={
          history || (
            <HistoryTrendDialog
              servers={servers}
              aggregateKind={panel.kind}
              kind={
                panel.kind === 'power'
                  ? 'power'
                  : panel.kind.startsWith('fan')
                    ? 'fans'
                    : 'temperatures'
              }
            />
          )
        }
        title={
          <span className={styles.heading}>
            {icon}
            {title}
          </span>
        }
      >
        {children}
        {additionalPanel ? (
          <div className={styles.airflowStatistics}>
            {[panel, additionalPanel].map((item) => (
              <div key={item.kind}>
                <Typography.Text strong>
                  {t(titleKeys[item.kind])}
                </Typography.Text>
                <PanelStatistics panel={item} />
              </div>
            ))}
          </div>
        ) : (
          <PanelStatistics panel={panel} coverageText={coverageText} />
        )}
        <TrendChart
          scopeKey={scopeKey || `aggregate:${panel.kind}`}
          series={series || aggregateSeries(panel, t)}
          dashedSeries={dashedSeries}
          height={190}
          unit={panel.unit}
          title={t('oob.aggregate.trend')}
          pointDescription={(time) => {
            const coverage = panel.coverage[time];
            return !series && coverage
              ? t('oob.aggregate.coverage', {
                  valid: coverage.validCount,
                  total: coverage.totalCount,
                })
              : undefined;
          }}
        />
      </Card>
    </section>
  );
}

/** 以稳定服务器 ID 持有每张卡自己的范围，设备列表调整后安全回到总体。 */
function useCardScope(servers: OverviewServerData[]) {
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const row = servers.find(
    (server) => server.snapshot.server.id === selectedId,
  );
  const current = row ? servers.indexOf(row) + 1 : 0;
  const move = (direction: number) => {
    const next =
      (current + direction + servers.length + 1) % (servers.length + 1);
    setSelectedId(next === 0 ? null : servers[next - 1].snapshot.server.id);
  };
  return { row, move };
}

function CardScopeNavigation({
  title,
  row,
  move,
  disabled,
}: {
  title: string;
  row?: OverviewServerData;
  move: (direction: number) => void;
  disabled: boolean;
}) {
  const t = useOobIntl();
  const { styles } = useStyles();
  return (
    <div
      className={styles.scopeNavigation}
      role="toolbar"
      // biome-ignore lint/a11y/noNoninteractiveTabindex: 独立卡片导航需可聚焦接收左右键，子按钮保持各自键义。
      tabIndex={0}
      aria-label={`${title} · ${t('oob.aggregate.scopeNavigation')}`}
      onKeyDown={(event) => {
        if (
          event.target !== event.currentTarget ||
          disabled ||
          event.repeat ||
          event.nativeEvent.isComposing ||
          event.ctrlKey ||
          event.altKey ||
          event.metaKey ||
          event.shiftKey
        )
          return;
        if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
        event.preventDefault();
        event.stopPropagation();
        move(event.key === 'ArrowLeft' ? -1 : 1);
      }}
    >
      <Button
        type="text"
        size="small"
        icon={<LeftOutlined />}
        disabled={disabled}
        aria-label={`${title} · ${t('oob.aggregate.scopePrevious')}`}
        onClick={() => move(-1)}
      />
      <span className={styles.scopeName} aria-live="polite">
        {row?.snapshot.server.name || t('oob.aggregate.overall')}
      </span>
      <Button
        type="text"
        size="small"
        icon={<RightOutlined />}
        disabled={disabled}
        aria-label={`${title} · ${t('oob.aggregate.scopeNext')}`}
        onClick={() => move(1)}
      />
    </div>
  );
}

function CpuCard({
  data,
  loading,
  servers,
  now,
}: {
  data: AggregateResult;
  loading: boolean;
  servers: OverviewServerData[];
  now: number;
}) {
  const t = useOobIntl();
  const { row, move } = useCardScope(servers);
  const panel = row ? aggregateDashboard([row], now).cpu : data.cpu;
  const labels = row
    ? cpuTemperatureLabels(
        row.inventory,
        [...row.snapshot.telemetry.metrics, ...row.history].filter(
          (sensor) => classifyMetric(sensor) === 'cpu',
        ),
        (number) => t('oob.monitor.cpuSlot', { number }),
      )
    : undefined;
  const series = row?.history
    .filter((sensor) => classifyMetric(sensor) === 'cpu')
    .map((sensor) => ({
      ...sensor,
      name: labels?.get(sensorIdentity(sensor))?.legend || sensor.name,
    }));
  return (
    <AggregateCard
      panel={panel}
      loading={loading}
      servers={servers}
      series={series}
      scopeKey={`cpu:${row?.snapshot.server.id || 'overall'}`}
      history={
        row ? (
          <HistoryTrendDialog
            kind="cpu"
            serverId={row.snapshot.server.id}
            serverName={row.snapshot.server.name}
            inventory={row.inventory}
          />
        ) : (
          <HistoryTrendDialog
            kind="cpu"
            servers={servers}
            aggregateKind="cpu"
          />
        )
      }
    >
      <CardScopeNavigation
        title={t('oob.aggregate.cpu')}
        row={row}
        move={move}
        disabled={loading || servers.length === 0}
      />
    </AggregateCard>
  );
}

function AirflowCard({
  data,
  loading,
  servers,
  now,
}: {
  data: AggregateResult;
  loading: boolean;
  servers: OverviewServerData[];
  now: number;
}) {
  const t = useOobIntl();
  const { row, move } = useCardScope(servers);
  const selected = row ? aggregateDashboard([row], now) : data;
  const inlet = row
    ? row.history.filter((sensor) => classifyMetric(sensor) === 'inlet')
    : aggregateSeries(selected.inlet, t, `${t('oob.aggregate.inlet')} · `);
  const exhaust = row
    ? row.history.filter((sensor) => classifyMetric(sensor) === 'exhaust')
    : aggregateSeries(selected.exhaust, t, `${t('oob.aggregate.exhaust')} · `);
  return (
    <AggregateCard
      panel={selected.inlet}
      additionalPanel={selected.exhaust}
      loading={loading}
      servers={servers}
      title={t('oob.aggregate.airflow')}
      series={[...inlet, ...exhaust]}
      dashedSeries={inlet.map(sensorIdentity)}
      scopeKey={`airflow:${row?.snapshot.server.id || 'overall'}`}
      history={
        row ? (
          <HistoryTrendDialog
            kind="airflow"
            serverId={row.snapshot.server.id}
            serverName={row.snapshot.server.name}
            inventory={row.inventory}
          />
        ) : (
          <HistoryTrendDialog
            kind="airflow"
            servers={servers}
            aggregateKind="inlet"
          />
        )
      }
    >
      <CardScopeNavigation
        title={t('oob.aggregate.airflow')}
        row={row}
        move={move}
        disabled={loading || servers.length === 0}
      />
    </AggregateCard>
  );
}

/** 单机曲线只采用对应实体；不能把CPU、区域温度或电源槽功率带入卡片。 */
function deviceSeries(
  row: OverviewServerData,
  kind: 'power' | 'array',
): HistorySeriesDto[] {
  if (kind === 'power')
    return row.history.filter(
      (sensor) => classifyMetric(sensor) === 'power' && sensor.unit === 'W',
    );
  // 近期历史与当前快照描述同一个探头时先按身份去重，避免误判为多个卡温探头。
  const descriptors = [
    ...new Map(
      [...row.history, ...row.snapshot.telemetry.metrics].map((sensor) => [
        sensorIdentity(sensor),
        sensor,
      ]),
    ).values(),
  ];
  const labels = new Map(
    controllerTemperatures(row.inventory, descriptors).flatMap((controller) =>
      controller.temperature
        ? [
            [
              sensorIdentity(controller.temperature),
              controller.model === '-' ? controller.name : controller.model,
            ] as const,
          ]
        : [],
    ),
  );
  return row.history
    .filter((sensor) => labels.has(sensorIdentity(sensor)))
    .map((sensor) => ({
      ...sensor,
      name: labels.get(sensorIdentity(sensor)) || sensor.name,
    }));
}

function DeviceMetricCard({
  data,
  loading,
  servers,
  now,
  kind,
}: {
  data: AggregateResult;
  loading: boolean;
  servers: OverviewServerData[];
  now: number;
  kind: 'power' | 'array';
}) {
  const t = useOobIntl();
  const { row, move } = useCardScope(servers);
  const panel = row ? aggregateDashboard([row], now)[kind] : data[kind];
  return (
    <AggregateCard
      panel={panel}
      loading={loading}
      servers={servers}
      series={row ? deviceSeries(row, kind) : undefined}
      scopeKey={`${kind}:${row?.snapshot.server.id || 'overall'}`}
      history={
        row ? (
          <HistoryTrendDialog
            kind={kind}
            serverId={row.snapshot.server.id}
            serverName={row.snapshot.server.name}
            inventory={row.inventory}
          />
        ) : (
          <HistoryTrendDialog
            kind={kind === 'power' ? 'power' : 'temperatures'}
            servers={servers}
            aggregateKind={kind}
          />
        )
      }
    >
      <CardScopeNavigation
        title={t(titleKeys[kind])}
        row={row}
        move={move}
        disabled={loading || servers.length === 0}
      />
      {kind === 'power' && (
        <PowerPressureRows rows={panel.pressureRows || []} />
      )}
    </AggregateCard>
  );
}

function FanCard({
  data,
  loading,
  servers,
  now,
}: {
  data: AggregateResult;
  loading: boolean;
  servers: OverviewServerData[];
  now: number;
}) {
  const t = useOobIntl();
  const { styles } = useStyles();
  const [selected, setSelected] = useState<'fanRpm' | 'fanSpeedPercent'>(
    'fanRpm',
  );
  const { row, move } = useCardScope(servers);
  const scopedData = row ? aggregateDashboard([row], now) : data;
  // 已发现的设备能力在暂时缺测时仍可查看；不为不支持百分比的设备增加空视图。
  const hasData = (panel: AggregatePanel) =>
    panel.validCount > 0 ||
    panel.readings.some(
      (reading) =>
        reading.observedAt &&
        reading.availability &&
        !['Unsupported', 'NotInstalled'].includes(reading.availability),
    ) ||
    panel.series.some((series) =>
      series.points.some((point) => point.count > 0 && point.average !== null),
    );
  const options = [
    { label: 'RPM', value: 'fanRpm' },
    ...(hasData(scopedData.fanSpeedPercent)
      ? [{ label: t('oob.aggregate.speedPercent'), value: 'fanSpeedPercent' }]
      : []),
  ];
  const kind = options.some((option) => option.value === selected)
    ? selected
    : 'fanRpm';
  return (
    <AggregateCard
      panel={scopedData[kind]}
      servers={servers}
      loading={loading}
      series={row?.history.filter(
        (sensor) =>
          sensor.metricCode ===
            (kind === 'fanRpm' ? 'fan.rpm' : 'fan.percent') &&
          sensor.unit === scopedData[kind].unit,
      )}
      scopeKey={`fan:${kind}:${row?.snapshot.server.id || 'overall'}`}
      history={
        row ? (
          <HistoryTrendDialog
            kind={kind === 'fanRpm' ? 'fanRpmDetails' : 'fanPercentDetails'}
            serverId={row.snapshot.server.id}
            serverName={row.snapshot.server.name}
            inventory={row.inventory}
          />
        ) : (
          <HistoryTrendDialog
            kind="fans"
            servers={servers}
            aggregateKind={kind}
          />
        )
      }
      coverageText={t('oob.aggregate.fanCoverage', {
        rpm: scopedData.fanRpm.validCount,
        speedPercent: scopedData.fanSpeedPercent.validCount,
        total: scopedData.fanRpm.totalCount,
      })}
    >
      <CardScopeNavigation
        title={t(titleKeys[kind])}
        row={row}
        move={move}
        disabled={loading || servers.length === 0}
      />
      {options.length > 1 && (
        <Segmented
          className={styles.fanUnit}
          aria-label={t('oob.aggregate.fanUnits')}
          value={kind}
          options={options}
          onChange={(value) =>
            setSelected(value as 'fanRpm' | 'fanSpeedPercent')
          }
        />
      )}
    </AggregateCard>
  );
}

/** 五张图共享父级快照；只有点击搜索入口才查询所选区间的本地历史。 */
export default function AggregateDashboard({
  data,
  loading,
  servers,
  now = Date.now(),
}: {
  data: AggregateResult;
  loading: boolean;
  servers: OverviewServerData[];
  now?: number;
}) {
  const t = useOobIntl();
  const { styles } = useStyles();
  return (
    <div>
      <Typography.Text className={styles.hint}>
        {t('oob.aggregate.currentHint')}
      </Typography.Text>
      <div className={styles.grid}>
        <CpuCard data={data} loading={loading} servers={servers} now={now} />
        <DeviceMetricCard
          kind="power"
          data={data}
          loading={loading}
          servers={servers}
          now={now}
        />
        <FanCard data={data} loading={loading} servers={servers} now={now} />
        <DeviceMetricCard
          kind="array"
          data={data}
          loading={loading}
          servers={servers}
          now={now}
        />
        <AirflowCard
          data={data}
          loading={loading}
          servers={servers}
          now={now}
        />
      </div>
    </div>
  );
}
