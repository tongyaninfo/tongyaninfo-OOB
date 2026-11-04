import { CopyOutlined, ReloadOutlined } from '@ant-design/icons';
import { PageContainer } from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Descriptions,
  Input,
  Modal,
  Select,
  Space,
  Tag,
  Typography,
} from 'antd';
import React, { useState } from 'react';
import { getException, getExceptions } from '@/services/oob/api';
import { captureRuntimeException } from '@/services/oob/browserExceptions';
import type { ExceptionLogDto } from '@/services/oob/models';
import ManagedTable from '../components/ManagedTable';
import { formatTime } from '../format';
import { useOobIntl } from '../intl';

/** 仅查询服务端已脱敏的异常，不把业务拒绝伪装成异常，也不在页面中执行日志文本。 */
export default function Exceptions() {
  const t = useOobIntl();
  const { message } = App.useApp();
  const [keyword, setKeyword] = useState('');
  const [kind, setKind] = useState<string>();
  const [level, setLevel] = useState<string>();
  const [source, setSource] = useState('');
  const kinds = [
    'SystemException',
    'DeviceFailure',
    'ClientException',
  ] as const;
  const levels = ['Error', 'Warning', 'Critical'] as const;
  const kindLabel = (value: string) =>
    kinds.includes(value as (typeof kinds)[number])
      ? t(`oob.exceptions.kind.${value as (typeof kinds)[number]}`)
      : value;
  const levelLabel = (value: string) =>
    levels.includes(value as (typeof levels)[number])
      ? t(`oob.exceptions.level.${value as (typeof levels)[number]}`)
      : value;
  const [current, setCurrent] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const [selected, setSelected] = useState<string>();
  const query = useQuery({
    queryKey: [
      'oob',
      'exceptions',
      keyword,
      kind,
      level,
      source,
      current,
      pageSize,
    ],
    queryFn: ({ signal }) =>
      getExceptions(
        { keyword, kind, level, source, current, pageSize },
        signal,
      ),
    retry: false,
    refetchOnWindowFocus: false,
  });
  const detail = useQuery({
    queryKey: ['oob', 'exception', selected],
    queryFn: ({ signal }) => getException(selected || '', signal),
    enabled: Boolean(selected),
    retry: false,
    refetchOnWindowFocus: false,
  });
  const data = query.data?.success ? query.data.data : undefined;
  const record = detail.data?.success ? detail.data.data : undefined;
  const copy = async () => {
    if (!record) return;
    try {
      await navigator.clipboard.writeText(JSON.stringify(record, null, 2));
      message.success(t('oob.exceptions.copied'));
    } catch (error) {
      void captureRuntimeException(error);
      message.error(t('oob.exceptions.copyFailed'));
    }
  };
  const interactive = (target: EventTarget | null) =>
    target instanceof HTMLElement &&
    Boolean(
      target.closest(
        'button,a,input,textarea,select,[role=button],[role=combobox]',
      ),
    );
  return (
    <PageContainer
      title={t('oob.exceptions.title')}
      extra={
        <Button
          icon={<ReloadOutlined />}
          loading={query.isFetching}
          onClick={() => query.refetch()}
        >
          {t('oob.refresh')}
        </Button>
      }
    >
      <Typography.Paragraph type="secondary">
        {t('oob.exceptions.help')}
      </Typography.Paragraph>
      <Space wrap style={{ marginBottom: 16 }}>
        <Select
          allowClear
          aria-label={t('oob.exceptions.kind')}
          placeholder={t('oob.exceptions.kind')}
          style={{ width: 180 }}
          value={kind}
          onChange={(value) => {
            setKind(value);
            setCurrent(1);
          }}
          options={kinds.map((value) => ({ value, label: kindLabel(value) }))}
        />
        <Select
          allowClear
          aria-label={t('oob.exceptions.level')}
          placeholder={t('oob.exceptions.level')}
          style={{ width: 140 }}
          value={level}
          onChange={(value) => {
            setLevel(value);
            setCurrent(1);
          }}
          options={levels.map((value) => ({ value, label: levelLabel(value) }))}
        />
        <Input.Search
          allowClear
          placeholder={t('oob.source')}
          style={{ maxWidth: 180 }}
          onSearch={(value) => {
            setSource(value);
            setCurrent(1);
          }}
        />
        <Input.Search
          allowClear
          placeholder={t('oob.exceptions.search')}
          style={{ maxWidth: 420, marginBottom: 16 }}
          onSearch={(value) => {
            setKeyword(value);
            setCurrent(1);
          }}
        />
      </Space>
      {query.data?.success === false && (
        <Alert
          showIcon
          type="error"
          title={query.data.errorMessage}
          style={{ marginBottom: 16 }}
        />
      )}
      <ManagedTable<ExceptionLogDto>
        tableId="oob.exceptions.v1"
        rowKey="id"
        dataSource={data?.items || []}
        loading={query.isFetching}
        scroll={{ x: 1100 }}
        pagination={{
          current,
          pageSize,
          total: data?.total || 0,
          showSizeChanger: true,
          onChange: (page, size) => {
            setCurrent(size === pageSize ? page : 1);
            setPageSize(size);
          },
        }}
        onRow={(row) => ({
          tabIndex: 0,
          onClick: (event) => {
            if (!interactive(event.target)) setSelected(row.id);
          },
          onKeyDown: (event) => {
            if (
              !interactive(event.target) &&
              !event.repeat &&
              !event.nativeEvent.isComposing &&
              !event.altKey &&
              !event.ctrlKey &&
              !event.metaKey &&
              ['Enter', ' '].includes(event.key)
            ) {
              event.preventDefault();
              setSelected(row.id);
            }
          },
        })}
        columns={[
          {
            title: t('oob.exceptions.time'),
            dataIndex: 'occurredAt',
            render: (value) => formatTime(value),
          },
          {
            title: t('oob.exceptions.kind'),
            dataIndex: 'kind',
            render: kindLabel,
          },
          {
            title: t('oob.exceptions.level'),
            dataIndex: 'level',
            render: (value) => (
              <Tag color={value === 'Warning' ? 'warning' : 'error'}>
                {levelLabel(value)}
              </Tag>
            ),
          },
          { title: t('oob.source'), dataIndex: 'source' },
          { title: t('oob.exceptions.operation'), dataIndex: 'operation' },
          { title: t('oob.exceptions.code'), dataIndex: 'errorCode' },
          {
            title: t('oob.exceptions.message'),
            dataIndex: 'message',
            ellipsis: true,
          },
          {
            title: t('oob.actions'),
            key: 'actions',
            render: (_, row) => (
              <Button type="link" onClick={() => setSelected(row.id)}>
                {t('oob.details')}
              </Button>
            ),
          },
        ]}
      />
      <Modal
        open={Boolean(selected)}
        title={t('oob.exceptions.details')}
        width={1000}
        onCancel={() => setSelected(undefined)}
        footer={
          <Space>
            <Button
              aria-label={t('oob.exceptions.copy')}
              icon={<CopyOutlined />}
              disabled={!record}
              onClick={() => void copy()}
            >
              {t('oob.exceptions.copy')}
            </Button>
            <Button onClick={() => setSelected(undefined)}>
              {t('oob.cancel')}
            </Button>
          </Space>
        }
      >
        {detail.isFetching && (
          <Typography.Text>{t('oob.exceptions.loading')}</Typography.Text>
        )}
        {detail.data?.success === false && (
          <Alert showIcon type="error" title={detail.data.errorMessage} />
        )}
        {record && (
          <>
            <Descriptions
              column={{ xs: 1, md: 2 }}
              bordered
              size="small"
              items={[
                {
                  key: 'time',
                  label: t('oob.exceptions.time'),
                  children: formatTime(record.occurredAt),
                },
                {
                  key: 'type',
                  label: t('oob.exceptions.type'),
                  children: record.exceptionType || '-',
                },
                {
                  key: 'source',
                  label: t('oob.source'),
                  children: record.source,
                },
                {
                  key: 'op',
                  label: t('oob.exceptions.operation'),
                  children: record.operation,
                },
                {
                  key: 'code',
                  label: t('oob.exceptions.code'),
                  children: record.errorCode,
                },
                {
                  key: 'trace',
                  label: 'Trace ID',
                  children: record.traceId || '-',
                },
                {
                  key: 'server',
                  label: t('oob.exceptions.serverId'),
                  children: record.serverId || '-',
                },
                {
                  key: 'path',
                  label: t('oob.exceptions.path'),
                  children:
                    [
                      record.requestMethod,
                      record.requestPath,
                      record.httpStatusCode,
                    ]
                      .filter((value) => value != null)
                      .join(' ') || '-',
                },
                {
                  key: 'message',
                  label: t('oob.exceptions.message'),
                  children: record.message,
                  span: 'filled',
                },
              ]}
            />
            <Typography.Title level={5}>
              {t('oob.exceptions.stack')}
            </Typography.Title>
            <pre
              style={{
                overflow: 'auto',
                maxHeight: 400,
                whiteSpace: 'pre-wrap',
                overflowWrap: 'anywhere',
              }}
            >
              {record.stackTrace || '-'}
            </pre>
          </>
        )}
      </Modal>
    </PageContainer>
  );
}
