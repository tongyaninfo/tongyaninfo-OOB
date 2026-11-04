import { SearchOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Empty,
  Modal,
  Space,
  Spin,
  Tooltip,
  Typography,
} from 'antd';
import React, { useState } from 'react';
import { getHistory } from '@/services/oob/api';
import type { DeviceInventory } from '@/services/oob/models';
import { formatTime } from '../format';
import { useOobIntl } from '../intl';
import { aggregateHistory } from './aggregate';
import type {
  AggregateKind,
  AggregatePanel,
  AggregateStat,
} from './aggregate.types';
import HistoryRangeControls from './HistoryRangeControls';
import {
  type HistoryPanelKind,
  type HistoryTrendGroup,
  type HistoryWindow,
  historyMetricCodes,
  historyTick,
  historyTrendGroups,
  historyWindow,
} from './historyTrends';
import { sensorIdentity } from './metrics';
import type { OverviewServerData } from './overviewData.types';
import TrendChart from './TrendChart';

const panelTitles = {
  temperatures: 'oob.monitor.temperatures',
  cpu: 'oob.aggregate.cpu',
  array: 'oob.aggregate.array',
  airflow: 'oob.aggregate.airflow',
  fans: 'oob.monitor.fans',
  fanRpmDetails: 'oob.aggregate.fan',
  fanPercentDetails: 'oob.aggregate.speedPercent',
  power: 'oob.monitor.power',
} as const;

const aggregateTitles = {
  cpu: 'oob.aggregate.cpu',
  array: 'oob.aggregate.array',
  fanRpm: 'oob.aggregate.fan',
  fanSpeedPercent: 'oob.aggregate.fan',
  power: 'oob.aggregate.power',
  inlet: 'oob.aggregate.inlet',
  exhaust: 'oob.aggregate.exhaust',
} as const;

type HistorySource = {
  serverId: string;
  serverName: string;
  inventory?: DeviceInventory | null;
  row?: OverviewServerData;
};
type HistoryDialogProps = { kind: HistoryPanelKind } & (
  | {
      serverId: string;
      serverName: string;
      inventory?: DeviceInventory | null;
      servers?: never;
      aggregateKind?: never;
    }
  | {
      servers: OverviewServerData[];
      aggregateKind: AggregateKind;
      serverId?: never;
      serverName?: never;
      inventory?: never;
    }
);

function HistoryContent({
  sources,
  kind,
  aggregateKind,
  openedAt,
  sessionId,
  closeSignal,
}: {
  sources: HistorySource[];
  kind: HistoryPanelKind;
  aggregateKind?: AggregateKind;
  openedAt: number;
  sessionId: number;
  closeSignal: AbortSignal;
}) {
  const t = useOobIntl();
  const [window, setWindow] = useState<HistoryWindow>(() =>
    historyWindow('24h', openedAt),
  );
  const scope = sources.map((source) => source.serverId);
  const query = useQuery({
    queryKey: [
      'oob',
      scope,
      'overview-history',
      kind,
      aggregateKind,
      sessionId,
      window,
    ],
    queryFn: async ({ signal }) => {
      const linked = new AbortController();
      const abort = () => linked.abort();
      const signals = [signal, closeSignal];
      for (const source of signals) {
        if (source.aborted) linked.abort();
        else source.addEventListener('abort', abort, { once: true });
      }
      // 总体风扇也固定为打开时选中的单位，后续推送导致能力回退不能改写已打开范围。
      const codes =
        kind === 'fans' && aggregateKind
          ? [aggregateKind === 'fanSpeedPercent' ? 'fan.percent' : 'fan.rpm']
          : historyMetricCodes[kind];
      const requests = sources.flatMap((source) =>
        codes.map((metricCode) => ({ source, metricCode })),
      );
      const results: {
        source: HistorySource;
        result: Awaited<ReturnType<typeof getHistory>>;
      }[] = [];
      try {
        // 聚合页限制并发，关闭或切换区间立即取消，尚未发出的请求不继续排队。
        for (
          let index = 0;
          index < requests.length && !linked.signal.aborted;
          index += 4
        ) {
          results.push(
            ...(await Promise.all(
              requests
                .slice(index, index + 4)
                .map(async ({ source, metricCode }) => ({
                  source,
                  result: await getHistory(
                    source.serverId,
                    {
                      from: window.from,
                      to: window.to,
                      bucketSeconds: window.bucketSeconds,
                      metricCode,
                    },
                    linked.signal,
                  ),
                })),
            )),
          );
        }
        return results;
      } finally {
        for (const source of signals)
          source.removeEventListener('abort', abort);
      }
    },
    retry: false,
    staleTime: Infinity,
    gcTime: 0,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchOnMount: 'always',
  });
  const results = query.data || [];
  const rows = results.flatMap(({ result }) =>
    result.success ? result.data : [],
  );
  const failures = results.flatMap(({ source, result }) =>
    !result.success && result.kind !== 'canceled'
      ? [`${source.serverName} · ${result.errorMessage}`]
      : [],
  );
  if (kind === 'temperatures' || kind === 'cpu' || kind === 'array')
    failures.push(
      ...sources.flatMap(
        (source) =>
          source.row?.issues
            .filter((issue) => issue.kind === 'inventory')
            .map((issue) => `${source.serverName} · ${issue.message}`) || [],
      ),
    );
  const labels = {
    temperatures: t('oob.monitor.temperatures'),
    cpu: t('oob.aggregate.cpu'),
    array: t('oob.aggregate.array'),
    airflow: t('oob.aggregate.airflow'),
    fans: t('oob.aggregate.fan'),
    power: t('oob.monitor.power'),
    rpm: t('oob.monitor.averageRpm'),
    speedPercent: t('oob.monitor.averageSpeedPercent'),
    cpuSlot: (number: string) => t('oob.monitor.cpuSlot', { number }),
  };
  const statLabels: Record<AggregateStat, string> = {
    average: t('oob.aggregate.average'),
    minimum: t('oob.aggregate.minimum'),
    maximum: t('oob.aggregate.maximum'),
    total: t('oob.aggregate.availableTotal'),
  };
  let availableGroups: (HistoryTrendGroup & {
    coverage?: AggregatePanel['coverage'];
    pointDescription?: (time: number) => string | undefined;
  })[];
  if (aggregateKind) {
    const historyRows = sources.flatMap((source) =>
      source.row
        ? [
            {
              ...source.row,
              history: results.flatMap((entry) =>
                entry.source.serverId === source.serverId &&
                entry.result.success
                  ? entry.result.data
                  : [],
              ),
            },
          ]
        : [],
    );
    const kinds: AggregateKind[] =
      kind === 'airflow' ? ['inlet', 'exhaust'] : [aggregateKind];
    availableGroups = kinds.map((item) => {
      const panel = aggregateHistory(historyRows, item, window);
      return {
        key: item,
        title: t(
          item === 'fanSpeedPercent'
            ? 'oob.aggregate.speedPercent'
            : aggregateTitles[item],
        ),
        unit: panel.unit,
        series: panel.series.map((series) => ({
          ...series,
          name: statLabels[series.name as AggregateStat],
        })),
        coverage: panel.coverage,
      };
    });
    if (kind === 'airflow') {
      // 进气与排气分别聚合，保持六条统计曲线；只有进气使用虚线。
      const thermalGroups = availableGroups;
      availableGroups = [
        {
          key: 'airflow',
          title: labels.airflow,
          unit: '°C',
          series: thermalGroups.flatMap((group) =>
            group.series.map((series) => ({
              ...series,
              name: `${group.title} · ${series.name}`,
            })),
          ),
          dashedSeries: thermalGroups
            .filter((group) => group.key === 'inlet')
            .flatMap((group) => group.series.map(sensorIdentity)),
          pointDescription: (time) =>
            thermalGroups
              .flatMap((group) => {
                const coverage = group.coverage?.[time];
                return coverage
                  ? [
                      `${group.title} · ${t('oob.aggregate.coverage', { valid: coverage.validCount, total: coverage.totalCount })}`,
                    ]
                  : [];
              })
              .join(' · ') || undefined,
        },
      ];
    }
  } else
    availableGroups = historyTrendGroups(
      kind,
      rows,
      sources[0]?.inventory,
      labels,
    );
  // 历史接口也返回旧序列的空桶；无可绘制点的序列不占图例，有效曲线内部缺口仍原样保留。
  const groups = availableGroups
    .map((group) => ({
      ...group,
      series: group.series.filter((row) =>
        row.points.some(
          (point) =>
            point.count > 0 &&
            point.average !== null &&
            Number.isFinite(point.average),
        ),
      ),
    }))
    .filter((group) => group.series.length > 0);
  const runQuery = (next: HistoryWindow) => {
    if (
      next.from === window.from &&
      next.to === window.to &&
      next.bucketSeconds === window.bucketSeconds &&
      next.period === window.period
    )
      void query.refetch();
    else setWindow(next);
  };
  return (
    <Space orientation="vertical" size="middle" style={{ width: '100%' }}>
      <Typography.Paragraph type="secondary" style={{ margin: 0 }}>
        {t('oob.monitor.history.local')}
      </Typography.Paragraph>
      <HistoryRangeControls
        window={window}
        loading={query.isFetching}
        onQuery={runQuery}
      />
      <Typography.Text type="secondary">
        {t('oob.monitor.history.window', {
          from: formatTime(window.from),
          to: formatTime(window.to),
        })}{' '}
        ·{' '}
        {t('oob.monitor.history.bucket', {
          minutes: window.bucketSeconds / 60,
        })}
      </Typography.Text>
      {aggregateKind && (
        <Typography.Text type="secondary">
          {t('oob.monitor.history.aggregateHelp')}
        </Typography.Text>
      )}
      {kind === 'fans' && !aggregateKind && (
        <Typography.Text type="secondary">
          {t('oob.monitor.history.fanHelp')}
        </Typography.Text>
      )}
      {(failures.length > 0 || query.error) && (
        <Alert
          showIcon
          type="error"
          title={t('oob.load.failed')}
          description={[
            ...new Set(failures),
            ...(query.error ? [query.error.message] : []),
          ].join('；')}
        />
      )}
      {query.isPending ? (
        <div style={{ textAlign: 'center', padding: 64 }}>
          <Spin />
        </div>
      ) : groups.length ? (
        groups.map((group) => (
          <section key={group.key} aria-label={group.title}>
            <TrendChart
              series={group.series}
              unit={group.unit}
              title={group.title}
              height={240}
              xLabelFormatter={historyTick}
              scopeKey={`${scope.join(':')}:history:${kind}:${group.key}`}
              dashedSeries={group.dashedSeries}
              pointDescription={
                group.pointDescription ||
                (group.coverage
                  ? (time) => {
                      const coverage = group.coverage?.[time];
                      return coverage
                        ? t('oob.aggregate.coverage', {
                            valid: coverage.validCount,
                            total: coverage.totalCount,
                          })
                        : undefined;
                    }
                  : undefined)
              }
            />
          </section>
        ))
      ) : (
        <Empty description={t('oob.monitor.history.empty')} />
      )}
    </Space>
  );
}

/** 只有用户打开或明确查询才读取本地历史；关闭卸载查询并取消旧请求，不访问带外设备。 */
export default function HistoryTrendDialog(props: HistoryDialogProps) {
  const { kind, aggregateKind } = props;
  const t = useOobIntl();
  const [session, setSession] = useState<{
    openedAt: number;
    id: number;
    controller: AbortController;
    sources: HistorySource[];
    aggregateKind?: AggregateKind;
    kind: HistoryPanelKind;
    title: string;
  }>();
  const nextSession = React.useRef(0);
  const close = () => {
    // 不等待Modal退出动画才卸载查询，关闭意图立即取消本地历史请求。
    session?.controller.abort();
    setSession(undefined);
  };
  const panel = t(
    kind === 'airflow'
      ? panelTitles.airflow
      : aggregateKind
        ? aggregateTitles[aggregateKind]
        : panelTitles[kind],
  );
  const serverName = aggregateKind
    ? t('oob.aggregate.title')
    : props.serverName;
  const title = t('oob.monitor.history.title', { name: serverName, panel });
  return (
    <>
      <Tooltip title={t('oob.monitor.history.query')}>
        <Button
          type="text"
          size="small"
          icon={<SearchOutlined />}
          aria-label={title}
          disabled={props.servers?.some((row) => row.loading)}
          onClick={() =>
            setSession({
              openedAt: Date.now(),
              id: ++nextSession.current,
              controller: new AbortController(),
              aggregateKind,
              kind,
              title,
              // 打开时固定服务器集合及库存，实时推送只更新主看板，不引发历史重查。
              sources: props.servers
                ? props.servers.map((row) => ({
                    serverId: row.snapshot.server.id,
                    serverName: row.snapshot.server.name,
                    inventory: row.inventory,
                    row,
                  }))
                : [
                    {
                      serverId: props.serverId,
                      serverName: props.serverName,
                      inventory: props.inventory,
                    },
                  ],
            })
          }
        />
      </Tooltip>
      <Modal
        open={session !== undefined}
        title={session?.title || title}
        width={1040}
        destroyOnHidden
        onCancel={close}
        footer={
          <Button
            aria-label={t('oob.monitor.fanDetails.close')}
            onClick={close}
          >
            {t('oob.monitor.fanDetails.close')}
          </Button>
        }
        styles={{
          body: { maxHeight: 'calc(100vh - 240px)', overflowY: 'auto' },
        }}
      >
        {session !== undefined && (
          <HistoryContent
            key={`${session.kind}:${session.id}`}
            sources={session.sources}
            aggregateKind={session.aggregateKind}
            kind={session.kind}
            openedAt={session.openedAt}
            sessionId={session.id}
            closeSignal={session.controller.signal}
          />
        )}
      </Modal>
    </>
  );
}
