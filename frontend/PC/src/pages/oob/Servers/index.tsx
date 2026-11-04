import {
  DownloadOutlined,
  PlusOutlined,
  UploadOutlined,
} from '@ant-design/icons';
import type { ActionType, ProColumns } from '@ant-design/pro-components';
import { PageContainer, ProTable } from '@ant-design/pro-components';
import { Link, useAccess } from '@umijs/max';
import { App, Button, Input } from 'antd';
import React, { useEffect, useRef, useState } from 'react';
import { getServers } from '@/services/oob/api';
import type { ServerDto } from '@/services/oob/models';
import {
  getTablePreference,
  hasTablePreferenceChanges,
  updateTablePreference,
} from '@/services/oob/preferences';
import ServerAddress from '../components/ServerAddress';
import ServerEditor from '../components/ServerEditor';
import ServerStatus from '../components/ServerStatus';
import { formatTime } from '../format';
import { useOobIntl } from '../intl';
import ConfigurationTransfer from './ConfigurationTransfer';
import useStyles from './style';

const tableId = 'oob.servers.v1';

/** 列表由服务端分页，稳定表编号隔离当前账号的列显隐和尺寸。 */
export default function Servers() {
  const { styles } = useStyles();
  const t = useOobIntl();
  const access = useAccess();
  const { message } = App.useApp();
  const table = useRef<ActionType>(null);
  const controller = useRef<AbortController | undefined>(undefined);
  const [editorOpen, setEditorOpen] = useState(false);
  const [transferMode, setTransferMode] = useState<'export' | 'import'>();
  const [editing, setEditing] = useState<ServerDto>();
  const [keyword, setKeyword] = useState('');
  const [current, setCurrent] = useState(1);
  const [pageSize, setPageSize] = useState(10);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const preference = getTablePreference(tableId);
  const [size, setSize] = useState<'small' | 'middle' | 'large'>(
    (preference?.size as 'small' | 'middle' | 'large') || 'middle',
  );
  const [columnsState, setColumnsState] = useState<
    Record<string, { show?: boolean }>
  >((preference?.columnsState as Record<string, { show?: boolean }>) || {});
  useEffect(() => () => controller.current?.abort(), []);
  const columns: ProColumns<ServerDto>[] = [
    {
      title: t('oob.name'),
      dataIndex: 'name',
      render: (_, server) => (
        <Link to={`/oob/servers/${server.id}`}>{server.name}</Link>
      ),
    },
    {
      title: t('oob.status'),
      key: 'status',
      render: (_, server) => <ServerStatus server={server} />,
    },
    { title: t('oob.brand'), dataIndex: 'brand' },
    { title: t('oob.controller'), dataIndex: 'controllerType' },
    {
      title: t('oob.model'),
      key: 'model',
      render: (_, server) => server.identity.model || '—',
    },
    {
      title: t('oob.firmware'),
      key: 'firmware',
      render: (_, server) => server.identity.firmwareVersion || '—',
    },
    {
      title: t('oob.address'),
      dataIndex: 'address',
      render: (_, server) => (
        <ServerAddress address={server.address} copyable />
      ),
    },
    { title: t('oob.interval'), dataIndex: 'samplingIntervalSeconds' },
    {
      title: t('oob.sampled'),
      dataIndex: 'lastCollectedAt',
      render: (_, server) => formatTime(server.lastCollectedAt),
    },
    {
      title: t('oob.actions'),
      valueType: 'option',
      key: 'actions',
      render: (_, server) => [
        <Link key="detail" to={`/oob/servers/${server.id}`}>
          {t('oob.details')}
        </Link>,
        access.canUseConsole && (
          <Link
            key="console"
            to={`/oob/consoles?serverId=${encodeURIComponent(server.id)}`}
          >
            {t('oob.console.open')}
          </Link>
        ),
        access.canManageServers && (
          <a
            key="edit"
            href={`/oob/servers/${server.id}?edit=true`}
            onClick={(event) => {
              event.preventDefault();
              setEditing(server);
              setEditorOpen(true);
            }}
          >
            {t('oob.edit')}
          </a>
        ),
      ],
    },
  ];
  return (
    <PageContainer title={t('oob.servers')} subTitle={t('oob.server.subtitle')}>
      <section
        // biome-ignore lint/a11y/noNoninteractiveTabindex: 命名分页区域必须可聚焦，以便使用左右键翻页。
        tabIndex={0}
        aria-label={t('oob.servers')}
        onKeyDown={(event) => {
          if (
            loading ||
            event.nativeEvent.isComposing ||
            event.repeat ||
            event.ctrlKey ||
            event.altKey ||
            event.metaKey ||
            event.shiftKey
          )
            return;
          if (
            (event.target as HTMLElement).closest(
              'input,textarea,select,button,a,[contenteditable=true],[role=combobox]',
            )
          )
            return;
          const next =
            event.key === 'ArrowRight'
              ? current + 1
              : event.key === 'ArrowLeft'
                ? current - 1
                : current;
          if (
            next !== current &&
            next >= 1 &&
            next <= Math.ceil(total / pageSize)
          ) {
            event.preventDefault();
            setCurrent(next);
          }
        }}
      >
        <ProTable<ServerDto>
          actionRef={table}
          rowKey="id"
          columns={columns}
          search={false}
          size={size}
          scroll={{ x: 1200 }}
          params={{ keyword }}
          pagination={{
            current,
            pageSize,
            total,
          }}
          // 监听真实表格事件，不接 ProTable 在内部 state updater 中同步回调的 pagination.onChange。
          onChange={(pagination) => {
            setCurrent(pagination.current || 1);
            setPageSize(pagination.pageSize || 10);
          }}
          onSizeChange={(next) => {
            const value =
              next === 'small' || next === 'large' ? next : 'middle';
            if (value === size) return;
            setSize(value);
            updateTablePreference(tableId, { size: value, columnsState }, () =>
              message.error(t('oob.settings.failed')),
            );
          }}
          columnsState={{
            value: columnsState,
            onChange: (value) => {
              if (
                !hasTablePreferenceChanges(
                  { size, columnsState },
                  { size, columnsState: value },
                )
              )
                return;
              setColumnsState(value);
              updateTablePreference(
                tableId,
                { size, columnsState: value },
                () => message.error(t('oob.settings.failed')),
              );
            },
          }}
          onLoadingChange={(value) => setLoading(Boolean(value))}
          request={async (params) => {
            controller.current?.abort();
            controller.current = new AbortController();
            const result = await getServers(
              {
                current: params.current,
                pageSize: params.pageSize,
                keyword: params.keyword,
              },
              controller.current.signal,
            );
            if (!result.success) {
              if (result.errorCode !== 'CANCELED')
                message.error(result.errorMessage);
              return { data: [], total: 0, success: false };
            }
            setTotal(result.data.total);
            return {
              data: result.data.items,
              total: result.data.total,
              success: true,
            };
          }}
          headerTitle={t('oob.servers')}
          toolbar={{ className: styles.toolbar }}
          toolBarRender={() => [
            <Input.Search
              key="search"
              allowClear
              placeholder={t('oob.search')}
              className={styles.search}
              onSearch={(value) => {
                setKeyword(value);
                setCurrent(1);
              }}
            />,
            access.canManageServers && (
              <Button
                key="export"
                icon={<DownloadOutlined />}
                onClick={() => setTransferMode('export')}
              >
                {t('oob.configuration.export')}
              </Button>
            ),
            access.canManageServers && (
              <Button
                key="import"
                icon={<UploadOutlined />}
                onClick={() => setTransferMode('import')}
              >
                {t('oob.configuration.import')}
              </Button>
            ),
            access.canManageServers && (
              <Button
                key="new"
                type="primary"
                icon={<PlusOutlined />}
                onClick={() => {
                  setEditing(undefined);
                  setEditorOpen(true);
                }}
              >
                {t('oob.add')}
              </Button>
            ),
          ]}
        />
      </section>
      {access.canManageServers && transferMode && (
        <ConfigurationTransfer
          mode={transferMode}
          onClose={() => setTransferMode(undefined)}
          onImported={() => table.current?.reload()}
        />
      )}
      {access.canManageServers && (
        <ServerEditor
          open={editorOpen}
          server={editing}
          onClose={() => setEditorOpen(false)}
          onSaved={() => table.current?.reload()}
        />
      )}
    </PageContainer>
  );
}
