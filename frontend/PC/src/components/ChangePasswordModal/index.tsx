import { Alert, App, Form, Input, Modal } from 'antd';
import React, { useEffect, useRef, useState } from 'react';
import { useOobIntl } from '@/pages/oob/intl';
import { userError } from '@/pages/oob/Users/errors';
import { changePassword } from '@/services/oob/auth';
import type { ChangePasswordRequest, UserDto } from '@/services/oob/models';
import { sessionGeneration } from '@/services/oob/session';
import { resetUserPassword } from '@/services/oob/users';

type Props = {
  open: boolean;
  user?: UserDto;
  onClose: () => void;
  onSaved?: () => void | Promise<void>;
};

/** 自助改密必须验证原密码；管理员重设使用版本控制，不读取或回填密码。 */
export default function ChangePasswordModal({
  open,
  user,
  onClose,
  onSaved,
}: Props) {
  const t = useOobIntl();
  const { message } = App.useApp();
  const [form] = Form.useForm<ChangePasswordRequest>();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const [unknown, setUnknown] = useState(false);
  const [conflicted, setConflicted] = useState(false);
  const writing = useRef(false);
  const active = useRef(false);
  useEffect(() => {
    active.current = open;
    if (open) {
      form.resetFields();
      setError('');
      setUnknown(false);
      setConflicted(false);
      setPending(false);
    }
    return () => {
      active.current = false;
    };
  }, [open, user?.id, form]);
  const submit = async (values: ChangePasswordRequest) => {
    if (writing.current || unknown || conflicted) return;
    const expected = sessionGeneration();
    writing.current = true;
    setPending(true);
    setError('');
    try {
      const result = user
        ? await resetUserPassword(user.id, {
            version: user.version,
            newPassword: values.newPassword,
            confirmPassword: values.confirmPassword,
          })
        : await changePassword(values);
      if (expected !== sessionGeneration() || (user && !active.current)) return;
      if (!result.success) {
        if (!active.current) return;
        const unresolved =
          result.dispatched &&
          (result.kind !== 'business' || result.status >= 500);
        setUnknown(unresolved);
        setConflicted(result.errorCode === 'USER_VERSION_CONFLICT');
        setError(unresolved ? t('oob.password.unknown') : userError(result, t));
        return;
      }
      message.success(
        t(user ? 'oob.password.resetSaved' : 'oob.password.changed'),
      );
      form.resetFields();
      onClose();
      await onSaved?.();
    } finally {
      writing.current = false;
      if (active.current) setPending(false);
    }
  };
  return (
    <Modal
      open={open}
      title={
        user
          ? t('oob.users.resetTitle', { name: user.displayName })
          : t('oob.password.change')
      }
      destroyOnHidden
      confirmLoading={pending}
      onOk={() => form.submit()}
      onCancel={() => {
        if (!writing.current) onClose();
      }}
      closable={!pending}
      keyboard={!pending}
      mask={{ closable: false }}
      cancelButtonProps={{ disabled: pending }}
      okButtonProps={{ disabled: unknown || conflicted }}
      okText={t('oob.users.save')}
      cancelText={t('oob.users.cancel')}
    >
      <Alert
        type="info"
        showIcon
        title={t(user ? 'oob.users.resetHint' : 'oob.password.hint')}
        style={{ marginBottom: 16 }}
      />
      {error && (
        <Alert
          type={unknown ? 'warning' : 'error'}
          showIcon
          title={error}
          style={{ marginBottom: 16 }}
        />
      )}
      <Form
        form={form}
        layout="vertical"
        onFinish={submit}
        preserve={false}
        disabled={pending}
      >
        {!user && (
          <Form.Item
            name="currentPassword"
            label={t('oob.password.current')}
            rules={[{ required: true, message: t('oob.users.required') }]}
          >
            <Input.Password autoComplete="current-password" maxLength={128} />
          </Form.Item>
        )}
        <Form.Item
          name="newPassword"
          label={t('oob.password.new')}
          rules={[
            { required: true, message: t('oob.users.required') },
            { min: 8, max: 128, message: t('oob.password.length') },
          ]}
        >
          <Input.Password autoComplete="new-password" maxLength={128} />
        </Form.Item>
        <Form.Item
          name="confirmPassword"
          label={t('oob.password.confirm')}
          dependencies={['newPassword']}
          rules={[
            { required: true, message: t('oob.users.required') },
            ({ getFieldValue }) => ({
              validator: (_, value) =>
                !value || getFieldValue('newPassword') === value
                  ? Promise.resolve()
                  : Promise.reject(new Error(t('oob.password.mismatch'))),
            }),
          ]}
        >
          <Input.Password autoComplete="new-password" maxLength={128} />
        </Form.Item>
      </Form>
    </Modal>
  );
}
