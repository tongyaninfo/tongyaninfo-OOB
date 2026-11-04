import { CopyOutlined, ReloadOutlined } from '@ant-design/icons';
import { PageContainer } from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import { useAccess, useModel } from '@umijs/max';
import {
  Alert,
  App,
  Button,
  Descriptions,
  Form,
  Input,
  Modal,
  Result,
  Select,
  Space,
  Tag,
  Typography,
} from 'antd';
import React, { useState } from 'react';
import ManagedTable from '@/pages/oob/components/ManagedTable';
import { formatTime } from '@/pages/oob/format';
import { useOobIntl } from '@/pages/oob/intl';
import { detailRow } from '@/pages/system/Mcp/common';
import { getMcpLog, getMcpLogs } from '@/services/oob/mcp';
import type { McpCallLogDto, McpLogQuery } from '@/services/oob/models';

export const logResults = [
  'Started',
  'Succeeded',
  'AuthenticationFailed',
  'NetworkDenied',
  'ToolDenied',
  'ScopeDenied',
  'InvalidArguments',
  'RateLimited',
  'BusinessFailed',
  'TimedOut',
  'Canceled',
  'Interrupted',
  'ServiceDisabled',
  'SystemFailed',
] as const;
/** datetime-local值明确解释为上海时间，不依赖浏览器操作系统时区。 */
export const shanghaiInput = (value: string) =>
  formatTime(value).replace(' ', 'T');
export function shanghaiIso(value: string) {
  const date = new Date(`${value}+08:00`);
  return Number.isNaN(date.valueOf()) ? undefined : date.toISOString();
}
export function validLogRange(from?: string | null, to?: string | null) {
  if (!from || !to) return false;
  const length = Date.parse(to) - Date.parse(from);
  return Number.isFinite(length) && length > 0 && length <= 31 * 86400000;
}
function initialQuery(): McpLogQuery {
  const to = new Date();
  return {
    current: 1,
    pageSize: 20,
    from: new Date(to.valueOf() - 86400000).toISOString(),
    to: to.toISOString(),
    sortField: 'occurredAt',
    sortOrder: 'descend',
  };
}

function Workspace({ account }: { account: string }) {
  const t = useOobIntl();
  const { message } = App.useApp();
  const [params, setParams] = useState<McpLogQuery>(initialQuery);
  const [draft, setDraft] = useState<McpLogQuery>(params);
  const [rangeError, setRangeError] = useState(false);
  const [selected, setSelected] = useState<string>();
  const query = useQuery({
    queryKey: ['mcp', account, 'logs', params],
    queryFn: ({ signal }) => getMcpLogs(params, signal),
    retry: false,
    refetchOnWindowFocus: false,
  });
  const detail = useQuery({
    queryKey: ['mcp', account, 'log', selected],
    queryFn: ({ signal }) => getMcpLog(selected || '', signal),
    enabled: Boolean(selected),
    retry: false,
    refetchOnWindowFocus: false,
  });
  const data = query.data?.success ? query.data.data : undefined;
  const record = detail.data?.success ? detail.data.data : undefined;
  const resultLabel = (value: string) =>
    logResults.includes(value as (typeof logResults)[number])
      ? t(`mcp.logs.${value as (typeof logResults)[number]}`)
      : value;
  const sort = (field: string) => ({
    sorter: true,
    // 保留确定的全量排序，取消排序不能把受控状态固定在默认降序。
    sortDirections: ['ascend', 'descend', 'ascend'] as ('ascend' | 'descend')[],
    sortOrder:
      params.sortField === field
        ? (params.sortOrder as 'ascend' | 'descend')
        : null,
  });
  const search = () => {
    if (!validLogRange(draft.from, draft.to)) {
      setRangeError(true);
      return;
    }
    setRangeError(false);
    setParams({
      ...draft,
      current: 1,
      pageSize: params.pageSize,
      sortField: params.sortField,
      sortOrder: params.sortOrder,
    });
  };
  const fields = [
    ['clientId', 'mcp.logs.client'],
    ['userId', 'mcp.logs.userId'],
    ['method', 'mcp.logs.method'],
    ['tool', 'mcp.logs.tool'],
    ['serverId', 'mcp.logs.serverId'],
    ['sourceIp', 'mcp.logs.sourceIp'],
    ['errorCode', 'mcp.logs.errorCode'],
    ['traceId', 'mcp.logs.traceId'],
    ['callId', 'mcp.logs.callId'],
  ] as const;
  return (
    <PageContainer title={t('mcp.logs.title')}>
      <Typography.Paragraph type="secondary">
        {t('mcp.logs.help')}
      </Typography.Paragraph>
      <Form layout="vertical" onFinish={search}>
        <Space wrap align="start">
          <Form.Item label={t('mcp.logs.from')}>
            <Input
              aria-label={t('mcp.logs.from')}
              type="datetime-local"
              step={1}
              value={draft.from ? shanghaiInput(draft.from) : ''}
              onChange={(event) =>
                setDraft((old) => ({
                  ...old,
                  from: shanghaiIso(event.target.value),
                }))
              }
            />
          </Form.Item>
          <Form.Item label={t('mcp.logs.to')}>
            <Input
              aria-label={t('mcp.logs.to')}
              type="datetime-local"
              step={1}
              value={draft.to ? shanghaiInput(draft.to) : ''}
              onChange={(event) =>
                setDraft((old) => ({
                  ...old,
                  to: shanghaiIso(event.target.value),
                }))
              }
            />
          </Form.Item>
          {fields.map(([key, label]) => (
            <Form.Item key={key} label={t(label)}>
              <Input
                allowClear
                aria-label={t(label)}
                value={draft[key] || ''}
                maxLength={200}
                style={{ width: 190 }}
                onChange={(event) =>
                  setDraft((old) => ({
                    ...old,
                    [key]: event.target.value.trim() || undefined,
                  }))
                }
              />
            </Form.Item>
          ))}
          <Form.Item label={t('mcp.logs.result')}>
            <Select
              allowClear
              aria-label={t('mcp.logs.result')}
              style={{ width: 190 }}
              value={draft.result || undefined}
              options={logResults.map((value) => ({
                value,
                label: resultLabel(value),
              }))}
              onChange={(result) => setDraft((old) => ({ ...old, result }))}
            />
          </Form.Item>
        </Space>
        <Space wrap style={{ marginBottom: 16 }}>
          <Button type="primary" htmlType="submit">
            {t('mcp.logs.search')}
          </Button>
          <Button
            icon={<ReloadOutlined />}
            loading={query.isFetching}
            onClick={() => void query.refetch()}
          >
            {t('mcp.refresh')}
          </Button>
        </Space>
      </Form>
      {rangeError && (
        <Alert
          showIcon
          type="error"
          title={t('mcp.logs.invalidRange')}
          style={{ marginBottom: 16 }}
        />
      )}
      {query.data?.success === false && (
        <Alert
          showIcon
          type="error"
          title={query.data.errorMessage}
          style={{ marginBottom: 16 }}
        />
      )}
      <ManagedTable<McpCallLogDto>
        tableId="system.mcp.logs.v1"
        rowKey="id"
        dataSource={data?.items || []}
        loading={query.isFetching}
        scroll={{ x: 1350 }}
        onRow={detailRow((row) => setSelected(row.id))}
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
          const value = Array.isArray(sorter) ? sorter[0] : sorter;
          setParams((old) => ({
            ...old,
            current: 1,
            sortField: String(value.columnKey || 'occurredAt'),
            sortOrder: value.order || 'descend',
          }));
        }}
        columns={[
          {
            key: 'occurredAt',
            dataIndex: 'occurredAt',
            title: t('mcp.logs.occurredAt'),
            render: formatTime,
            ...sort('occurredAt'),
          },
          {
            key: 'client',
            title: t('mcp.logs.client'),
            render: (_, row) => row.clientName || row.clientId || '—',
          },
          {
            key: 'tool',
            dataIndex: 'tool',
            title: t('mcp.logs.tool'),
            render: (value, row) => value || row.method,
          },
          {
            key: 'serverId',
            dataIndex: 'serverId',
            title: t('mcp.logs.serverId'),
          },
          {
            key: 'sourceIp',
            dataIndex: 'sourceIp',
            title: t('mcp.logs.sourceIp'),
          },
          {
            key: 'result',
            dataIndex: 'result',
            title: t('mcp.logs.result'),
            render: (value) => (
              <Tag
                color={
                  value === 'Succeeded'
                    ? 'success'
                    : value === 'Started'
                      ? 'processing'
                      : 'warning'
                }
              >
                {resultLabel(value)}
              </Tag>
            ),
          },
          {
            key: 'durationMilliseconds',
            dataIndex: 'durationMilliseconds',
            title: t('mcp.logs.duration'),
            render: (value) => value ?? '—',
            ...sort('durationMilliseconds'),
          },
          {
            key: 'errorCode',
            dataIndex: 'errorCode',
            title: t('mcp.logs.errorCode'),
          },
          { key: 'callId', dataIndex: 'callId', title: t('mcp.logs.callId') },
          {
            key: 'actions',
            title: t('mcp.actions'),
            render: (_, row) => (
              <Button type="link" onClick={() => setSelected(row.id)}>
                {t('mcp.details')}
              </Button>
            ),
          },
        ]}
      />
      <Modal
        open={Boolean(selected)}
        title={t('mcp.logs.details')}
        width={1000}
        onCancel={() => setSelected(undefined)}
        footer={
          <Space>
            <Button
              icon={<CopyOutlined />}
              aria-label={t('mcp.copy')}
              disabled={!record}
              onClick={async () => {
                if (!record) return;
                try {
                  await navigator.clipboard.writeText(
                    JSON.stringify(record, null, 2),
                  );
                  message.success(t('mcp.copied'));
                } catch {
                  message.error(t('mcp.copyFailed'));
                }
              }}
            >
              {t('mcp.copy')}
            </Button>
            <Button onClick={() => setSelected(undefined)}>
              {t('mcp.cancel')}
            </Button>
          </Space>
        }
      >
        {detail.isFetching && (
          <Typography.Paragraph>{t('mcp.loading')}</Typography.Paragraph>
        )}
        {detail.data?.success === false && (
          <Alert type="error" showIcon title={detail.data.errorMessage} />
        )}
        {record && (
          <Descriptions
            bordered
            column={1}
            items={[
              {
                key: 'occurredAt',
                label: t('mcp.logs.occurredAt'),
                children: formatTime(record.occurredAt),
              },
              {
                key: 'callId',
                label: t('mcp.logs.callId'),
                children: record.callId,
              },
              {
                key: 'completedAt',
                label: t('mcp.logs.completedAt'),
                children: formatTime(record.completedAt),
              },
              {
                key: 'requestId',
                label: t('mcp.logs.requestId'),
                children: record.requestId || '—',
              },
              {
                key: 'httpStatus',
                label: t('mcp.logs.httpStatus'),
                children: record.httpStatusCode ?? '—',
              },
              {
                key: 'client',
                label: t('mcp.logs.client'),
                children: record.clientName || record.clientId || '—',
              },
              {
                key: 'user',
                label: t('mcp.logs.userId'),
                children: record.userId || '—',
              },
              {
                key: 'method',
                label: t('mcp.logs.method'),
                children: record.method,
              },
              {
                key: 'tool',
                label: t('mcp.logs.tool'),
                children: record.tool || '—',
              },
              {
                key: 'server',
                label: t('mcp.logs.serverId'),
                children: record.serverId || '—',
              },
              {
                key: 'ip',
                label: t('mcp.logs.sourceIp'),
                children: record.sourceIp,
              },
              {
                key: 'result',
                label: t('mcp.logs.result'),
                children: resultLabel(record.result),
              },
              {
                key: 'code',
                label: t('mcp.logs.errorCode'),
                children: record.errorCode || '—',
              },
              {
                key: 'duration',
                label: t('mcp.logs.duration'),
                children: record.durationMilliseconds ?? '—',
              },
              {
                key: 'version',
                label: t('mcp.logs.protocol'),
                children: record.protocolVersion || '—',
              },
              {
                key: 'trace',
                label: t('mcp.logs.traceId'),
                children: record.traceId || '—',
              },
              {
                key: 'exception',
                label: t('mcp.logs.exception'),
                children: record.exceptionId || '—',
              },
              ...(
                [
                  ['parametersSummary', 'mcp.logs.parameters'],
                  ['outputSummary', 'mcp.logs.output'],
                  ['policySummary', 'mcp.logs.policy'],
                ] as const
              ).map(([field, label]) => ({
                key: field,
                label: t(label),
                children: (
                  <pre
                    style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}
                  >
                    {record[field] || '—'}
                  </pre>
                ),
              })),
            ]}
          />
        )}
      </Modal>
    </PageContainer>
  );
}

export default function McpLogs() {
  const access = useAccess();
  const { initialState } = useModel('@@initialState');
  const account = initialState?.currentUser?.userid || '';
  return access.canAdmin ? (
    <Workspace key={account} account={account} />
  ) : (
    <Result status="403" />
  );
}
