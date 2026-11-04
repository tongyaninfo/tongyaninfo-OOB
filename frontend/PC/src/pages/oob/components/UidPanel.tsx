import { ReloadOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, Button, Card, Space, Tag, Typography } from 'antd';
import React, { useRef, useState } from 'react';
import { getUid, setUid } from '@/services/oob/api';
import { captureRuntimeException } from '@/services/oob/browserExceptions';
import type {
  ServerDto,
  SetUidRequest,
  UidStatusDto,
} from '@/services/oob/models';
import { formatTime } from '../format';
import { useOobIntl } from '../intl';

type Props = { server: Pick<ServerDto, 'id' | 'name'> };

/** 切换服务器时隔离未知结果与进行中的请求，旧结果不回写新服务器。 */
export default function UidPanel({ server }: Props) {
  return <UidControls key={server.id} server={server} />;
}

/** 页面挂载只读；用户点击定位灯动作后直接发送，不依赖电源控制开关。 */
function UidControls({ server }: Props) {
  const t = useOobIntl();
  const cache = useQueryClient();
  const queryKey = ['oob', server.id, 'uid'];
  const [needsReview, setNeedsReview] = useState(false);
  const [controlUnavailable, setControlUnavailable] = useState(false);
  const [feedback, setFeedback] = useState<{
    type: 'error' | 'warning' | 'success' | 'info';
    text: string;
  }>();
  // React状态更新前的连续点击也只允许一个写请求。
  const writing = useRef(false);
  const query = useQuery({
    queryKey,
    queryFn: ({ signal }) => getUid(server.id, signal),
    retry: false,
    staleTime: Infinity,
    refetchOnMount: 'always',
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  const known = (status: UidStatusDto | undefined) =>
    Boolean(status && ['Off', 'Lit', 'Blinking'].includes(status.state));
  const status = query.data?.success ? query.data.data : undefined;
  // 写入结果未知后，写前读数只能作为缓存，不能继续标为当前确定状态。
  const displayedStatus = needsReview ? undefined : status;
  const execute = useMutation({
    retry: false,
    mutationFn: (request: SetUidRequest) => setUid(server.id, request),
    onSuccess: async (response, request) => {
      if (!response.success) {
        const unsupported = response.errorCode === 'UID_UNSUPPORTED';
        const unknown =
          !unsupported &&
          response.dispatched &&
          (response.kind !== 'business' ||
            response.status >= 500 ||
            response.errorCode === 'UID_RESULT_UNKNOWN');
        setNeedsReview(unknown);
        // 明确拒绝不改变已知灯状态，但旧查询能力已失效，必须手动重新读取才能继续操作。
        if (unsupported) setControlUnavailable(true);
        setFeedback({
          type: unknown ? 'warning' : 'error',
          text: unknown
            ? `${t('oob.uid.unknown')} ${response.errorMessage}`
            : response.errorMessage,
        });
        return;
      }
      cache.setQueryData(queryKey, response);
      if (!known(response.data)) {
        setNeedsReview(true);
        setFeedback({ type: 'warning', text: t('oob.uid.unknown') });
        return;
      }
      // 设备接受请求后只读确认；读取失败保留待核对状态，绝不重发PUT。
      const checked = await query.refetch();
      const verified =
        checked.data?.success &&
        known(checked.data.data) &&
        checked.data.data.state === request.state;
      setNeedsReview(!verified);
      setFeedback({
        type: verified ? 'success' : 'warning',
        text: t(verified ? 'oob.uid.updated' : 'oob.uid.verify.failed'),
      });
    },
    onError: (error: Error) => {
      void captureRuntimeException(error);
      setNeedsReview(true);
      setFeedback({
        type: 'warning',
        text: `${t('oob.uid.unknown')} ${error.message}`,
      });
    },
    onSettled: () => {
      writing.current = false;
    },
  });
  const canControl =
    known(status) &&
    status?.canControl &&
    !controlUnavailable &&
    !needsReview &&
    !query.isFetching &&
    !execute.isPending;
  const label = (state: UidStatusDto['state'] | undefined) => {
    switch (state) {
      case 'Off':
        return t('oob.uid.state.Off');
      case 'Lit':
        return t('oob.uid.state.Lit');
      case 'Blinking':
        return t('oob.uid.state.Blinking');
      default:
        return t('oob.uid.state.Unknown');
    }
  };
  const actionLabel = (state: SetUidRequest['state'] | undefined) => {
    switch (state) {
      case 'Lit':
        return t('oob.uid.on');
      case 'Blinking':
        return t('oob.uid.blink');
      default:
        return t('oob.uid.off');
    }
  };
  const refresh = async () => {
    const checked = await query.refetch();
    if (checked.data?.success && known(checked.data.data)) {
      if (needsReview || controlUnavailable)
        setFeedback({ type: 'info', text: t('oob.uid.reconciled') });
      setNeedsReview(false);
      setControlUnavailable(false);
    }
  };
  const submit = (state: SetUidRequest['state']) => {
    if (
      writing.current ||
      !canControl ||
      status?.state === state ||
      !status?.supportedStates.includes(state)
    )
      return;
    writing.current = true;
    setFeedback(undefined);
    // 保留服务端名称一致性校验，避免页面旧名称对应的目标已被重新命名。
    execute.mutate({ state, confirmation: server.name });
  };
  const readError =
    query.data?.success === false && query.data.errorCode !== 'CANCELED'
      ? query.data.errorMessage
      : query.error instanceof Error
        ? query.error.message
        : undefined;
  return (
    <Card
      title={t('oob.uid.title')}
      extra={
        <Button
          icon={<ReloadOutlined />}
          aria-label={t('oob.uid.refresh')}
          loading={query.isFetching}
          disabled={execute.isPending}
          onClick={() => void refresh()}
        >
          {t('oob.uid.refresh')}
        </Button>
      }
    >
      <Space orientation="vertical" style={{ width: '100%' }}>
        <Typography.Text type="secondary">{t('oob.uid.help')}</Typography.Text>
        <Space wrap>
          <Tag
            color={
              displayedStatus?.state === 'Lit'
                ? 'blue'
                : displayedStatus?.state === 'Blinking'
                  ? 'warning'
                  : 'default'
            }
          >
            {label(displayedStatus?.state)}
          </Tag>
          {displayedStatus?.observedAt && (
            <Typography.Text type="secondary">
              {t('oob.observedAt')}：{formatTime(displayedStatus.observedAt)}
            </Typography.Text>
          )}
          {(['Lit', 'Blinking', 'Off'] as const)
            .filter((state) => status?.supportedStates.includes(state))
            .map((state) => (
              <Button
                key={state}
                disabled={!canControl || status?.state === state}
                loading={
                  execute.isPending && execute.variables?.state === state
                }
                onClick={() => submit(state)}
              >
                {actionLabel(state)}
              </Button>
            ))}
        </Space>
        {displayedStatus?.message && (
          <Typography.Text>{displayedStatus.message}</Typography.Text>
        )}
        {!query.isFetching && (!known(status) || needsReview) && (
          <Typography.Text type="secondary">
            {t('oob.uid.unavailable')}
          </Typography.Text>
        )}
        {known(status) && (!status?.canControl || controlUnavailable) && (
          <Typography.Text type="secondary">
            {t('oob.uid.readOnly')}
          </Typography.Text>
        )}
        {readError && <Alert showIcon type="error" title={readError} />}
        {feedback && (
          <Alert showIcon type={feedback.type} title={feedback.text} />
        )}
      </Space>
    </Card>
  );
}
