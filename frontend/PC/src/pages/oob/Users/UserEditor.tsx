import { Alert, App, Checkbox, Form, Input, Modal, Space, Switch } from 'antd';
import React, { useEffect, useRef, useState } from 'react';
import type { SaveUserRequest, UserDto } from '@/services/oob/models';
import { saveUser } from '@/services/oob/users';
import { useOobIntl } from '../intl';
import { userError } from './errors';

type Props = {
  open: boolean;
  user?: UserDto;
  onClose: () => void;
  onSaved: () => void | Promise<void>;
};
/** 编辑从列表的完整版本开始，冲突后要求重新载入，不用旧表单覆盖新权限。 */
export default function UserEditor({ open, user, onClose, onSaved }: Props) {
  const t = useOobIntl();
  const { message } = App.useApp();
  const [form] = Form.useForm<SaveUserRequest>();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const [blocked, setBlocked] = useState(false);
  const writing = useRef(false);
  const active = useRef(false);
  useEffect(() => {
    active.current = open;
    if (open) {
      form.resetFields();
      setError('');
      setBlocked(false);
      setPending(false);
      form.setFieldsValue(
        user
          ? {
              username: user.username,
              displayName: user.displayName,
              enabled: user.enabled,
              canManageUsers: user.canManageUsers,
              canUseConsole: user.canUseConsole,
              canManageServers: user.canManageServers,
              canOperatePower: user.canOperatePower,
            }
          : {
              enabled: true,
              canManageUsers: false,
              canUseConsole: false,
              canManageServers: false,
              canOperatePower: false,
            },
      );
    }
    return () => {
      active.current = false;
    };
  }, [open, user, form]);
  const submit = async (values: SaveUserRequest) => {
    if (writing.current || blocked) return;
    writing.current = true;
    setPending(true);
    setError('');
    try {
      const result = await saveUser(
        {
          ...values,
          username: values.username.trim(),
          displayName: values.displayName.trim(),
          ...(user ? { version: user.version, password: undefined } : {}),
        },
        user?.id,
      );
      if (!active.current) return;
      if (!result.success) {
        const unknown =
          result.dispatched &&
          (result.kind !== 'business' || result.status >= 500);
        setBlocked(unknown || result.errorCode === 'USER_VERSION_CONFLICT');
        setError(unknown ? t('oob.users.unknown') : userError(result, t));
        return;
      }
      message.success(t('oob.users.saved'));
      form.resetFields();
      onClose();
      await onSaved();
    } finally {
      writing.current = false;
      if (active.current) setPending(false);
    }
  };
  return (
    <Modal
      open={open}
      title={t(user ? 'oob.users.edit' : 'oob.users.add')}
      destroyOnHidden
      onOk={() => form.submit()}
      onCancel={() => {
        if (!writing.current) onClose();
      }}
      confirmLoading={pending}
      closable={!pending}
      keyboard={!pending}
      mask={{ closable: false }}
      cancelButtonProps={{ disabled: pending }}
      okButtonProps={{ disabled: blocked }}
      okText={t('oob.users.save')}
      cancelText={t('oob.users.cancel')}
    >
      {error && (
        <Alert
          showIcon
          type="error"
          title={error}
          style={{ marginBottom: 16 }}
        />
      )}
      {user && (
        <Alert
          showIcon
          type="info"
          title={t('oob.users.editHint')}
          style={{ marginBottom: 16 }}
        />
      )}
      <Form
        form={form}
        layout="vertical"
        preserve={false}
        disabled={pending}
        onFinish={submit}
      >
        <Form.Item
          name="displayName"
          label={t('oob.users.name')}
          rules={[
            {
              required: true,
              whitespace: true,
              message: t('oob.users.required'),
            },
          ]}
        >
          <Input autoComplete="off" maxLength={100} />
        </Form.Item>
        <Form.Item
          name="username"
          label={t('oob.users.username')}
          extra={t('oob.users.accountHint')}
          rules={[
            {
              required: true,
              whitespace: true,
              message: t('oob.users.required'),
            },
          ]}
        >
          <Input autoComplete="off" maxLength={100} />
        </Form.Item>
        {!user && (
          <Form.Item
            name="password"
            label={t('oob.password.initial')}
            rules={[
              { required: true, message: t('oob.users.required') },
              { min: 8, max: 128, message: t('oob.password.length') },
            ]}
          >
            <Input.Password autoComplete="new-password" maxLength={128} />
          </Form.Item>
        )}
        <Form.Item
          name="enabled"
          label={t('oob.users.status')}
          valuePropName="checked"
        >
          <Switch
            checkedChildren={t('oob.users.enabled')}
            unCheckedChildren={t('oob.users.disabled')}
          />
        </Form.Item>
        <Form.Item
          label={t('oob.users.permissions')}
          extra={t('oob.users.permissionHint')}
        >
          <Space wrap>
            <Form.Item name="canManageUsers" valuePropName="checked" noStyle>
              <Checkbox>{t('oob.users.manage')}</Checkbox>
            </Form.Item>
            <Form.Item name="canUseConsole" valuePropName="checked" noStyle>
              <Checkbox>{t('oob.users.console')}</Checkbox>
            </Form.Item>
            <Form.Item name="canManageServers" valuePropName="checked" noStyle>
              <Checkbox>{t('oob.users.servers')}</Checkbox>
            </Form.Item>
            <Form.Item name="canOperatePower" valuePropName="checked" noStyle>
              <Checkbox>{t('oob.users.power')}</Checkbox>
            </Form.Item>
          </Space>
        </Form.Item>
      </Form>
    </Modal>
  );
}
