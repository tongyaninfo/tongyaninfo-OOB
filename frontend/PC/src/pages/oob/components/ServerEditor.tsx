import { useMutation } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Checkbox,
  Form,
  Input,
  InputNumber,
  Modal,
  Select,
  Space,
  Switch,
  Typography,
} from 'antd';
import React, { useEffect, useState } from 'react';
import { saveServer, testConnection } from '@/services/oob/api';
import { captureRuntimeException } from '@/services/oob/browserExceptions';
import { requestFailure } from '@/services/oob/http';
import type {
  ConnectionTestResult,
  SaveServerRequest,
  ServerDto,
} from '@/services/oob/models';
import { useOobIntl } from '../intl';

interface Props {
  open: boolean;
  server?: ServerDto;
  onClose: () => void;
  onSaved: () => void;
}

/** 表单不回填设备密码；编辑时空密码由后端保留原密文。 */
export default function ServerEditor({
  open,
  server,
  onClose,
  onSaved,
}: Props) {
  const t = useOobIntl();
  const { message } = App.useApp();
  const [form] = Form.useForm<SaveServerRequest>();
  const [tested, setTested] = useState<ConnectionTestResult>();
  const brand = Form.useWatch('brand', form) || 'HP';
  const address = Form.useWatch('address', form);
  const username = Form.useWatch('username', form);
  const controllerType = Form.useWatch('controllerType', form);
  const normalTarget = (value: string | undefined) =>
    (value || '')
      .trim()
      .replace(/\/$/, '')
      .replace(/^https:\/\//i, '')
      .toLowerCase();
  const credentialsChanged = Boolean(
    server &&
      (normalTarget(address) !== normalTarget(server.address) ||
        username !== server.username ||
        brand !== server.brand ||
        controllerType !== server.controllerType),
  );
  useEffect(() => {
    if (!open) return;
    // 同一服务器的后台采样会替换 DTO 引用，不能因此抹掉编辑草稿和连接测试反馈。
    // 仅重新打开或明确切换目标记录时，用该次最新保存值初始化表单。
    form.resetFields();
    form.setFieldsValue(
      server
        ? {
            name: server.name,
            brand: server.brand as 'HP' | 'DELL',
            controllerType: server.controllerType as 'iLO4' | 'iDRAC8',
            address: server.address,
            username: server.username,
            password: undefined,
            samplingIntervalSeconds: server.samplingIntervalSeconds,
            enabled: server.enabled,
            certificateFingerprint: server.certificateFingerprint || undefined,
            ignoreCertificateErrors: server.ignoreCertificateErrors ?? true,
            existingServerId: server.id,
          }
        : {
            brand: 'HP',
            controllerType: 'iLO4',
            samplingIntervalSeconds: 30,
            enabled: true,
            ignoreCertificateErrors: true,
          },
    );
    setTested(undefined);
  }, [open, server?.id, form]);

  /** 提交边界使用本次表单快照复核，不能只依赖异步 useWatch 更新后的必填规则。 */
  const ensureCredentials = (values: SaveServerRequest) => {
    const changed =
      server &&
      (normalTarget(values.address) !== normalTarget(server.address) ||
        values.username !== server.username ||
        values.brand !== server.brand ||
        values.controllerType !== server.controllerType);
    return changed && !values.password
      ? requestFailure('CREDENTIAL_REENTRY_REQUIRED', t('oob.password.reentry'))
      : null;
  };
  const save = useMutation({
    mutationFn: (values: SaveServerRequest) => {
      const invalid = ensureCredentials(values);
      if (invalid) return Promise.resolve(invalid);
      return saveServer(
        {
          ...values,
          ignoreCertificateErrors: values.ignoreCertificateErrors ?? true,
          existingServerId: server?.id,
        },
        server?.id,
      );
    },
    retry: false,
    onSuccess: (result) => {
      if (!result.success) {
        message.error(result.errorMessage);
        return;
      }
      message.success(t('oob.saved'));
      onSaved();
      onClose();
    },
    onError: (error: Error) => {
      void captureRuntimeException(error);
      message.error(error.message || t('oob.error'));
    },
  });
  const test = useMutation({
    mutationFn: (values: SaveServerRequest) => {
      const invalid = ensureCredentials(values);
      if (invalid) return Promise.resolve(invalid);
      return testConnection({
        ...values,
        ignoreCertificateErrors: values.ignoreCertificateErrors ?? true,
        existingServerId: server?.id,
      });
    },
    retry: false,
    onSuccess: (result) => {
      if (result.success) setTested(result.data);
      else message.error(result.errorMessage);
    },
    onError: (error: Error) => {
      void captureRuntimeException(error);
      message.error(error.message || t('oob.error'));
    },
  });
  const busy = save.isPending || test.isPending;
  return (
    <Modal
      open={open}
      title={t(server ? 'oob.edit' : 'oob.add')}
      onCancel={busy ? undefined : onClose}
      mask={{ closable: !busy }}
      closable={!busy}
      width={680}
      destroyOnHidden
      footer={
        <Space>
          <Button disabled={busy} onClick={onClose}>
            {t('oob.cancel')}
          </Button>
          <Button
            loading={test.isPending}
            disabled={save.isPending}
            onClick={() =>
              form
                .validateFields()
                .then((values) => test.mutate(values))
                .catch(() => undefined)
            }
          >
            {t('oob.test')}
          </Button>
          <Button
            type="primary"
            loading={save.isPending}
            disabled={test.isPending}
            onClick={() => form.submit()}
          >
            {t('oob.save')}
          </Button>
        </Space>
      }
    >
      <Form
        form={form}
        layout="vertical"
        onFinish={(values) => save.mutate(values)}
        onValuesChange={(changed) => {
          setTested(undefined);
          if (changed.brand)
            form.setFieldValue(
              'controllerType',
              changed.brand === 'HP' ? 'iLO4' : 'iDRAC8',
            );
        }}
      >
        <Form.Item
          name="name"
          label={t('oob.name')}
          rules={[
            { required: true, whitespace: true, message: t('oob.required') },
          ]}
        >
          <Input maxLength={100} />
        </Form.Item>
        <Space align="start" style={{ display: 'flex' }}>
          <Form.Item
            name="brand"
            label={t('oob.brand')}
            rules={[{ required: true }]}
          >
            <Select
              style={{ width: 180 }}
              options={[
                { value: 'HP', label: 'HP' },
                { value: 'DELL', label: 'DELL' },
              ]}
            />
          </Form.Item>
          <Form.Item
            name="controllerType"
            label={t('oob.controller')}
            rules={[{ required: true }]}
          >
            <Select
              style={{ width: 180 }}
              options={[
                {
                  value: brand === 'HP' ? 'iLO4' : 'iDRAC8',
                  label: brand === 'HP' ? 'iLO4' : 'iDRAC 8',
                },
              ]}
            />
          </Form.Item>
        </Space>
        <Form.Item
          name="address"
          label={t('oob.address')}
          rules={[
            { required: true, whitespace: true, message: t('oob.required') },
          ]}
        >
          <Input autoComplete="off" placeholder="https://192.0.2.10" />
        </Form.Item>
        <Form.Item
          name="username"
          label={t('oob.username')}
          rules={[
            { required: true, whitespace: true, message: t('oob.required') },
          ]}
        >
          <Input autoComplete="off" />
        </Form.Item>
        <Form.Item
          name="password"
          label={t('oob.password')}
          extra={credentialsChanged ? t('oob.password.reentry') : undefined}
          rules={[
            {
              required: !server || credentialsChanged,
              message: t('oob.required'),
            },
          ]}
        >
          <Input.Password
            autoComplete="new-password"
            placeholder={server ? t('oob.password.keep') : undefined}
          />
        </Form.Item>
        <Form.Item
          name="samplingIntervalSeconds"
          label={t('oob.interval')}
          extra={t('oob.interval.help')}
          rules={[{ required: true, type: 'number', min: 1, max: 86400 }]}
        >
          <InputNumber
            min={1}
            max={86400}
            precision={0}
            style={{ width: '100%' }}
          />
        </Form.Item>
        <Form.Item
          name="enabled"
          label={t('oob.enabled')}
          valuePropName="checked"
        >
          <Switch />
        </Form.Item>
        <Form.Item
          name="ignoreCertificateErrors"
          valuePropName="checked"
          extra={t('oob.ignoreCertificateErrors.help')}
        >
          <Checkbox disabled={busy}>
            {t('oob.ignoreCertificateErrors')}
          </Checkbox>
        </Form.Item>
        <Form.Item
          name="certificateFingerprint"
          label={t('oob.fingerprint')}
          extra={t('oob.fingerprint.help')}
        >
          <Input autoComplete="off" />
        </Form.Item>
        <Typography.Text type="secondary">{t('oob.test.help')}</Typography.Text>
        {tested && (
          <Alert
            style={{ marginTop: 16 }}
            showIcon
            type={tested.success ? 'success' : 'error'}
            title={t(tested.success ? 'oob.test.success' : 'oob.test.failure')}
            description={
              <Space orientation="vertical" size={2}>
                {tested.errorMessage && <span>{tested.errorMessage}</span>}
                <span>
                  {tested.identity.model || '—'} ·{' '}
                  {tested.identity.controllerType || '—'} ·{' '}
                  {tested.identity.firmwareVersion || '—'}
                </span>
                {(tested.warnings || []).map((warning) => (
                  <span key={warning}>{warning}</span>
                ))}
              </Space>
            }
          />
        )}
      </Form>
    </Modal>
  );
}
