import { useQuery } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Form,
  Input,
  Modal,
  Select,
  Space,
  Spin,
  Typography,
} from 'antd';
import React, { useEffect, useState } from 'react';
import { formatTime } from '@/pages/oob/format';
import { useOobIntl } from '@/pages/oob/intl';
import { getMcpClient, getMcpOptions, saveMcpClient } from '@/services/oob/mcp';
import type {
  McpClientDto,
  McpClientRequest,
  McpToolDto,
} from '@/services/oob/models';
import {
  lines,
  outcomeUnknown,
  requiresReload,
  useRequestScope,
  validNetwork,
  validOrigin,
  validRedirect,
} from './common';

// 仅表单展示字段派生自正式请求，业务字段仍由生成合同拥有。
type Values = Omit<
  McpClientRequest,
  'networks' | 'origins' | 'redirectUris' | 'expiresAt'
> & {
  networksText: string;
  originsText: string;
  redirectUrisText: string;
  expiresLocal?: string;
};
type Props = {
  id?: string;
  account: string;
  tools: McpToolDto[];
  onClose: () => void;
  onSaved: () => Promise<void>;
};
export default function ClientEditor({
  id,
  account,
  tools,
  onClose,
  onSaved,
}: Props) {
  const t = useOobIntl();
  const { message } = App.useApp();
  const scope = useRequestScope();
  const [form] = Form.useForm<Values>();
  const [baseline, setBaseline] = useState<McpClientDto>();
  const [initialized, setInitialized] = useState(false);
  const [pending, setPending] = useState(false);
  const [blocked, setBlocked] = useState(false);
  const [error, setError] = useState('');
  const serverScope = Form.useWatch('serverScope', form);
  const query = useQuery({
    queryKey: ['mcp', account, 'client', id],
    queryFn: ({ signal }) => getMcpClient(id || '', signal),
    enabled: Boolean(id),
    retry: false,
    refetchOnWindowFocus: false,
  });
  const options = useQuery({
    queryKey: ['mcp', account, 'options'],
    queryFn: ({ signal }) => getMcpOptions(signal),
    retry: false,
    refetchOnWindowFocus: false,
  });
  const choices = options.data?.success ? options.data.data : undefined;
  const loaded = query.data?.success ? query.data.data : undefined;
  const restore = (value?: McpClientDto) => {
    setBaseline(value);
    form.setFieldsValue({
      name: value?.name || '',
      clientType: value?.clientType || 'public',
      userId: value?.userId || undefined,
      expectedVersion: value?.version || '0',
      serverScope: value?.serverScope || 'SelectedServers',
      tools: value?.tools || [],
      serverIds: value?.serverIds || [],
      networksText: value?.networks.join('\n') || '',
      originsText: value?.origins.join('\n') || '',
      redirectUrisText: value?.redirectUris.join('\n') || '',
      expiresLocal: value?.expiresAt
        ? formatTime(value.expiresAt).replace(' ', 'T')
        : '',
    });
    setInitialized(true);
    setBlocked(false);
    setError('');
  };
  useEffect(() => {
    if (!initialized && (!id || loaded)) restore(loaded);
  }, [id, loaded, initialized, form]);
  const reload = async () => {
    const current = scope.capture();
    const [record, optionResult] = await Promise.all([
      id ? query.refetch() : Promise.resolve(undefined),
      options.refetch(),
    ]);
    if (!current()) return;
    if (id && record?.data?.success) restore(record.data.data);
    if (!id && optionResult.data?.success && !blocked) setError('');
  };
  const submit = async (values: Values) => {
    if (scope.pending.current || blocked || !choices || !initialized) return;
    const expiresAt = values.expiresLocal
      ? new Date(`${values.expiresLocal}+08:00`).toISOString()
      : null;
    scope.pending.current = true;
    setPending(true);
    setError('');
    const current = scope.capture();
    try {
      const result = await saveMcpClient(
        {
          name: values.name.trim(),
          clientType: values.clientType,
          userId: values.userId,
          serverScope: values.serverScope,
          tools: values.tools || [],
          serverIds:
            values.serverScope === 'AllServers' ? [] : values.serverIds || [],
          networks: lines(values.networksText),
          origins: lines(values.originsText),
          redirectUris: lines(values.redirectUrisText),
          expiresAt,
          expectedVersion: baseline?.version || '0',
        },
        id,
      );
      if (!current()) return;
      if (result.success) {
        message.success(t('mcp.saved'));
        await onSaved();
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
      if (current()) setPending(false);
    }
  };
  const validateLines = (
    check: (value: string) => boolean,
    label: 'mcp.invalidIp' | 'mcp.invalidOrigin' | 'mcp.invalidRedirect',
  ) => ({
    validator: (_rule: unknown, value: string) =>
      lines(value).every(check)
        ? Promise.resolve()
        : Promise.reject(new Error(t(label))),
  });
  return (
    <Modal
      open
      title={t(id ? 'mcp.edit' : 'mcp.add')}
      width={820}
      onCancel={() => {
        if (!scope.pending.current) onClose();
      }}
      destroyOnHidden
      closable={!pending}
      keyboard={!pending}
      mask={{ closable: false }}
      footer={
        <Space>
          <Button disabled={pending} onClick={onClose}>
            {t('mcp.cancel')}
          </Button>
          <Button
            disabled={pending}
            loading={query.isFetching || options.isFetching}
            onClick={() => void reload()}
          >
            {t('mcp.reload')}
          </Button>
          <Button
            type="primary"
            loading={pending}
            disabled={
              blocked ||
              !choices ||
              !initialized ||
              options.isFetching ||
              query.isFetching
            }
            onClick={() => form.submit()}
          >
            {t('mcp.save')}
          </Button>
        </Space>
      }
    >
      <Typography.Paragraph type="secondary">
        {t('mcp.clientHelp')}
      </Typography.Paragraph>
      {!initialized && query.isFetching && (
        <Spin aria-label={t('mcp.loading')} />
      )}
      {error && (
        <Alert
          showIcon
          type="error"
          title={error}
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
      {options.data?.success === false && (
        <Alert
          showIcon
          type="error"
          title={t('mcp.optionsFailed')}
          description={options.data.errorMessage}
          style={{ marginBottom: 16 }}
        />
      )}
      {initialized && (
        <Form
          form={form}
          layout="vertical"
          disabled={
            pending || !choices || query.isFetching || options.isFetching
          }
          onFinish={(values) => void submit(values)}
        >
          {baseline && (
            <Form.Item label={t('mcp.clientId')}>
              <Input readOnly value={baseline.oAuthClientId} />
            </Form.Item>
          )}
          <Form.Item
            name="name"
            label={t('mcp.name')}
            rules={[
              { required: true, whitespace: true, message: t('mcp.required') },
            ]}
          >
            <Input maxLength={100} autoComplete="off" />
          </Form.Item>
          <Form.Item
            name="clientType"
            label={t('mcp.clientType')}
            extra={t('mcp.clientTypeHelp')}
            rules={[{ required: true, message: t('mcp.required') }]}
          >
            <Select
              options={[
                { value: 'public', label: t('mcp.publicClient') },
                { value: 'confidential', label: t('mcp.confidentialClient') },
              ]}
            />
          </Form.Item>
          <Form.Item
            name="userId"
            label={t('mcp.boundUser')}
            rules={[{ required: true, message: t('mcp.required') }]}
          >
            <Select
              showSearch={{ optionFilterProp: 'label' }}
              options={choices?.users.map((user) => ({
                value: user.id,
                label: `${user.displayName} (${user.username})${user.enabled ? '' : ` · ${t('mcp.disabled')}`}`,
                disabled: !user.enabled,
              }))}
            />
          </Form.Item>
          <Form.Item
            name="expiresLocal"
            label={t('mcp.expiresAt')}
            extra={t('mcp.expiryHint')}
            rules={[
              {
                validator: (_rule, value: string) =>
                  !value || !Number.isNaN(new Date(`${value}+08:00`).valueOf())
                    ? Promise.resolve()
                    : Promise.reject(new Error(t('mcp.required'))),
              },
            ]}
          >
            <Input type="datetime-local" step={1} />
          </Form.Item>
          <Form.Item
            name="tools"
            label={t('mcp.toolsAllowed')}
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
          <Form.Item
            name="serverScope"
            label={t('mcp.serverScope')}
            extra={t('mcp.allServersHint')}
            rules={[{ required: true, message: t('mcp.required') }]}
          >
            <Select
              options={[
                { value: 'SelectedServers', label: t('mcp.selectedServers') },
                { value: 'AllServers', label: t('mcp.allServers') },
              ]}
            />
          </Form.Item>
          {serverScope !== 'AllServers' && (
            <Form.Item name="serverIds" label={t('mcp.serverIds')}>
              <Select
                mode="multiple"
                allowClear
                showSearch={{ optionFilterProp: 'label' }}
                options={choices?.servers.map((server) => ({
                  value: server.id,
                  label: `${server.name} · ${server.brand}${server.enabled ? '' : ` · ${t('mcp.disabled')}`}`,
                }))}
              />
            </Form.Item>
          )}
          <Form.Item
            name="networksText"
            label={t('mcp.ipRanges')}
            extra={t('mcp.ipHint')}
            rules={[validateLines(validNetwork, 'mcp.invalidIp')]}
          >
            <Input.TextArea
              rows={3}
              maxLength={12000}
              placeholder={'192.0.2.0/24\n2001:db8::/32'}
            />
          </Form.Item>
          <Form.Item
            name="originsText"
            label={t('mcp.origins')}
            extra={t('mcp.originHint')}
            rules={[validateLines(validOrigin, 'mcp.invalidOrigin')]}
          >
            <Input.TextArea rows={2} maxLength={12000} />
          </Form.Item>
          <Form.Item
            name="redirectUrisText"
            label={t('mcp.redirectUris')}
            extra={t('mcp.redirectHint')}
            rules={[
              { required: true, whitespace: true, message: t('mcp.required') },
              validateLines(validRedirect, 'mcp.invalidRedirect'),
            ]}
          >
            <Input.TextArea rows={2} maxLength={12000} />
          </Form.Item>
        </Form>
      )}
    </Modal>
  );
}
