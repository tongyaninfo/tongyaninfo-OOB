import {
  CloudServerOutlined,
  DisconnectOutlined,
  FullscreenExitOutlined,
  FullscreenOutlined,
  LeftOutlined,
  ReloadOutlined,
  RightOutlined,
  ThunderboltOutlined,
  WifiOutlined,
} from '@ant-design/icons';
import { PageContainer } from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@umijs/max';
import {
  Alert,
  Button,
  Card,
  ConfigProvider,
  Empty,
  Space,
  Statistic,
  Tag,
  Typography,
} from 'antd';
import React, { useEffect, useMemo, useState } from 'react';
import { getDashboard } from '@/services/oob/api';
import type { ServerDto, ServerTelemetryDto } from '@/services/oob/models';
import ManagedTable from '../components/ManagedTable';
import ServerAddress from '../components/ServerAddress';
import ServerStatus from '../components/ServerStatus';
import { formatReading, formatTime } from '../format';
import { useOobIntl } from '../intl';
import { useTelemetryStream } from '../useTelemetryStream';
import AggregateDashboard from './AggregateDashboard';
import { aggregateDashboard } from './aggregate';
import useStyles from './Overview.style';
import { compareVersions, mergeSnapshots, RECENT_WINDOW_MS } from './recent';
import ServerMonitor from './ServerMonitor';
import useBoardFullscreen from './useBoardFullscreen';
import useOverviewData from './useOverviewData';

/** 总览用已持久化快照初始化，接收推送后直接更新数值及曲线，不轮询 HTTP。 */
export default function Overview() {
  const t = useOobIntl();
  const { styles } = useStyles();
  const viewport = useBoardFullscreen();
  const [now, setNow] = useState(Date.now);
  // 用服务器身份保持当前页，新增或排序变化不会把明细悄悄换成另一台。
  const [selectedServerId, setSelectedServerId] = useState<string | null>(null);
  const [events, setEvents] = useState<Record<string, ServerTelemetryDto[]>>(
    {},
  );
  const query = useQuery({
    queryKey: ['oob', 'dashboard'],
    queryFn: ({ signal }) => getDashboard(signal),
    retry: false,
    staleTime: Infinity,
    refetchOnMount: 'always',
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  const { state, gaps } = useTelemetryStream((event) => {
    // 新样本到达时同步墙钟，避免5秒定时器的旧时间把新读数误判为未来/过期。
    setNow(Date.now());
    setEvents((previous) => {
      const rows = previous[event.server.id] || [];
      if (
        rows.length &&
        compareVersions(rows[rows.length - 1].publishedAt, event.publishedAt) >=
          0
      )
        return previous;
      const cutoff = Date.now() - RECENT_WINDOW_MS;
      return {
        ...previous,
        [event.server.id]: [
          ...rows.filter((item) => Date.parse(item.publishedAt) >= cutoff),
          event,
        ],
      };
    });
  });
  useEffect(() => {
    // 即使没有新推送，也按真实时间标记过期和滚动15分钟窗口。
    const timer = setInterval(() => setNow(Date.now()), 5000);
    return () => clearInterval(timer);
  }, []);
  const dashboard = query.data?.success ? query.data.data : undefined;
  const snapshots = useMemo(
    () =>
      mergeSnapshots(
        dashboard?.telemetrySnapshots || [],
        Object.values(events).flatMap((rows) => rows.slice(-1)),
      ),
    [dashboard, events],
  );
  const servers = snapshots.map((snapshot) => snapshot.server);
  // 首次HTTP结果也可能晚于定时器刻度，用本次渲染墙钟校验新鲜度。
  const observedNow = Math.max(now, Date.now());
  const serverData = useOverviewData(snapshots, events, gaps, observedNow);
  const aggregate = useMemo(
    () => aggregateDashboard(serverData, observedNow),
    [serverData, observedNow],
  );
  const totalPower = aggregate.power.stats.total;
  const selectedIndex = serverData.findIndex(
    (row) => row.snapshot.server.id === selectedServerId,
  );
  const page = selectedIndex + 1;
  const selected = selectedIndex >= 0 ? serverData[selectedIndex] : undefined;
  const selectPage = (next: number) => {
    if (next < 0 || next > serverData.length) return;
    setSelectedServerId(
      next === 0 ? null : serverData[next - 1].snapshot.server.id,
    );
  };
  const onBoardKeyDown = (event: React.KeyboardEvent<HTMLElement>) => {
    if (
      !['ArrowLeft', 'ArrowRight'].includes(event.key) ||
      event.repeat ||
      event.nativeEvent.isComposing ||
      event.ctrlKey ||
      event.altKey ||
      event.metaKey ||
      event.shiftKey
    )
      return;
    // 只在看板箭头获得焦点时响应，不抢占页面表格、输入框或图例的键盘行为。
    event.preventDefault();
    selectPage(page + (event.key === 'ArrowRight' ? 1 : -1));
  };
  const updated = snapshots.reduce(
    (latest, item) => Math.max(latest, Date.parse(item.publishedAt)),
    Date.parse(dashboard?.generatedAt || '') || 0,
  );
  const stats = [
    [
      t('oob.total'),
      query.isLoading && !servers.length ? '—' : servers.length,
      <CloudServerOutlined key="total" />,
      'normal',
    ],
    [
      t('oob.online'),
      query.isLoading && !servers.length
        ? '—'
        : servers.filter((server) => server.connectionStatus === 'Online')
            .length,
      <WifiOutlined key="online" />,
      'online',
    ],
    [
      t('oob.offline'),
      query.isLoading && !servers.length
        ? '—'
        : servers.filter((server) => server.connectionStatus === 'Offline')
            .length,
      <DisconnectOutlined key="offline" />,
      'offline',
    ],
    [
      t('oob.totalPower'),
      formatReading(totalPower, 'W'),
      <ThunderboltOutlined key="power" />,
      'power',
    ],
  ] as const;
  const stateLabel = {
    connecting: 'oob.live.connecting',
    connected: 'oob.live.connected',
    reconnecting: 'oob.live.reconnecting',
    disconnected: 'oob.live.disconnected',
  } as const;
  const fullscreenIssues = {
    unsupported: 'oob.aggregate.fullscreen.unsupported',
    occupied: 'oob.aggregate.fullscreen.occupied',
    failed: 'oob.aggregate.fullscreen.failed',
  } as const;
  return (
    <div ref={viewport.ref} className={styles.viewport}>
      <ConfigProvider getPopupContainer={viewport.popupContainer}>
        <PageContainer
          className={styles.page}
          title={
            <span className={styles.headingRow}>
              <span className={styles.headingTitle}>{t('oob.overview')}</span>
              <span className={styles.switcher}>
                <Button
                  icon={<LeftOutlined />}
                  aria-label={t('oob.aggregate.previous')}
                  title={t('oob.aggregate.previous')}
                  disabled={page === 0}
                  onClick={() => selectPage(page - 1)}
                  onKeyDown={onBoardKeyDown}
                />
                <span className={styles.switcherLabel}>
                  <Typography.Text strong>
                    {selected
                      ? selected.snapshot.server.name
                      : t('oob.aggregate.title')}
                  </Typography.Text>
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {selected
                      ? `${t('oob.aggregate.detail')} · ${t(
                          'oob.aggregate.detailPage',
                          {
                            current: page,
                            total: serverData.length,
                          },
                        )}`
                      : t('oob.aggregate.page', {
                          current: 1,
                          total: serverData.length + 1,
                        })}
                  </Typography.Text>
                </span>
                <Button
                  icon={<RightOutlined />}
                  aria-label={t('oob.aggregate.next')}
                  title={t('oob.aggregate.next')}
                  disabled={page >= serverData.length}
                  onClick={() => selectPage(page + 1)}
                  onKeyDown={onBoardKeyDown}
                />
              </span>
            </span>
          }
          extra={
            <Space wrap>
              <Tag color={state === 'connected' ? 'success' : 'warning'}>
                {t(stateLabel[state])} · WebSocket
              </Tag>
              <Button
                icon={<ReloadOutlined />}
                loading={query.isFetching}
                onClick={() => query.refetch()}
              >
                {t('oob.refresh')}
              </Button>
              <Button
                icon={
                  viewport.isFullscreen ? (
                    <FullscreenExitOutlined />
                  ) : (
                    <FullscreenOutlined />
                  )
                }
                loading={viewport.pending}
                aria-pressed={viewport.isFullscreen}
                aria-label={t(
                  viewport.isFullscreen
                    ? 'oob.aggregate.fullscreen.exit'
                    : 'oob.aggregate.fullscreen.enter',
                )}
                title={t(
                  viewport.isFullscreen
                    ? 'oob.aggregate.fullscreen.hint'
                    : 'oob.aggregate.fullscreen.enter',
                )}
                onClick={() => void viewport.toggle()}
              >
                {t(
                  viewport.isFullscreen
                    ? 'oob.aggregate.fullscreen.exit'
                    : 'oob.aggregate.fullscreen.enter',
                )}
              </Button>
            </Space>
          }
        >
          <Space orientation="vertical" size={16} style={{ width: '100%' }}>
            <div className={styles.introduction}>
              <div>
                <span className={styles.brandLabel}>通研带外系统管理</span>
                <p className={styles.subtitle}>
                  {t('oob.brand.overview.description')}
                </p>
              </div>
              <span className={styles.updated}>
                {t('oob.updated')}：
                {updated ? formatTime(new Date(updated).toISOString()) : '—'}
              </span>
            </div>
            {viewport.issue && (
              <Alert
                showIcon
                type="warning"
                title={t(fullscreenIssues[viewport.issue])}
              />
            )}
            {(query.error || query.data?.success === false) && (
              <Alert
                showIcon
                type="error"
                title={t('oob.load.failed')}
                description={
                  query.data?.success === false
                    ? query.data.errorMessage
                    : (query.error as Error).message
                }
              />
            )}
            <div className={styles.statistics}>
              {stats.map(([title, value, icon, kind]) => (
                <Card
                  key={title}
                  className={styles.statisticCard}
                  size="small"
                  loading={query.isLoading && !servers.length}
                >
                  <div className={styles.statisticInner}>
                    <Statistic title={title} value={value} />
                    <span
                      className={styles.statisticIcon}
                      data-kind={kind}
                      aria-hidden="true"
                    >
                      {icon}
                    </span>
                  </div>
                </Card>
              ))}
            </div>
            <Typography.Paragraph className={styles.dataHint}>
              {t('oob.live.help')}
            </Typography.Paragraph>
            {state !== 'connected' && (
              <Alert
                showIcon
                type="warning"
                title={t(stateLabel[state])}
                description={t('oob.live.gap')}
              />
            )}
            {!query.isLoading && !servers.length && (
              <Empty description={t('oob.noServers.help')} />
            )}
            {serverData.flatMap((row) =>
              row.issues.map((issue) => (
                <Alert
                  key={`${row.snapshot.server.id}:${issue.kind}`}
                  showIcon
                  type="warning"
                  title={
                    row.snapshot.server.name +
                    ' · ' +
                    t(
                      issue.kind === 'history'
                        ? 'oob.trends.failed'
                        : 'oob.inventory',
                    )
                  }
                  description={issue.message}
                />
              )),
            )}
            {selected ? (
              <ServerMonitor
                key={selected.snapshot.server.id}
                snapshot={selected.snapshot}
                inventory={selected.inventory}
                history={selected.history}
                loading={selected.loading}
                liveDisconnected={state !== 'connected'}
                now={observedNow}
              />
            ) : (
              <AggregateDashboard
                data={aggregate}
                servers={serverData}
                now={observedNow}
                loading={query.isLoading && !servers.length}
              />
            )}
            <Card
              title={t('oob.servers')}
              size="small"
              className={styles.serverCard}
            >
              <ManagedTable<ServerDto>
                tableId="oob.overview.servers.v1"
                rowKey="id"
                loading={query.isLoading}
                dataSource={servers}
                scroll={{ x: 880 }}
                pagination={{ pageSize: 10, showSizeChanger: true }}
                locale={{
                  emptyText: <Empty description={t('oob.noServers')} />,
                }}
                columns={[
                  {
                    title: t('oob.name'),
                    dataIndex: 'name',
                    render: (name: string, server) => (
                      <Link to={`/oob/servers/${server.id}`}>{name}</Link>
                    ),
                  },
                  {
                    title: t('oob.status'),
                    key: 'status',
                    render: (_, server) => <ServerStatus server={server} />,
                  },
                  { title: t('oob.controller'), dataIndex: 'controllerType' },
                  {
                    title: t('oob.model'),
                    key: 'model',
                    render: (_, server) => server.identity.model || '—',
                  },
                  {
                    title: t('oob.address'),
                    dataIndex: 'address',
                    render: (_, server) => (
                      <ServerAddress address={server.address} />
                    ),
                  },
                  {
                    title: t('oob.sampled'),
                    dataIndex: 'lastCollectedAt',
                    render: (value) => formatTime(value),
                  },
                ]}
              />
            </Card>
          </Space>
        </PageContainer>
      </ConfigProvider>
    </div>
  );
}
