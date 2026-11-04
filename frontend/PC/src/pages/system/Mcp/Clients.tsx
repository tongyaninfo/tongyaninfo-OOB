import { PlusOutlined, ReloadOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, App, Button, Input, Modal, Space, Tag, Typography } from 'antd';
import React, { useState } from 'react';
import ManagedTable from '@/pages/oob/components/ManagedTable';
import { formatTime } from '@/pages/oob/format';
import { useOobIntl } from '@/pages/oob/intl';
import {
  changeMcpClient,
  getMcpClients,
  getMcpOptions,
  rotateMcpSecret,
} from '@/services/oob/mcp';
import type {
  McpClientDto,
  McpClientQuery,
  McpSecretDto,
  McpToolDto,
} from '@/services/oob/models';
import ClientEditor from './ClientEditor';
import {
  detailRow,
  outcomeUnknown,
  requiresReload,
  useRequestScope,
} from './common';

export default function Clients({
  tools,
  account,
}: {
  tools: McpToolDto[];
  account: string;
}) {
  const t = useOobIntl();
  const { message, modal } = App.useApp();
  const scope = useRequestScope();
  const [params, setParams] = useState<McpClientQuery>({
    current: 1,
    pageSize: 20,
    sortField: 'createdAt',
    sortOrder: 'descend',
  });
  const [editor, setEditor] = useState<{ id?: string }>();
  const [pending, setPending] = useState(false);
  const [blocked, setBlocked] = useState(false);
  const [error, setError] = useState('');
  const [secret, setSecret] = useState<McpSecretDto>();
  const query = useQuery({
    queryKey: ['mcp', account, 'clients', params],
    queryFn: ({ signal }) => getMcpClients(params, signal),
    retry: false,
    refetchOnWindowFocus: false,
  });
  const data = query.data?.success ? query.data.data : undefined;
  const options = useQuery({
    queryKey: ['mcp', account, 'options'],
    queryFn: ({ signal }) => getMcpOptions(signal),
    retry: false,
    refetchOnWindowFocus: false,
  });
  const userNames = new Map(
    (options.data?.success ? options.data.data.users : []).map((user) => [
      user.id,
      `${user.displayName} (${user.username})`,
    ]),
  );
  const reload = async () => {
    const current = scope.capture();
    const result = await query.refetch();
    if (current() && result.data?.success) {
      setBlocked(false);
      setError('');
    }
  };
  const act = (
    client: McpClientDto,
    action: 'enable' | 'disable' | 'revoke' | 'rotate-secret',
  ) => {
    const title =
      action === 'rotate-secret'
        ? 'mcp.rotate'
        : action === 'revoke'
          ? 'mcp.revoke'
          : action === 'enable'
            ? 'mcp.enable'
            : 'mcp.disable';
    modal.confirm({
      title: `${t(title)} · ${client.name}`,
      content: t(
        action === 'rotate-secret'
          ? 'mcp.rotateHelp'
          : action === 'revoke'
            ? 'mcp.revokeHelp'
            : 'mcp.clientHelp',
      ),
      okText: t(title),
      cancelText: t('mcp.cancel'),
      okButtonProps: { danger: action !== 'enable' },
      onOk: async () => {
        if (!scope.active.current || scope.pending.current || blocked) return;
        const current = scope.capture();
        scope.pending.current = true;
        setPending(true);
        setError('');
        try {
          if (action === 'rotate-secret') {
            const result = await rotateMcpSecret(client.id, {
              expectedVersion: client.version,
            });
            if (!current()) return;
            if (result.success) {
              setSecret(result.data);
              await reload();
            } else {
              setBlocked(requiresReload(result));
              setError(
                outcomeUnknown(result)
                  ? t('mcp.secretUnknown')
                  : result.errorCode === 'MCP_VERSION_CONFLICT'
                    ? t('mcp.conflict')
                    : result.errorMessage,
              );
            }
          } else {
            const result = await changeMcpClient(client.id, action, {
              expectedVersion: client.version,
            });
            if (!current()) return;
            if (result.success) {
              message.success(t('mcp.saved'));
              await reload();
            } else {
              setBlocked(requiresReload(result));
              setError(
                outcomeUnknown(result)
                  ? t('mcp.unknown')
                  : result.errorCode === 'MCP_VERSION_CONFLICT'
                    ? t('mcp.conflict')
                    : result.errorMessage,
              );
            }
          }
        } finally {
          scope.pending.current = false;
          if (current()) setPending(false);
        }
      },
    });
  };
  const sort = (field: string) => ({
    sorter: true,
    // 正式查询始终有稳定排序，循环只在升降序之间切换，避免“取消”被默认降序锁住。
    sortDirections: ['ascend', 'descend', 'ascend'] as ('ascend' | 'descend')[],
    sortOrder:
      params.sortField === field
        ? (params.sortOrder as 'ascend' | 'descend')
        : null,
  });
  return (
    <>
      <Typography.Paragraph type="secondary">
        {t('mcp.clientHelp')}
      </Typography.Paragraph>
      <Space wrap style={{ marginBottom: 16 }}>
        <Input.Search
          aria-label={t('mcp.keyword')}
          placeholder={t('mcp.keyword')}
          allowClear
          style={{ width: 300 }}
          onSearch={(keyword) =>
            setParams((old) => ({ ...old, keyword, current: 1 }))
          }
        />
        <Button
          icon={<ReloadOutlined />}
          loading={query.isFetching}
          disabled={pending}
          onClick={() => void reload()}
        >
          {t('mcp.reload')}
        </Button>
        <Button
          type="primary"
          icon={<PlusOutlined />}
          disabled={pending || blocked}
          onClick={() => setEditor({})}
        >
          {t('mcp.add')}
        </Button>
      </Space>
      {(error || query.data?.success === false) && (
        <Alert
          showIcon
          type="error"
          title={
            error ||
            (query.data?.success === false ? query.data.errorMessage : '')
          }
          style={{ marginBottom: 16 }}
        />
      )}
      <ManagedTable<McpClientDto>
        tableId="system.mcp.clients.v1"
        rowKey="id"
        dataSource={data?.items || []}
        loading={query.isFetching}
        scroll={{ x: 1200 }}
        onRow={detailRow((row) => {
          if (!pending && !blocked) setEditor({ id: row.id });
        })}
        pagination={{
          current: params.current,
          pageSize: params.pageSize,
          total: data?.total || 0,
          showSizeChanger: true,
          onChange: (current, pageSize) =>
            setParams((old) => ({
              ...old,
              current: pageSize === old.pageSize ? current : 1,
              pageSize,
            })),
        }}
        onChange={(_page, _filters, sorter, extra) => {
          if (extra.action !== 'sort') return;
          const selected = Array.isArray(sorter) ? sorter[0] : sorter;
          setParams((old) => ({
            ...old,
            current: 1,
            sortField: String(selected.columnKey || 'createdAt'),
            sortOrder: selected.order || 'descend',
          }));
        }}
        columns={[
          {
            key: 'name',
            dataIndex: 'name',
            title: t('mcp.name'),
            ...sort('name'),
          },
          {
            key: 'oauthClientId',
            dataIndex: 'oAuthClientId',
            title: t('mcp.clientId'),
          },
          {
            key: 'userId',
            dataIndex: 'userId',
            title: t('mcp.boundUser'),
            render: (value: string) => userNames.get(value) || '—',
          },
          {
            key: 'enabled',
            dataIndex: 'enabled',
            title: t('mcp.status'),
            render: (value) => (
              <Tag color={value ? 'success' : 'default'}>
                {t(value ? 'mcp.enabled' : 'mcp.disabled')}
              </Tag>
            ),
          },
          {
            key: 'expiresAt',
            dataIndex: 'expiresAt',
            title: t('mcp.expiresAt'),
            render: formatTime,
          },
          {
            key: 'createdAt',
            dataIndex: 'createdAt',
            title: t('mcp.createdAt'),
            render: formatTime,
            ...sort('createdAt'),
          },
          {
            key: 'updatedAt',
            dataIndex: 'updatedAt',
            title: t('mcp.updatedAt'),
            render: formatTime,
            ...sort('updatedAt'),
          },
          {
            key: 'lastCalledAt',
            dataIndex: 'lastCalledAt',
            title: t('mcp.lastCalledAt'),
            render: formatTime,
            ...sort('lastCalledAt'),
          },
          {
            key: 'actions',
            title: t('mcp.actions'),
            render: (_, row) => (
              <Space wrap>
                <Button
                  type="link"
                  disabled={pending || blocked}
                  onClick={() => setEditor({ id: row.id })}
                >
                  {t('mcp.edit')}
                </Button>
                <Button
                  type="link"
                  disabled={pending || blocked}
                  onClick={() => act(row, row.enabled ? 'disable' : 'enable')}
                >
                  {t(row.enabled ? 'mcp.disable' : 'mcp.enable')}
                </Button>
                <Button
                  type="link"
                  danger
                  disabled={pending || blocked}
                  onClick={() => act(row, 'revoke')}
                >
                  {t('mcp.revoke')}
                </Button>
                {row.clientType === 'confidential' && (
                  <Button
                    type="link"
                    danger
                    disabled={pending || blocked}
                    onClick={() => act(row, 'rotate-secret')}
                  >
                    {t('mcp.rotate')}
                  </Button>
                )}
              </Space>
            ),
          },
        ]}
      />
      {editor && (
        <ClientEditor
          key={editor.id || 'new'}
          id={editor.id}
          account={account}
          tools={tools}
          onClose={() => setEditor(undefined)}
          onSaved={async () => {
            setEditor(undefined);
            await reload();
          }}
        />
      )}
      {secret && (
        <Modal
          open
          destroyOnHidden
          title={t('mcp.secret')}
          closable={false}
          mask={{ closable: false }}
          keyboard={false}
          footer={
            <Button type="primary" onClick={() => setSecret(undefined)}>
              {t('mcp.closeSecret')}
            </Button>
          }
        >
          <Typography.Paragraph>{t('mcp.rotateHelp')}</Typography.Paragraph>
          <Input.TextArea
            readOnly
            value={secret?.secret || ''}
            rows={4}
            autoComplete="off"
            aria-label={t('mcp.secret')}
          />
        </Modal>
      )}
    </>
  );
}
