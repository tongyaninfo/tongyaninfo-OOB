import {
  Alert,
  App,
  Button,
  Collapse,
  Descriptions,
  Drawer,
  Input,
  Progress,
  Segmented,
  Select,
  Space,
  Statistic,
  Typography,
  Upload,
} from 'antd';
import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  ejectVirtualMedia,
  findVirtualMediaOperation,
  getMediaImages,
  getVirtualMedia,
  getVirtualMediaTraffic,
  mountVirtualMedia,
  reviewVirtualMediaNotExecuted,
  uploadMediaImage,
} from '@/services/oob/console';
import type { BinaryUploadProgress } from '@/services/oob/http';
import type {
  ServerDto,
  VirtualMediaImageDto,
  VirtualMediaOperationDto,
  VirtualMediaStatusDto,
  VirtualMediaTrafficDto,
} from '@/services/oob/models';
import { createUuidV4 } from '@/utils/uuid';
import { formatTime } from '../format';
import { useOobIntl } from '../intl';
import useStyles from './style';
import { formatBytes } from './traffic';

type MediaMode = 'Local' | 'URL' | 'Library';
export default function VirtualMediaDrawer({
  server,
  open,
  onClose,
  getContainer,
  localSupported,
  localReady,
  localStatus,
  onLocalFile,
  onLocalEject,
}: {
  server: ServerDto;
  open: boolean;
  onClose: () => void;
  getContainer: () => HTMLElement;
  localSupported: boolean;
  localReady: boolean;
  localStatus?: string;
  onLocalFile: (file: File) => boolean;
  onLocalEject: () => void;
}) {
  const t = useOobIntl();
  const { styles } = useStyles();
  const { message } = App.useApp();
  const [mode, setMode] = useState<MediaMode>('Local');
  const [status, setStatus] = useState<VirtualMediaStatusDto>();
  const [traffic, setTraffic] = useState<VirtualMediaTrafficDto>();
  const [images, setImages] = useState<VirtualMediaImageDto[]>([]);
  const [imagesComplete, setImagesComplete] = useState(false);
  const [imageId, setImageId] = useState<string>();
  const [imageUrl, setImageUrl] = useState('');
  const [slotId, setSlotId] = useState<string>();
  const [confirmation, setConfirmation] = useState('');
  const [operation, setOperation] = useState<VirtualMediaOperationDto>();
  const [unknownRequest, setUnknownRequest] = useState<string>();
  const [reviewName, setReviewName] = useState('');
  const [reviewNote, setReviewNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [uploading, setUploading] = useState(false);
  const [uploadProgress, setUploadProgress] = useState<BinaryUploadProgress>();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();
  const live = useRef(true);
  const uploadAbort = useRef<AbortController | undefined>(undefined);
  useEffect(() => {
    live.current = true;
    return () => {
      live.current = false;
      uploadAbort.current?.abort();
    };
  }, []);
  const refresh = useCallback(
    async (signal?: AbortSignal) => {
      setLoading(true);
      const result = await getVirtualMedia(server.id, signal);
      if (!live.current || signal?.aborted) return;
      setLoading(false);
      if (result.success) {
        setStatus(result.data);
        const activeOperation = result.data.operations.find((item) =>
          ['Submitted', 'Unknown'].includes(item.status),
        );
        setOperation(
          (previous) =>
            activeOperation ||
            result.data.operations.find(
              (item) => item.requestId === previous?.requestId,
            ) ||
            previous,
        );
        setError(undefined);
      } else setError(result.errorMessage);
    },
    [server.id],
  );
  useEffect(() => {
    if (!open) return;
    const abort = new AbortController();
    void refresh(abort.signal);
    const load = async () => {
      setImagesComplete(false);
      const all = new Map<string, VirtualMediaImageDto>();
      let total: number | undefined;
      for (let current = 1; current < 10000; current++) {
        const result = await getMediaImages(current, abort.signal);
        if (abort.signal.aborted || !live.current) return;
        if (!result.success) {
          setError(result.errorMessage);
          return;
        }
        if (
          (total !== undefined && total !== result.data.total) ||
          result.data.current !== current ||
          (!result.data.items.length && all.size < result.data.total)
        ) {
          setError(t('oob.console.directoryFailed'));
          return;
        }
        total = result.data.total;
        for (const image of result.data.items) {
          if (all.has(image.id)) {
            setError(t('oob.console.directoryFailed'));
            return;
          }
          all.set(image.id, image);
        }
        if (all.size === total) {
          setImages([...all.values()]);
          setImagesComplete(true);
          return;
        }
      }
    };
    void load();
    let timer: ReturnType<typeof setTimeout>;
    const sample = async () => {
      const result = await getVirtualMediaTraffic(server.id, abort.signal);
      if (abort.signal.aborted || !live.current) return;
      if (result.success) setTraffic(result.data);
      else setTraffic(undefined);
      timer = setTimeout(sample, 1000);
    };
    void sample();
    return () => {
      abort.abort();
      clearTimeout(timer);
    };
  }, [open, server.id, refresh]);
  const slot = status?.slots.find((item) => item.id === slotId);
  const image = images.find((item) => item.id === imageId);
  const pending = Boolean(
    unknownRequest ||
      (operation && ['Submitted', 'Unknown'].includes(operation.status)),
  );
  const perform = async (eject: boolean) => {
    if (!slot || confirmation !== server.name || busy || pending) {
      if (confirmation !== server.name)
        message.error(t('oob.console.confirmRequired'));
      return;
    }
    const requestId = createUuidV4();
    setBusy(true);
    const result = eject
      ? await ejectVirtualMedia(server.id, {
          slotId: slot.id,
          confirmation,
          requestId,
        })
      : await mountVirtualMedia(server.id, {
          slotId: slot.id,
          confirmation,
          requestId,
          imageId: mode === 'Library' ? imageId || null : null,
          imageUrl: mode === 'URL' ? imageUrl : null,
        });
    if (!live.current) return;
    setBusy(false);
    if (result.success) {
      setOperation(result.data);
      setUnknownRequest(undefined);
      setConfirmation('');
      void refresh();
    } else {
      setError(result.errorMessage);
      if (
        result.dispatched &&
        ['network', 'invalid-response'].includes(result.kind)
      )
        setUnknownRequest(requestId);
    }
  };
  const verify = async () => {
    const requestId = unknownRequest || operation?.requestId;
    if (!requestId) return;
    setBusy(true);
    const result = await findVirtualMediaOperation(server.id, requestId);
    if (!live.current) return;
    setBusy(false);
    if (result.success) {
      setOperation(result.data);
      setUnknownRequest(undefined);
      void refresh();
    } else setError(result.errorMessage);
  };
  const review = async () => {
    const requestId = unknownRequest || operation?.requestId;
    if (
      !requestId ||
      reviewName !== server.name ||
      !reviewNote.trim() ||
      reviewNote.length > 500 ||
      busy
    )
      return;
    setBusy(true);
    const result = await reviewVirtualMediaNotExecuted(server.id, requestId, {
      confirmation: reviewName,
      note: reviewNote.trim(),
    });
    if (!live.current) return;
    setBusy(false);
    if (result.success) {
      setOperation(result.data);
      setUnknownRequest(undefined);
      setReviewName('');
      setReviewNote('');
      void refresh();
    } else setError(result.errorMessage);
  };
  const upload = async (file: File) => {
    if (
      !/\.(iso|img)$/i.test(file.name) ||
      file.size === 0 ||
      file.size > 16 * 1024 ** 3 ||
      (/\.img$/i.test(file.name) && file.size !== 1474560)
    ) {
      message.error(t('oob.console.invalidImage'));
      return;
    }
    uploadAbort.current = new AbortController();
    setUploading(true);
    setUploadProgress({
      loaded: 0,
      total: file.size,
      percent: 0,
      bytesPerSecond: 0,
    });
    const result = await uploadMediaImage(
      file,
      uploadAbort.current.signal,
      (value) => {
        if (live.current) setUploadProgress(value);
      },
    );
    if (!live.current) return;
    setUploading(false);
    if (result.success) {
      setImages((previous) => [
        ...previous.filter((item) => item.id !== result.data.id),
        result.data,
      ]);
      setImageId(result.data.id);
      message.success(t('oob.console.uploaded'));
    } else setError(result.errorMessage);
  };
  return (
    <Drawer
      title={`${t('oob.console.media')} · ${server.name}`}
      open={open}
      onClose={onClose}
      getContainer={getContainer}
      size={560}
    >
      <Space orientation="vertical" size="middle" style={{ width: '100%' }}>
        <Alert type="info" showIcon title={t('oob.console.mediaCloseHint')} />
        <Segmented
          block
          value={mode}
          options={[
            { label: t('oob.console.mediaLocal'), value: 'Local' },
            { label: 'URL', value: 'URL' },
            { label: t('oob.console.mediaLibrary'), value: 'Library' },
          ]}
          onChange={(value) => setMode(value as MediaMode)}
        />
        {error && <Alert type="error" showIcon title={error} />}
        {mode === 'Local' ? (
          <>
            <Typography.Paragraph>
              {t('oob.console.localMediaHint')}
            </Typography.Paragraph>
            {(!localSupported || !localReady) && (
              <Alert
                type="warning"
                showIcon
                title={t('oob.console.localUnavailable')}
              />
            )}
            <Input
              aria-label={t('oob.console.confirmServer')}
              placeholder={`${t('oob.console.confirmServer')}: ${server.name}`}
              value={confirmation}
              onChange={(event) => setConfirmation(event.target.value)}
              autoComplete="off"
            />
            <Upload
              accept=".iso"
              showUploadList={false}
              disabled={
                !localSupported || !localReady || confirmation !== server.name
              }
              beforeUpload={(file) => {
                if (
                  !/\.iso$/i.test(file.name) ||
                  file.size > 16 * 1024 ** 3 ||
                  file.size === 0
                )
                  message.error(t('oob.console.localInvalidImage'));
                else if (onLocalFile(file))
                  message.info(t('oob.console.localPending'));
                return false;
              }}
            >
              <Button
                disabled={
                  !localSupported || !localReady || confirmation !== server.name
                }
              >
                {t('oob.console.localSelect')}
              </Button>
            </Upload>
            <Button
              danger
              disabled={
                !localSupported || !localReady || confirmation !== server.name
              }
              onClick={onLocalEject}
            >
              {t('oob.console.eject')}
            </Button>
            {localStatus && (
              <Typography.Text>
                {t('oob.console.localStatus')}: {localStatus}
              </Typography.Text>
            )}
          </>
        ) : (
          <>
            <Typography.Paragraph type="secondary">
              {t('oob.console.mediaHint')}
            </Typography.Paragraph>
            <Button loading={loading} onClick={() => void refresh()}>
              {t('oob.console.refreshMedia')}
            </Button>
            {status && !status.supported && (
              <Alert
                type="warning"
                showIcon
                title={t('oob.console.mediaUnavailable')}
              />
            )}
            {mode === 'Library' && status && !status.mediaBaseUrlConfigured && (
              <Alert
                type="warning"
                showIcon
                title={t('oob.console.mediaBaseMissing')}
              />
            )}
            {status?.warnings.map((warning) => (
              <Alert type="warning" showIcon key={warning} title={warning} />
            ))}
            <Typography.Text>{t('oob.console.slot')}</Typography.Text>
            <Select
              className={styles.mediaFile}
              aria-label={t('oob.console.slot')}
              value={slotId}
              placeholder={t('oob.console.slot')}
              options={status?.slots.map((item) => ({
                value: item.id,
                label: `${item.name} · ${item.mediaType}`,
              }))}
              onChange={setSlotId}
            />
            {slot && (
              <Descriptions
                size="small"
                column={1}
                items={[
                  {
                    key: 'state',
                    label: t('oob.status'),
                    children: slot.inserted
                      ? slot.imageName || '—'
                      : t('oob.console.noMedia'),
                  },
                  {
                    key: 'protection',
                    label: t('oob.console.readonly'),
                    children: slot.writeProtected ? '✓' : '—',
                  },
                ]}
              />
            )}
            {mode === 'Library' ? (
              <>
                <Typography.Text>{t('oob.console.image')}</Typography.Text>
                <Select
                  className={styles.mediaFile}
                  showSearch
                  optionFilterProp="label"
                  aria-label={t('oob.console.image')}
                  value={imageId}
                  disabled={!imagesComplete}
                  placeholder={t('oob.console.image')}
                  options={images.map((item) => ({
                    value: item.id,
                    label: `${item.fileName} · ${formatBytes(Number(item.sizeBytes))}`,
                    disabled: Boolean(
                      slot && slot.mediaType !== item.mediaType,
                    ),
                  }))}
                  onChange={setImageId}
                />
                {image && (
                  <Typography.Text
                    type="secondary"
                    className={styles.mediaFile}
                  >
                    {image.fileName} · SHA-256: {image.sha256}
                  </Typography.Text>
                )}
                <Upload
                  accept=".iso,.img"
                  showUploadList={false}
                  disabled={uploading}
                  beforeUpload={(file) => {
                    void upload(file);
                    return false;
                  }}
                >
                  <Button loading={uploading}>{t('oob.console.upload')}</Button>
                </Upload>
                {uploading && (
                  <>
                    <Typography.Text>
                      {t('oob.console.uploading')}
                    </Typography.Text>
                    <Progress
                      percent={Math.floor(uploadProgress?.percent || 0)}
                      status="active"
                    />
                    <Typography.Text type="secondary">
                      {t('oob.console.uploadProgress')}:{' '}
                      {formatBytes(uploadProgress?.loaded)} /{' '}
                      {formatBytes(uploadProgress?.total)} ·{' '}
                      {formatBytes(uploadProgress?.bytesPerSecond, true)}
                    </Typography.Text>
                    <Button onClick={() => uploadAbort.current?.abort()}>
                      {t('oob.cancel')}
                    </Button>
                  </>
                )}
              </>
            ) : (
              <Input
                aria-label={t('oob.console.mediaUrl')}
                placeholder="https://"
                value={imageUrl}
                onChange={(event) => setImageUrl(event.target.value)}
              />
            )}
            <Input
              aria-label={t('oob.console.confirmServer')}
              placeholder={`${t('oob.console.confirmServer')}: ${server.name}`}
              value={confirmation}
              onChange={(event) => setConfirmation(event.target.value)}
              autoComplete="off"
            />
            <Space>
              <Button
                type="primary"
                loading={busy}
                disabled={
                  pending ||
                  !slot?.canMount ||
                  !status?.canMount ||
                  (mode === 'Library' &&
                    (!image ||
                      !imagesComplete ||
                      !status.mediaBaseUrlConfigured ||
                      slot.mediaType !== image.mediaType)) ||
                  (mode === 'URL' && !/^https?:\/\//i.test(imageUrl)) ||
                  confirmation !== server.name
                }
                onClick={() => void perform(false)}
              >
                {t('oob.console.mount')}
              </Button>
              <Button
                danger
                loading={busy}
                disabled={
                  pending || !slot?.canEject || confirmation !== server.name
                }
                onClick={() => void perform(true)}
              >
                {t('oob.console.eject')}
              </Button>
            </Space>
            {(unknownRequest || operation) && (
              <Alert
                type={pending ? 'warning' : 'info'}
                showIcon
                title={
                  unknownRequest
                    ? t('oob.console.operation.Unknown')
                    : operation &&
                      t(
                        `oob.console.operation.${operation.status}` as 'oob.console.operation.Submitted',
                      )
                }
                description={
                  <Space orientation="vertical">
                    <span>{operation?.message}</span>
                    {operation && (
                      <span>{formatTime(operation.updatedAt)}</span>
                    )}
                    {pending &&
                      (unknownRequest || operation?.canReviewNotExecuted) && (
                        <Collapse
                          items={[
                            {
                              key: 'review',
                              label: t('oob.console.reviewTitle'),
                              children: (
                                <Space
                                  orientation="vertical"
                                  style={{ width: '100%' }}
                                >
                                  <Alert
                                    type="warning"
                                    showIcon
                                    title={t('oob.console.reviewHint')}
                                  />
                                  <Input
                                    value={reviewName}
                                    aria-label={t('oob.console.reviewName')}
                                    placeholder={`${t('oob.console.confirmServer')}: ${server.name}`}
                                    onChange={(event) =>
                                      setReviewName(event.target.value)
                                    }
                                  />
                                  <Input.TextArea
                                    value={reviewNote}
                                    maxLength={500}
                                    showCount
                                    aria-label={t('oob.console.reviewNote')}
                                    placeholder={t('oob.console.reviewNote')}
                                    onChange={(event) =>
                                      setReviewNote(event.target.value)
                                    }
                                  />
                                  <Button
                                    loading={busy}
                                    disabled={
                                      reviewName !== server.name ||
                                      !reviewNote.trim()
                                    }
                                    onClick={() => void review()}
                                  >
                                    {t('oob.console.reviewSubmit')}
                                  </Button>
                                </Space>
                              ),
                            },
                          ]}
                        />
                      )}
                    {pending && (
                      <Button
                        size="small"
                        loading={busy}
                        onClick={() => void verify()}
                      >
                        {t('oob.console.verifyMedia')}
                      </Button>
                    )}
                  </Space>
                }
              />
            )}
            <Typography.Text strong>
              {t('oob.console.mediaTraffic')}
            </Typography.Text>
            <Space wrap>
              <Statistic
                title={t('oob.console.receiveRate')}
                value={formatBytes(traffic?.bytesPerSecond, true)}
              />
              <Statistic
                title={t('oob.console.received')}
                value={formatBytes(traffic ? Number(traffic.bytesRead) : null)}
              />
            </Space>
            <Typography.Text type="secondary">
              {t('oob.console.mediaTrafficHint')}
            </Typography.Text>
          </>
        )}
      </Space>
    </Drawer>
  );
}
