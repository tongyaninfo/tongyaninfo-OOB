import { ReloadOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Descriptions,
  Input,
  Modal,
  Select,
  Space,
  type TableProps,
  Tag,
  Typography,
} from 'antd';
import React, { useState } from 'react';
import {
  getMailMessage,
  getMailMessages,
  type MailQuery,
} from '@/services/oob/mail';
import type { MailMessageDto } from '@/services/oob/models';
import ManagedTable from '../components/ManagedTable';
import { formatTime } from '../format';
import { useOobIntl } from '../intl';
import {
  mailKindLabel,
  mailKinds,
  mailStatusColor,
  mailStatuses,
  mailStatusLabel,
} from './common';
import ReportHtml from './ReportHtml';

/** 发件列表只读本系统记录；普通正文保持文本，系统报表HTML只在受限沙箱中展示。 */
export default function Records() {
  const t = useOobIntl();
  const [params, setParams] = useState<MailQuery>({
    current: 1,
    pageSize: 20,
    sortField: 'createdAt',
    sortOrder: 'descend',
  });
  const [selected, setSelected] = useState<string>();
  const query = useQuery({
    queryKey: ['oob', 'mail', 'messages', params],
    queryFn: ({ signal }) => getMailMessages(params, signal),
    retry: false,
    refetchOnWindowFocus: false,
  });
  const detail = useQuery({
    queryKey: ['oob', 'mail', 'message', selected],
    queryFn: ({ signal }) => getMailMessage(selected || '', signal),
    enabled: Boolean(selected),
    retry: false,
    refetchOnWindowFocus: false,
  });
  const data = query.data?.success ? query.data.data : undefined;
  const record = detail.data?.success ? detail.data.data : undefined;
  const filter = (values: Partial<MailQuery>) =>
    setParams((previous) => ({ ...previous, ...values, current: 1 }));
  const sorted = (field: MailQuery['sortField']) =>
    params.sortField === field ? params.sortOrder : null;
  const columns: TableProps<MailMessageDto>['columns'] = [
    {
      title: t('oob.mail.recipient'),
      dataIndex: 'toAddress',
      sorter: true,
      sortOrder: sorted('toAddress'),
      ellipsis: true,
    },
    {
      title: t('oob.mail.subject'),
      dataIndex: 'subject',
      sorter: true,
      sortOrder: sorted('subject'),
      ellipsis: true,
    },
    {
      title: t('oob.mail.source'),
      dataIndex: 'kind',
      sorter: true,
      sortOrder: sorted('kind'),
      render: (value) => mailKindLabel(value, t),
    },
    {
      title: t('oob.mail.status'),
      dataIndex: 'status',
      sorter: true,
      sortOrder: sorted('status'),
      render: (value) => (
        <Tag color={mailStatusColor(value)}>{mailStatusLabel(value, t)}</Tag>
      ),
    },
    {
      title: t('oob.mail.sentAt'),
      dataIndex: 'completedAt',
      sorter: true,
      sortOrder: sorted('completedAt'),
      render: (value) => formatTime(value),
    },
    {
      title: t('oob.mail.createdAt'),
      dataIndex: 'createdAt',
      sorter: true,
      sortOrder: sorted('createdAt'),
      render: (value) => formatTime(value),
    },
    { title: t('oob.mail.result'), dataIndex: 'message', ellipsis: true },
    {
      title: t('oob.actions'),
      key: 'actions',
      render: (_, row) => (
        <Button type="link" onClick={() => setSelected(row.id)}>
          {t('oob.details')}
        </Button>
      ),
    },
  ];
  const interactive = (target: EventTarget | null) =>
    target instanceof HTMLElement &&
    Boolean(
      target.closest(
        'button,a,input,textarea,select,[role=button],[role=combobox]',
      ),
    );
  return (
    <>
      <Space wrap style={{ marginBottom: 16, width: '100%' }}>
        <Input.Search
          allowClear
          aria-label={t('oob.mail.search')}
          placeholder={t('oob.mail.search')}
          style={{ maxWidth: 360 }}
          onSearch={(keyword) => filter({ keyword })}
        />
        <Select
          allowClear
          aria-label={t('oob.mail.status')}
          placeholder={t('oob.mail.status')}
          style={{ width: 180 }}
          value={params.status}
          onChange={(status) => filter({ status })}
          options={mailStatuses.map((value) => ({
            value,
            label: mailStatusLabel(value, t),
          }))}
        />
        <Select
          allowClear
          aria-label={t('oob.mail.source')}
          placeholder={t('oob.mail.source')}
          style={{ width: 140 }}
          value={params.kind}
          onChange={(kind) => filter({ kind })}
          options={mailKinds.map((value) => ({
            value,
            label: mailKindLabel(value, t),
          }))}
        />
        <Button
          icon={<ReloadOutlined />}
          loading={query.isFetching}
          onClick={() => void query.refetch()}
        >
          {t('oob.refresh')}
        </Button>
      </Space>
      {query.data?.success === false && (
        <Alert
          type="error"
          showIcon
          title={query.data.errorMessage}
          style={{ marginBottom: 16 }}
        />
      )}
      <ManagedTable<MailMessageDto>
        tableId="oob.mail.messages.v1"
        rowKey="id"
        loading={query.isFetching}
        dataSource={data?.items || []}
        columns={columns}
        locale={{ emptyText: t('oob.mail.empty') }}
        scroll={{ x: 1180 }}
        pagination={{
          current: params.current,
          pageSize: params.pageSize,
          total: data?.total || 0,
          showSizeChanger: true,
          onChange: (current, pageSize) =>
            setParams((previous) => ({
              ...previous,
              current: pageSize === previous.pageSize ? current : 1,
              pageSize,
            })),
        }}
        onChange={(_, __, sorter, extra) => {
          if (extra.action !== 'sort') return;
          const value = Array.isArray(sorter) ? sorter[0] : sorter;
          filter({
            sortField: value.order
              ? (value.field as MailQuery['sortField'])
              : 'createdAt',
            sortOrder: value.order || 'descend',
          });
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
              !event.shiftKey &&
              ['Enter', ' '].includes(event.key)
            ) {
              event.preventDefault();
              setSelected(row.id);
            }
          },
        })}
      />
      <Modal
        open={Boolean(selected)}
        title={t('oob.mail.details')}
        width={800}
        onCancel={() => setSelected(undefined)}
        footer={
          <Space>
            <Button
              loading={detail.isFetching}
              onClick={() => void detail.refetch()}
            >
              {t('oob.mail.checkStatus')}
            </Button>
            <Button onClick={() => setSelected(undefined)}>
              {t('oob.cancel')}
            </Button>
          </Space>
        }
      >
        {detail.isFetching && (
          <Typography.Paragraph>{t('oob.mail.loading')}</Typography.Paragraph>
        )}
        {detail.data?.success === false && (
          <Alert type="error" showIcon title={detail.data.errorMessage} />
        )}
        {record && (
          <>
            {record.status === 'Unknown' && (
              <Alert
                type="warning"
                showIcon
                title={t('oob.mail.unknown')}
                style={{ marginBottom: 16 }}
              />
            )}
            {record.status === 'Accepted' && (
              <Alert
                type="success"
                showIcon
                title={t('oob.mail.accepted')}
                description={t('oob.mail.acceptedHelp')}
                style={{ marginBottom: 16 }}
              />
            )}
            <Descriptions
              bordered
              style={{ overflowWrap: 'anywhere' }}
              column={{ xs: 1, md: 2 }}
              size="small"
              items={[
                {
                  key: 'sender',
                  label: t('oob.mail.sender'),
                  children: record.fromAddress,
                },
                {
                  key: 'recipient',
                  label: t('oob.mail.recipient'),
                  children: record.toAddress,
                },
                {
                  key: 'subject',
                  label: t('oob.mail.subject'),
                  children: record.subject,
                  span: 'filled',
                },
                {
                  key: 'source',
                  label: t('oob.mail.source'),
                  children: mailKindLabel(record.kind, t),
                },
                {
                  key: 'status',
                  label: t('oob.mail.status'),
                  children: (
                    <Tag color={mailStatusColor(record.status)}>
                      {mailStatusLabel(record.status, t)}
                    </Tag>
                  ),
                },
                {
                  key: 'created',
                  label: t('oob.mail.createdAt'),
                  children: formatTime(record.createdAt),
                },
                {
                  key: 'completed',
                  label: t('oob.mail.sentAt'),
                  children: formatTime(record.completedAt),
                },
                {
                  key: 'message',
                  label: t('oob.mail.result'),
                  children: record.message || '—',
                  span: 'filled',
                },
                {
                  key: 'request',
                  label: t('oob.mail.requestId'),
                  children: (
                    <Typography.Text copyable>
                      {record.requestId}
                    </Typography.Text>
                  ),
                  span: 'filled',
                },
              ]}
            />
            <Typography.Title level={5}>{t('oob.mail.body')}</Typography.Title>
            {record.htmlBody && (
              <ReportHtml
                html={record.htmlBody}
                title={t('oob.mail.reports.previewTitle')}
              />
            )}
            <pre
              style={{
                whiteSpace: 'pre-wrap',
                overflowWrap: 'anywhere',
                maxHeight: 360,
                overflow: 'auto',
              }}
            >
              {record.textBody || '—'}
            </pre>
          </>
        )}
      </Modal>
    </>
  );
}
