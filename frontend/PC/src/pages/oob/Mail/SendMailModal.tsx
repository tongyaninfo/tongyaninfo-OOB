import {
  Alert,
  App,
  Button,
  Form,
  Input,
  Modal,
  Space,
  Typography,
} from 'antd';
import React, { useEffect, useRef, useState } from 'react';
import { getMailRequest, sendMail } from '@/services/oob/mail';
import type { MailMessageDto, MailSendRequest } from '@/services/oob/models';
import { sessionGeneration } from '@/services/oob/session';
import { createUuidV4 } from '@/utils/uuid';
import { useOobIntl } from '../intl';
import { mailStatusLabel } from './common';

type Props = {
  open: boolean;
  test?: boolean;
  onClose: () => void;
  onChanged: () => void;
};

/** 同一显式发送只创建一个请求标识，网络结果未知时锁定原内容并查询原意图。 */
export default function SendMailModal({
  open,
  test = false,
  onClose,
  onChanged,
}: Props) {
  const t = useOobIntl();
  const { message } = App.useApp();
  const [form] = Form.useForm<MailSendRequest>();
  const [pending, setPending] = useState(false);
  const [intent, setIntent] = useState<MailSendRequest>();
  const [record, setRecord] = useState<MailMessageDto>();
  const [error, setError] = useState('');
  const [notFound, setNotFound] = useState(false);
  const writing = useRef(false);
  const active = useRef(false);
  useEffect(() => {
    active.current = open;
    if (open) {
      form.resetFields();
      if (test)
        form.setFieldsValue({
          subject: t('oob.mail.testSubject'),
          textBody: t('oob.mail.testBody'),
        });
      setIntent(undefined);
      setRecord(undefined);
      setError('');
      setNotFound(false);
    }
    return () => {
      active.current = false;
    };
    // 语言切换不重置已输入内容或发送意图。
  }, [open, test, form]);

  const applyRecord = (value: MailMessageDto) => {
    setRecord(value);
    setError('');
    setNotFound(false);
    if (value.status === 'Accepted') message.success(t('oob.mail.accepted'));
    onChanged();
  };
  const execute = async (values: MailSendRequest) => {
    if (writing.current) return;
    writing.current = true;
    setPending(true);
    setError('');
    setNotFound(false);
    const expectedSession = sessionGeneration();
    try {
      const result = await sendMail(values, test);
      if (!active.current || expectedSession !== sessionGeneration()) return;
      if (result.success) applyRecord(result.data);
      else {
        const unknown =
          result.dispatched &&
          (result.kind !== 'business' || result.status >= 500);
        setError(unknown ? t('oob.mail.unknown') : result.errorMessage);
        // 明确拒绝且没有创建发送意图时可以编辑；未知结果必须保留同一标识。
        if (!unknown && result.errorCode !== 'MAIL_REQUEST_CONFLICT')
          setIntent(undefined);
        onChanged();
      }
    } finally {
      writing.current = false;
      if (active.current) setPending(false);
    }
  };
  const submit = (values: MailSendRequest) => {
    if (intent || writing.current) return;
    const next = {
      ...values,
      toAddress: values.toAddress.trim(),
      requestId: createUuidV4(),
    };
    setIntent(next);
    void execute(next);
  };
  const check = async () => {
    if (!intent || writing.current) return;
    writing.current = true;
    setPending(true);
    const expectedSession = sessionGeneration();
    try {
      const result = await getMailRequest(intent.requestId);
      if (!active.current || expectedSession !== sessionGeneration()) return;
      if (result.success) applyRecord(result.data);
      else {
        setError(result.errorMessage);
        // 仅后端明确查无原意图时，允许用户显式重试相同标识与相同内容。
        setNotFound(
          result.status === 404 &&
            result.kind === 'business' &&
            result.errorCode === 'MAIL_MESSAGE_NOT_FOUND',
        );
      }
    } finally {
      writing.current = false;
      if (active.current) setPending(false);
    }
  };
  return (
    <Modal
      open={open}
      title={t(test ? 'oob.mail.testSend' : 'oob.mail.send')}
      destroyOnHidden
      onCancel={() => {
        if (!writing.current) onClose();
      }}
      closable={!pending}
      keyboard={!pending}
      mask={{ closable: false }}
      footer={
        <Space wrap>
          {intent && (
            <Button loading={pending} onClick={() => void check()}>
              {t('oob.mail.checkStatus')}
            </Button>
          )}
          {intent && notFound && (
            <Button loading={pending} onClick={() => void execute(intent)}>
              {t('oob.mail.retryOriginal')}
            </Button>
          )}
          {!intent && (
            <Button
              type="primary"
              loading={pending}
              onClick={() => form.submit()}
            >
              {t(test ? 'oob.mail.testSend' : 'oob.mail.send')}
            </Button>
          )}
          <Button disabled={pending} onClick={onClose}>
            {t('oob.cancel')}
          </Button>
        </Space>
      }
    >
      {error && (
        <Alert
          showIcon
          type="error"
          title={error}
          style={{ marginBottom: 16 }}
        />
      )}
      {record && (
        <Alert
          showIcon
          type={
            record.status === 'Accepted'
              ? 'success'
              : record.status === 'Failed'
                ? 'error'
                : 'warning'
          }
          title={mailStatusLabel(record.status, t)}
          description={
            record.status === 'Accepted'
              ? t('oob.mail.acceptedHelp')
              : record.message || t('oob.mail.unknown')
          }
          style={{ marginBottom: 16 }}
        />
      )}
      {intent && (
        <Typography.Paragraph
          type="secondary"
          copyable={{ text: intent.requestId }}
        >
          {t('oob.mail.requestId')}: {intent.requestId}
        </Typography.Paragraph>
      )}
      <Form
        form={form}
        layout="vertical"
        disabled={Boolean(intent) || pending}
        onFinish={submit}
      >
        <Form.Item
          name="toAddress"
          label={t('oob.mail.recipient')}
          rules={[
            { required: true, message: t('oob.mail.required') },
            { type: 'email', message: t('oob.mail.invalidEmail') },
          ]}
        >
          <Input autoComplete="off" maxLength={320} />
        </Form.Item>
        {!test && (
          <>
            <Form.Item
              name="subject"
              label={t('oob.mail.subject')}
              rules={[
                {
                  required: true,
                  whitespace: true,
                  message: t('oob.mail.required'),
                },
              ]}
            >
              <Input maxLength={200} />
            </Form.Item>
            <Form.Item
              name="textBody"
              label={t('oob.mail.body')}
              rules={[
                {
                  required: true,
                  whitespace: true,
                  message: t('oob.mail.required'),
                },
              ]}
            >
              <Input.TextArea rows={7} maxLength={20000} showCount />
            </Form.Item>
          </>
        )}
        {test && (
          <>
            <Form.Item name="subject" hidden>
              <Input />
            </Form.Item>
            <Form.Item name="textBody" hidden>
              <Input />
            </Form.Item>
          </>
        )}
      </Form>
    </Modal>
  );
}
