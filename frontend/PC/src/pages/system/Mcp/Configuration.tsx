import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Descriptions,
  Form,
  InputNumber,
  Select,
  Space,
  Spin,
  Tag,
  Typography,
} from 'antd';
import React, { useEffect, useState } from 'react';
import { useOobIntl } from '@/pages/oob/intl';
import {
  getMcpConfiguration,
  saveMcpConfiguration,
  setMcpEnabled,
} from '@/services/oob/mcp';
import type {
  McpConfigurationDto,
  McpConfigurationRequest,
  McpToolDto,
} from '@/services/oob/models';
import { outcomeUnknown, requiresReload, useRequestScope } from './common';

export default function Configuration({
  tools,
  account,
}: {
  tools: McpToolDto[];
  account: string;
}) {
  const t = useOobIntl();
  const { message, modal } = App.useApp();
  const client = useQueryClient();
  const [form] = Form.useForm<McpConfigurationRequest>();
  const [configuration, setConfiguration] = useState<McpConfigurationDto>();
  const [pending, setPending] = useState<'save' | 'state'>();
  const [dirty, setDirty] = useState(false);
  const [blocked, setBlocked] = useState(false);
  const [error, setError] = useState('');
  const scope = useRequestScope();
  const key = ['mcp', account, 'configuration'];
  const query = useQuery({
    queryKey: key,
    queryFn: ({ signal }) => getMcpConfiguration(signal),
    retry: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  const loaded = query.data?.success ? query.data.data : undefined;
  const restore = (value: McpConfigurationDto) => {
    setConfiguration(value);
    form.setFieldsValue({
      expectedVersion: value.version,
      requestsPerMinute: value.requestsPerMinute,
      clientConcurrency: value.clientConcurrency,
      globalConcurrency: value.globalConcurrency,
      queryTimeoutSeconds: value.queryTimeoutSeconds,
      maxRequestBytes: value.maxRequestBytes,
      maxResponseBytes: value.maxResponseBytes,
      enabledTools: value.enabledTools,
    });
    setDirty(false);
    setBlocked(false);
    setError('');
  };
  useEffect(() => {
    // 后台读取不能覆盖当前草稿与其并发版本，明确重载才建立新基线。
    if (!configuration && loaded) restore(loaded);
  }, [configuration, loaded, form]);
  const reload = async () => {
    const current = scope.capture();
    const result = await query.refetch();
    if (current() && result.data?.success) restore(result.data.data);
  };
  const write = async (values?: McpConfigurationRequest) => {
    if (
      !scope.active.current ||
      scope.pending.current ||
      blocked ||
      !configuration
    )
      return;
    scope.pending.current = true;
    setPending(values ? 'save' : 'state');
    setError('');
    const current = scope.capture();
    try {
      const result = values
        ? await saveMcpConfiguration({
            ...values,
            expectedVersion: configuration.version,
          })
        : await setMcpEnabled(!configuration.enabled, {
            expectedVersion: configuration.version,
          });
      if (!current()) return;
      if (result.success) {
        restore(result.data);
        client.setQueryData(key, result);
        void client.invalidateQueries({ queryKey: ['mcp', account, 'tools'] });
        message.success(t('mcp.saved'));
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
    } finally {
      scope.pending.current = false;
      if (current()) setPending(undefined);
    }
  };
  if (!configuration && query.isPending)
    return <Spin aria-label={t('mcp.loading')} />;
  if (!configuration)
    return (
      <Alert
        type="error"
        showIcon
        title={
          query.data?.success === false
            ? query.data.errorMessage
            : t('mcp.failed')
        }
        action={
          <Button onClick={() => void reload()}>{t('mcp.reload')}</Button>
        }
      />
    );
  return (
    <Space orientation="vertical" style={{ width: '100%' }} size="middle">
      <Typography.Paragraph>{t('mcp.saveHelp')}</Typography.Paragraph>
      {!configuration.supported && (
        <Alert showIcon type="info" title={t('mcp.desktop')} />
      )}
      <Descriptions
        bordered
        column={{ xs: 1, md: 2 }}
        items={[
          {
            key: 'state',
            label: t('mcp.serviceState'),
            children: (
              <Tag color={configuration.enabled ? 'success' : 'default'}>
                {t(configuration.enabled ? 'mcp.enabled' : 'mcp.disabled')}
              </Tag>
            ),
          },
          {
            key: 'ready',
            label: t('mcp.status'),
            children: (
              <Tag color={configuration.ready ? 'success' : 'warning'}>
                {t(configuration.ready ? 'mcp.available' : 'mcp.unavailable')}
              </Tag>
            ),
          },
          {
            key: 'endpoint',
            label: t('mcp.publicUrl'),
            children: configuration.endpoint || '—',
          },
          {
            key: 'versions',
            label: t('mcp.protocols'),
            children: configuration.protocolVersions.join(', ') || '—',
          },
          {
            key: 'readonly',
            label: t('mcp.readonly'),
            children: t('mcp.readonlyHelp'),
            span: 2,
          },
          {
            key: 'version',
            label: t('mcp.version'),
            children: configuration.version,
          },
        ]}
      />
      {configuration.readinessErrors.length > 0 && (
        <Alert
          showIcon
          type="warning"
          title={t('mcp.unavailable')}
          description={
            <ul>
              {configuration.readinessErrors.map((item) => (
                <li key={item}>{item}</li>
              ))}
            </ul>
          }
        />
      )}
      {error && <Alert showIcon type="error" title={error} />}
      <Form
        form={form}
        layout="vertical"
        disabled={
          Boolean(pending) || query.isFetching || !configuration.supported
        }
        onValuesChange={() => setDirty(true)}
        onFinish={(values) => void write(values)}
        style={{ maxWidth: 720 }}
      >
        {(
          [
            ['requestsPerMinute', 'mcp.rateLimit', 1, 6000],
            ['clientConcurrency', 'mcp.clientConcurrency', 1, 32],
            ['globalConcurrency', 'mcp.globalConcurrency', 1, 128],
            ['queryTimeoutSeconds', 'mcp.timeout', 1, 120],
            ['maxRequestBytes', 'mcp.requestLimit', 1024, 1048576],
            ['maxResponseBytes', 'mcp.responseLimit', 1024, 8388608],
          ] as const
        ).map(([name, label, min, max]) => (
          <Form.Item
            key={name}
            name={name}
            label={t(label)}
            dependencies={
              name === 'globalConcurrency' ? ['clientConcurrency'] : undefined
            }
            rules={[
              { required: true, message: t('mcp.required') },
              ...(name === 'globalConcurrency'
                ? [
                    {
                      validator: (_rule: unknown, value: number) =>
                        value >= form.getFieldValue('clientConcurrency')
                          ? Promise.resolve()
                          : Promise.reject(
                              new Error(t('mcp.concurrencyInvalid')),
                            ),
                    },
                  ]
                : []),
            ]}
          >
            <InputNumber
              min={min}
              max={max}
              precision={0}
              style={{ width: '100%' }}
            />
          </Form.Item>
        ))}
        <Form.Item
          name="enabledTools"
          label={t('mcp.globalTools')}
          extra={t('mcp.emptyTools')}
        >
          <Select
            mode="multiple"
            allowClear
            showSearch={{ optionFilterProp: 'label' }}
            options={tools.map((tool) => ({
              value: tool.name,
              label: `${tool.name} · ${tool.description}`,
            }))}
          />
        </Form.Item>
        <Space wrap>
          <Button
            type="primary"
            htmlType="submit"
            loading={pending === 'save'}
            disabled={
              blocked ||
              Boolean(pending) ||
              query.isFetching ||
              !configuration.supported
            }
          >
            {t('mcp.save')}
          </Button>
          <Button
            danger={configuration.enabled}
            loading={pending === 'state'}
            disabled={
              blocked ||
              dirty ||
              Boolean(pending) ||
              query.isFetching ||
              !configuration.supported
            }
            onClick={() =>
              modal.confirm({
                title: t(
                  configuration.enabled
                    ? 'mcp.disableConfirm'
                    : 'mcp.enableConfirm',
                ),
                content: t('mcp.serviceConfirm'),
                okText: t(configuration.enabled ? 'mcp.disable' : 'mcp.enable'),
                cancelText: t('mcp.cancel'),
                okButtonProps: { danger: configuration.enabled },
                onOk: () => write(),
              })
            }
          >
            {t(configuration.enabled ? 'mcp.disable' : 'mcp.enable')}
          </Button>
          <Button
            disabled={Boolean(pending)}
            loading={query.isFetching}
            onClick={() => void reload()}
          >
            {t('mcp.reload')}
          </Button>
        </Space>
      </Form>
      {dirty && (
        <Typography.Text type="secondary">{t('mcp.dirty')}</Typography.Text>
      )}
    </Space>
  );
}
