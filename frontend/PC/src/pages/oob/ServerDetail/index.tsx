import {
  ArrowLeftOutlined,
  EditOutlined,
  ReloadOutlined,
} from '@ant-design/icons';
import { PageContainer } from '@ant-design/pro-components';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useAccess, useParams } from '@umijs/max';
import {
  Alert,
  App,
  Button,
  Card,
  Descriptions,
  Empty,
  Skeleton,
  Space,
  Tabs,
  Tag,
} from 'antd';
import React, { useEffect, useState } from 'react';
import {
  getServer,
  refreshInventory,
  sampleTelemetry,
} from '@/services/oob/api';
import { captureRuntimeException } from '@/services/oob/browserExceptions';
import type { ServerTelemetryDto } from '@/services/oob/models';
import HistoryPanel from '../components/HistoryPanel';
import InventoryPanel from '../components/InventoryPanel';
import PowerPanel from '../components/PowerPanel';
import ServerEditor from '../components/ServerEditor';
import ServerStatus from '../components/ServerStatus';
import TelemetryPanel from '../components/TelemetryPanel';
import UidPanel from '../components/UidPanel';
import { formatTime } from '../format';
import { useOobIntl } from '../intl';
import { compareVersions, isSnapshotStale } from '../Overview/recent';
import { useTelemetryStream } from '../useTelemetryStream';

/** 详情首次读取快照，此后接收已入库采样的推送；重连不补取或触发采集。 */
export default function ServerDetail() {
  const t = useOobIntl();
  const access = useAccess();
  const { id = '' } = useParams<{ id: string }>();
  const { message } = App.useApp();
  const cache = useQueryClient();
  const [editorOpen, setEditorOpen] = useState(false);
  const [latestEvent, setLatestEvent] = useState<ServerTelemetryDto>();
  const [now, setNow] = useState(Date.now);
  const live = useTelemetryStream((event) => {
    if (event.server.id !== id) return;
    setLatestEvent((previous) =>
      !previous ||
      previous.server.id !== id ||
      compareVersions(event.publishedAt, previous.publishedAt) > 0
        ? event
        : previous,
    );
  });
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 5000);
    return () => clearInterval(timer);
  }, []);
  const query = useQuery({
    queryKey: ['oob', id, 'detail'],
    queryFn: ({ signal }) => getServer(id, signal),
    enabled: Boolean(id),
    retry: false,
    staleTime: Infinity,
    refetchOnMount: 'always',
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  const invalidate = () => {
    cache.invalidateQueries({ queryKey: ['oob', id, 'detail'] });
    cache.invalidateQueries({ queryKey: ['oob', 'dashboard'] });
  };
  const inventory = useMutation({
    mutationFn: () => refreshInventory(id),
    retry: false,
    onSuccess: (result) => {
      if (result.success) invalidate();
      else message.error(result.errorMessage);
    },
    onError: (error: Error) => {
      void captureRuntimeException(error);
      message.error(error.message);
    },
  });
  const sample = useMutation({
    mutationFn: () => sampleTelemetry(id),
    retry: false,
    onSuccess: (result) => {
      if (result.success) invalidate();
      else message.error(result.errorMessage);
    },
    onError: (error: Error) => {
      void captureRuntimeException(error);
      message.error(error.message);
    },
  });
  const initial = query.data?.success ? query.data.data : undefined;
  const event = latestEvent?.server.id === id ? latestEvent : undefined;
  const updated =
    initial &&
    event &&
    compareVersions(event.publishedAt, initial.server.updatedAt) > 0
      ? { ...initial, server: event.server, telemetry: event.telemetry }
      : initial;
  const detail = updated
    ? {
        ...updated,
        telemetry: {
          ...updated.telemetry,
          stale: isSnapshotStale(
            {
              server: updated.server,
              telemetry: updated.telemetry,
              publishedAt: updated.server.updatedAt,
            },
            now,
          ),
        },
      }
    : undefined;
  const server = detail?.server;
  const identity = server?.identity;
  const notices = [
    ...new Set([
      ...(detail?.capabilities.warnings || []),
      ...(detail?.inventory?.warnings || []),
      ...(detail?.telemetry.warnings || []),
    ]),
  ];
  return (
    <PageContainer
      title={server?.name || t('oob.details')}
      subTitle={identity?.model || undefined}
      extra={
        <Space wrap>
          <Tag color={live.state === 'connected' ? 'success' : 'warning'}>
            {t(
              live.state === 'connected'
                ? 'oob.live.connected'
                : 'oob.live.disconnected',
            )}{' '}
            · WebSocket
          </Tag>
          <Link to="/oob/servers">
            <Button icon={<ArrowLeftOutlined />}>{t('oob.back')}</Button>
          </Link>
          {access.canUseConsole && (
            <Link to={`/oob/consoles?serverId=${encodeURIComponent(id)}`}>
              <Button disabled={!server}>{t('oob.console.open')}</Button>
            </Link>
          )}
          <Button
            icon={<ReloadOutlined />}
            loading={query.isFetching}
            onClick={() => query.refetch()}
          >
            {t('oob.refresh')}
          </Button>
          {access.canManageServers && (
            <Button
              icon={<EditOutlined />}
              disabled={!server}
              onClick={() => setEditorOpen(true)}
            >
              {t('oob.edit')}
            </Button>
          )}
        </Space>
      }
    >
      {query.isLoading ? (
        <Skeleton active />
      ) : query.error || query.data?.success === false ? (
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
      ) : !detail || !server ? (
        <Empty description={t('oob.details.notFound')} />
      ) : (
        <Space orientation="vertical" size="large" style={{ width: '100%' }}>
          {live.state !== 'connected' && (
            <Alert
              showIcon
              type="warning"
              title={t('oob.live.disconnected')}
              description={t('oob.live.gap')}
            />
          )}
          <Card
            title={t('oob.identity')}
            extra={<ServerStatus server={server} />}
          >
            <Descriptions
              column={{ xs: 1, sm: 2, lg: 3 }}
              items={[
                { key: 'brand', label: t('oob.brand'), children: server.brand },
                {
                  key: 'model',
                  label: t('oob.model'),
                  children: identity?.model || '—',
                },
                {
                  key: 'serial',
                  label: t('oob.serial'),
                  children: identity?.serialNumber || '—',
                },
                {
                  key: 'address',
                  label: t('oob.address'),
                  children: server.address,
                },
                {
                  key: 'controller',
                  label: t('oob.controller'),
                  children: server.controllerType,
                },
                {
                  key: 'firmware',
                  label: t('oob.firmware'),
                  children: identity?.firmwareVersion || '—',
                },
                {
                  key: 'host',
                  label: t('oob.hostName'),
                  children: identity?.hostName || '—',
                },
                {
                  key: 'bmc-host',
                  label: t('oob.controllerHostName'),
                  children: identity?.controllerHostName || '—',
                },
                {
                  key: 'power',
                  label: t('oob.powerState'),
                  children: identity?.powerState || '—',
                },
                {
                  key: 'protocol',
                  label: t('oob.protocol'),
                  children: detail.capabilities.protocol,
                },
                {
                  key: 'sampling',
                  label: t('oob.interval'),
                  children: server.samplingIntervalSeconds,
                },
                {
                  key: 'last',
                  label: t('oob.sampled'),
                  children: formatTime(server.lastCollectedAt),
                },
              ]}
            />
            <Space wrap>
              {detail.capabilities.supportedMetrics.map((metric) => (
                <Tag key={metric}>{metric}</Tag>
              ))}
            </Space>
          </Card>
          {access.canManageServers && <UidPanel server={server} />}
          {server.lastError && (
            <Alert
              showIcon
              type="warning"
              title={t('oob.warnings')}
              description={server.lastError}
            />
          )}
          {notices.length > 0 && (
            <Alert
              showIcon
              type="info"
              title={t('oob.warnings')}
              description={
                <ul style={{ marginBottom: 0 }}>
                  {notices.map((notice) => (
                    <li key={notice}>{notice}</li>
                  ))}
                </ul>
              }
            />
          )}
          <Card>
            <Tabs
              destroyOnHidden
              items={[
                {
                  key: 'inventory',
                  label: t('oob.inventory'),
                  children: (
                    <Space
                      orientation="vertical"
                      size="large"
                      style={{ width: '100%' }}
                    >
                      {access.canManageServers && (
                        <Button
                          loading={inventory.isPending}
                          onClick={() => inventory.mutate()}
                        >
                          {t('oob.inventory.refresh')}
                        </Button>
                      )}
                      <InventoryPanel inventory={detail.inventory} />
                    </Space>
                  ),
                },
                {
                  key: 'realtime',
                  label: t('oob.realtime'),
                  children: (
                    <Space
                      orientation="vertical"
                      size="large"
                      style={{ width: '100%' }}
                    >
                      {access.canManageServers && (
                        <Button
                          loading={sample.isPending}
                          onClick={() => sample.mutate()}
                        >
                          {t('oob.telemetry.sample')}
                        </Button>
                      )}
                      <TelemetryPanel telemetry={detail.telemetry} />
                    </Space>
                  ),
                },
                {
                  key: 'history',
                  label: t('oob.history'),
                  children: <HistoryPanel id={id} />,
                },
                ...(access.canOperatePower
                  ? [
                      {
                        key: 'power',
                        label: t('oob.power'),
                        children: (
                          <PowerPanel
                            server={detail.server}
                            initialOperations={detail.powerOperations}
                          />
                        ),
                      },
                    ]
                  : []),
              ]}
            />
          </Card>
          {access.canManageServers && (
            <ServerEditor
              open={editorOpen}
              server={server}
              onClose={() => setEditorOpen(false)}
              onSaved={invalidate}
            />
          )}
        </Space>
      )}
    </PageContainer>
  );
}
