import { Alert, App, Button, Form, Input, Modal, Space } from 'antd';
import React, { useEffect, useRef, useState } from 'react';
import {
  exportServerConfiguration,
  importServerConfiguration,
} from '@/services/oob/api';
import type {
  ServerConfigurationFile,
  ServerConfigurationImportResult,
} from '@/services/oob/models';
import {
  isSessionExpired,
  sessionGeneration,
  subscribeSessionExpiry,
} from '@/services/oob/session';
import { useOobIntl } from '../intl';

interface Props {
  mode: 'export' | 'import';
  onClose: () => void;
  onImported: () => void;
}

const maximumFileBytes = 4 * 1024 * 1024;
const encryptedFields = [
  'format',
  'salt',
  'nonce',
  'ciphertext',
  'tag',
] as const;

/** 只接受密文信封，设备账号、地址与登录密码不进入浏览器的配置对象。 */
function isConfigurationFile(value: unknown): value is ServerConfigurationFile {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const record = value as Record<string, unknown>;
  return (
    Object.keys(record).length === encryptedFields.length + 1 &&
    Number.isInteger(record.version) &&
    encryptedFields.every(
      (key) => typeof record[key] === 'string' && record[key].length > 0,
    )
  );
}

/** 文件只保存服务器返回的密文；下载完成释放临时 URL，不持久化文件密码。 */
function downloadConfiguration(file: ServerConfigurationFile) {
  const url = URL.createObjectURL(
    new Blob([JSON.stringify(file, null, 2)], { type: 'application/json' }),
  );
  const link = document.createElement('a');
  link.href = url;
  link.download = `oob-server-configuration-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
  document.body.appendChild(link);
  try {
    link.click();
  } finally {
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
}

/** 选文件和打开弹窗均不提交；明确点击后才导出或覆盖配置。 */
export default function ConfigurationTransfer({
  mode,
  onClose,
  onImported,
}: Props) {
  const t = useOobIntl();
  const { message } = App.useApp();
  const [password, setPassword] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [file, setFile] = useState<File>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [unknown, setUnknown] = useState(false);
  const [result, setResult] = useState<ServerConfigurationImportResult>();
  const pending = useRef(false);
  const controller = useRef<AbortController | undefined>(undefined);
  const active = useRef(true);
  const initialSession = useRef(sessionGeneration());
  const close = useRef(onClose);
  close.current = onClose;
  useEffect(() => {
    active.current = true;
    const unsubscribe = subscribeSessionExpiry(() => {
      active.current = false;
      controller.current?.abort();
      close.current();
    });
    return () => {
      active.current = false;
      controller.current?.abort();
      unsubscribe();
    };
  }, []);
  const currentSession = () =>
    active.current &&
    initialSession.current === sessionGeneration() &&
    !isSessionExpired();

  const submit = async () => {
    if (pending.current || result || !currentSession()) return;
    setError('');
    setUnknown(false);
    if (password.length < 8 || password.length > 128 || !password.trim()) {
      setError(t('oob.configuration.passwordInvalid'));
      return;
    }
    if (mode === 'export' && password !== confirmation) {
      setError(t('oob.configuration.passwordMismatch'));
      return;
    }
    if (mode === 'import' && !file) {
      setError(t('oob.configuration.fileRequired'));
      return;
    }
    if (file && file.size > maximumFileBytes) {
      setError(t('oob.configuration.fileTooLarge'));
      return;
    }
    pending.current = true;
    setBusy(true);
    controller.current = new AbortController();
    try {
      if (mode === 'export') {
        const exported = await exportServerConfiguration(
          { password },
          controller.current.signal,
        );
        if (!currentSession()) return;
        if (!exported.success) {
          if (exported.errorCode !== 'CANCELED')
            setError(exported.errorMessage);
          return;
        }
        downloadConfiguration(exported.data);
        message.success(t('oob.configuration.exportSaved'));
        onClose();
      } else {
        let parsed: unknown;
        try {
          parsed = JSON.parse(await (file as File).text());
        } catch {
          if (currentSession()) setError(t('oob.configuration.fileInvalid'));
          return;
        }
        if (!currentSession()) return;
        if (!isConfigurationFile(parsed)) {
          setError(t('oob.configuration.fileInvalid'));
          return;
        }
        const imported = await importServerConfiguration(
          { password, file: parsed },
          controller.current.signal,
        );
        if (!currentSession()) return;
        if (!imported.success) {
          if (imported.errorCode !== 'CANCELED') {
            // 已发送且未得到明确业务拒绝时保留未知结果，不自动重试。
            const uncertain =
              imported.dispatched && imported.kind !== 'business';
            setUnknown(uncertain);
            setError(
              uncertain
                ? t('oob.configuration.importUnknown')
                : imported.errorMessage,
            );
          }
          return;
        }
        setResult(imported.data);
        setPassword('');
        setFile(undefined);
        onImported();
      }
    } catch {
      // 不上报文件内容或密码，异常详情也不拼入界面与日志。
      if (currentSession()) setError(t('oob.configuration.failed'));
    } finally {
      pending.current = false;
      if (currentSession()) setBusy(false);
    }
  };

  return (
    <Modal
      open
      title={t(`oob.configuration.${mode}`)}
      onCancel={busy ? undefined : onClose}
      closable={!busy}
      keyboard={!busy}
      mask={{ closable: !busy }}
      destroyOnHidden
      footer={
        <Space wrap>
          <Button disabled={busy} onClick={onClose}>
            {t('oob.configuration.close')}
          </Button>
          {unknown && (
            <Button onClick={onImported}>
              {t('oob.configuration.reload')}
            </Button>
          )}
          {!result && (
            <Button
              type="primary"
              aria-label={t(`oob.configuration.${mode}`)}
              loading={busy}
              onClick={() => void submit()}
            >
              {t(`oob.configuration.${mode}`)}
            </Button>
          )}
        </Space>
      }
    >
      <Alert
        type="info"
        showIcon
        title={t(`oob.configuration.${mode}Help`)}
        style={{ marginBottom: 16 }}
      />
      {result ? (
        <Alert
          type="success"
          showIcon
          title={t('oob.configuration.importResult', {
            added: result.added,
            updated: result.updated,
            skipped: result.skipped,
          })}
        />
      ) : (
        <Form layout="vertical" disabled={busy}>
          {mode === 'import' && (
            <Form.Item
              label={t('oob.configuration.file')}
              htmlFor="configuration-file"
            >
              <input
                id="configuration-file"
                type="file"
                style={{ maxWidth: '100%' }}
                accept=".json,application/json"
                disabled={busy}
                onChange={(event) => {
                  setFile(event.target.files?.[0]);
                  setError('');
                  setUnknown(false);
                }}
              />
            </Form.Item>
          )}
          <Form.Item
            label={t('oob.configuration.password')}
            htmlFor="configuration-password"
            extra={t('oob.configuration.passwordHelp')}
          >
            <Input.Password
              id="configuration-password"
              autoComplete="new-password"
              maxLength={128}
              value={password}
              onChange={(event) => setPassword(event.target.value)}
            />
          </Form.Item>
          {mode === 'export' && (
            <Form.Item
              label={t('oob.configuration.confirmPassword')}
              htmlFor="configuration-confirmation"
            >
              <Input.Password
                id="configuration-confirmation"
                autoComplete="new-password"
                maxLength={128}
                value={confirmation}
                onChange={(event) => setConfirmation(event.target.value)}
              />
            </Form.Item>
          )}
        </Form>
      )}
      {error && <Alert type="error" showIcon title={error} />}
    </Modal>
  );
}
