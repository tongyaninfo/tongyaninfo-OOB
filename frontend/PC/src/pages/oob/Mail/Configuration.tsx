import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Collapse,
  Form,
  Input,
  InputNumber,
  Select,
  Space,
  Spin,
  Typography,
} from 'antd';
import React, { useEffect, useRef, useState } from 'react';
import {
  getMailConfiguration,
  probeMailConnection,
  saveMailConfiguration,
} from '@/services/oob/mail';
import type {
  MailConfigurationDto,
  MailConfigurationRequest,
} from '@/services/oob/models';
import { sessionGeneration } from '@/services/oob/session';
import { useOobIntl } from '../intl';
import SendMailModal from './SendMailModal';

/** 配置保存、协议连接检测和真正发送分别由三个显式动作触发。 */
export default function Configuration({
  onChanged,
}: {
  onChanged: () => void;
}) {
  const t = useOobIntl();
  const { message } = App.useApp();
  const [form] = Form.useForm<MailConfigurationRequest>();
  const client = useQueryClient();
  const [configuration, setConfiguration] = useState<MailConfigurationDto>();
  const [pending, setPending] = useState<'save' | 'probe'>();
  const [dirty, setDirty] = useState(false);
  const [blocked, setBlocked] = useState(false);
  const [error, setError] = useState('');
  const [probeResult, setProbeResult] = useState<{
    connected: boolean;
    message: string;
  }>();
  const [testing, setTesting] = useState(false);
  const active = useRef(true);
  const writing = useRef(false);
  useEffect(() => {
    active.current = true;
    return () => {
      active.current = false;
    };
  }, []);
  const query = useQuery({
    queryKey: ['oob', 'mail', 'configuration'],
    queryFn: ({ signal }) => getMailConfiguration(signal),
    retry: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  const loadedConfiguration = query.data?.success ? query.data.data : undefined;
  const restore = (value: MailConfigurationDto) => {
    form.setFieldsValue({
      host: value.host,
      fromAddress: value.fromAddress,
      username: value.username,
      port: value.port || 465,
      securityMode: value.securityMode || 'SslOnConnect',
      password: undefined,
      version: value.version,
    });
    setConfiguration(value);
    setDirty(false);
    setBlocked(false);
  };
  useEffect(() => {
    // 后台重查不能覆盖正在编辑的草稿，也不能替换草稿绑定的并发版本。
    // 只有首次读取、明确重新载入及本次保存成功才建立新的表单基线。
    if (!configuration && loadedConfiguration) restore(loadedConfiguration);
  }, [configuration, loadedConfiguration, form]);
  const reload = async () => {
    setError('');
    setProbeResult(undefined);
    const expectedSession = sessionGeneration();
    const result = await query.refetch();
    if (
      active.current &&
      expectedSession === sessionGeneration() &&
      result.data?.success
    )
      restore(result.data.data);
  };
  const save = async (values: MailConfigurationRequest) => {
    if (writing.current || blocked || !configuration) return;
    writing.current = true;
    setPending('save');
    setError('');
    setProbeResult(undefined);
    const expectedSession = sessionGeneration();
    try {
      const result = await saveMailConfiguration({
        ...values,
        host: values.host.trim(),
        fromAddress: values.fromAddress.trim(),
        username: values.username?.trim() || undefined,
        password: values.password || undefined,
        version: configuration.version,
      });
      if (!active.current || expectedSession !== sessionGeneration()) return;
      if (result.success) {
        restore(result.data);
        client.setQueryData(['oob', 'mail', 'configuration'], result);
        message.success(t('oob.mail.saved'));
      } else {
        const unknown =
          result.dispatched &&
          (result.kind !== 'business' || result.status >= 500);
        setBlocked(unknown || result.status === 409);
        setError(unknown ? t('oob.mail.saveUnknown') : result.errorMessage);
      }
    } finally {
      writing.current = false;
      if (active.current) setPending(undefined);
    }
  };
  const probe = async () => {
    if (writing.current || dirty || !configuration?.configured) return;
    writing.current = true;
    setPending('probe');
    setError('');
    setProbeResult(undefined);
    const expectedSession = sessionGeneration();
    try {
      const result = await probeMailConnection();
      if (!active.current || expectedSession !== sessionGeneration()) return;
      if (result.success)
        setProbeResult({
          connected: result.data.connected,
          message: result.data.message,
        });
      else setError(result.errorMessage);
    } finally {
      writing.current = false;
      if (active.current) setPending(undefined);
    }
  };
  if (!configuration && query.isPending)
    return <Spin aria-label={t('oob.mail.loading')} />;
  if (!configuration)
    return (
      <Alert
        type="error"
        showIcon
        title={
          query.data?.success === false
            ? query.data.errorMessage
            : t('oob.load.failed')
        }
        action={
          <Button onClick={() => void query.refetch()}>{t('oob.retry')}</Button>
        }
      />
    );
  return (
    <div style={{ maxWidth: 640 }}>
      <Typography.Paragraph type="secondary">
        {t('oob.mail.saveHelp')}
      </Typography.Paragraph>
      {error && (
        <Alert
          type="error"
          showIcon
          title={error}
          style={{ marginBottom: 16 }}
        />
      )}
      {query.data?.success === false && (
        <Alert
          type="error"
          showIcon
          title={query.data.errorMessage}
          style={{ marginBottom: 16 }}
        />
      )}
      {probeResult && (
        <Alert
          type={probeResult.connected ? 'success' : 'error'}
          showIcon
          title={
            probeResult.connected
              ? t('oob.mail.connected')
              : probeResult.message
          }
          style={{ marginBottom: 16 }}
        />
      )}
      <Form
        form={form}
        layout="vertical"
        disabled={Boolean(pending) || query.isFetching}
        onFinish={(values) => void save(values)}
        onValuesChange={() => {
          setDirty(true);
          setProbeResult(undefined);
        }}
      >
        <Form.Item
          name="host"
          label={t('oob.mail.host')}
          rules={[
            {
              required: true,
              whitespace: true,
              message: t('oob.mail.required'),
            },
          ]}
        >
          <Input maxLength={253} autoComplete="off" />
        </Form.Item>
        <Form.Item
          name="fromAddress"
          label={t('oob.mail.sender')}
          rules={[
            { required: true, message: t('oob.mail.required') },
            { type: 'email', message: t('oob.mail.invalidEmail') },
          ]}
        >
          <Input maxLength={320} autoComplete="off" />
        </Form.Item>
        <Form.Item
          name="password"
          label={t('oob.mail.password')}
          rules={[
            {
              required: !configuration.passwordConfigured,
              message: t('oob.mail.passwordRequired'),
            },
          ]}
        >
          <Input.Password
            autoComplete="new-password"
            placeholder={
              configuration.passwordConfigured
                ? t('oob.mail.passwordKeep')
                : undefined
            }
            maxLength={512}
          />
        </Form.Item>
        <Collapse
          style={{ marginBottom: 24 }}
          items={[
            {
              key: 'advanced',
              label: t('oob.mail.advanced'),
              forceRender: true,
              children: (
                <>
                  <Form.Item
                    name="port"
                    label={t('oob.mail.port')}
                    rules={[
                      { required: true, message: t('oob.mail.required') },
                    ]}
                  >
                    <InputNumber
                      min={1}
                      max={65535}
                      precision={0}
                      style={{ width: '100%' }}
                    />
                  </Form.Item>
                  <Form.Item
                    name="securityMode"
                    label={t('oob.mail.security')}
                    rules={[
                      { required: true, message: t('oob.mail.required') },
                    ]}
                  >
                    <Select
                      options={[
                        {
                          value: 'SslOnConnect',
                          label: t('oob.mail.security.ssl'),
                        },
                        {
                          value: 'StartTls',
                          label: t('oob.mail.security.starttls'),
                        },
                      ]}
                    />
                  </Form.Item>
                  <Form.Item name="username" label={t('oob.mail.username')}>
                    <Input
                      placeholder={t('oob.mail.usernameHint')}
                      maxLength={320}
                      autoComplete="off"
                    />
                  </Form.Item>
                </>
              ),
            },
          ]}
        />
        <Space wrap>
          <Button
            type="primary"
            htmlType="submit"
            loading={pending === 'save'}
            disabled={blocked || Boolean(pending) || query.isFetching}
          >
            {t('oob.mail.save')}
          </Button>
          <Button
            loading={pending === 'probe'}
            disabled={
              !configuration.configured ||
              dirty ||
              blocked ||
              Boolean(pending) ||
              query.isFetching
            }
            onClick={() => void probe()}
          >
            {t('oob.mail.connect')}
          </Button>
          <Button
            disabled={
              !configuration.configured ||
              dirty ||
              blocked ||
              Boolean(pending) ||
              query.isFetching
            }
            onClick={() => setTesting(true)}
          >
            {t('oob.mail.testSend')}
          </Button>
          <Button
            disabled={Boolean(pending)}
            loading={query.isFetching}
            onClick={() => void reload()}
          >
            {t('oob.mail.reload')}
          </Button>
        </Space>
      </Form>
      {dirty && (
        <Typography.Paragraph type="secondary" style={{ marginTop: 16 }}>
          {t('oob.mail.configChanged')}
        </Typography.Paragraph>
      )}
      {!configuration.configured && (
        <Typography.Paragraph type="secondary" style={{ marginTop: 16 }}>
          {t('oob.mail.unsaved')}
        </Typography.Paragraph>
      )}
      <SendMailModal
        open={testing}
        test
        onClose={() => setTesting(false)}
        onChanged={onChanged}
      />
    </div>
  );
}
