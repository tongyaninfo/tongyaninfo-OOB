import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Card,
  Input,
  Modal,
  Select,
  Space,
  Tag,
  Typography,
} from 'antd';
import React, { useEffect, useRef, useState } from 'react';
import {
  executePowerAction,
  getPowerCapabilities,
  getPowerOperations,
  reviewPowerOperation,
} from '@/services/oob/api';
import { captureRuntimeException } from '@/services/oob/browserExceptions';
import { type RequestResult, requestFailure } from '@/services/oob/http';
import type {
  PowerAction,
  PowerOperationDto,
  ServerDto,
} from '@/services/oob/models';
import { createUuidV4 } from '@/utils/uuid';
import { formatTime } from '../format';
import { useOobIntl } from '../intl';
import ManagedTable from './ManagedTable';

export type PowerPanelProps = {
  server: Pick<ServerDto, 'id' | 'name'>;
  initialOperations?: PowerOperationDto[];
  active?: boolean;
  getContainer?: () => HTMLElement;
  beforeInteraction?: () => void;
};

const emptyOperations: PowerOperationDto[] = [];

/** 动作仅由确认框提交，不在挂载、轮询、关闭弹窗或错误重试时发送设备写请求。 */
export default function PowerPanel(props: PowerPanelProps) {
  return <PowerControls key={props.server.id} {...props} />;
}

/** 切换服务器时隔离未提交的确认与未知结果，防止旧目标意图落到新服务器。 */
function PowerControls({
  server,
  initialOperations = emptyOperations,
  active = true,
  getContainer,
  beforeInteraction,
}: PowerPanelProps) {
  const t = useOobIntl();
  const { message } = App.useApp();
  const cache = useQueryClient();
  const [action, setAction] = useState<PowerAction>();
  const [confirmation, setConfirmation] = useState('');
  const [requestId, setRequestId] = useState('');
  const [unknownId, setUnknownId] = useState('');
  const [reviewing, setReviewing] = useState<PowerOperationDto>();
  const [reviewConfirmation, setReviewConfirmation] = useState('');
  const [reviewOutcome, setReviewOutcome] = useState<
    'Confirmed' | 'NotExecuted'
  >();
  const [reviewNote, setReviewNote] = useState('');
  const writing = useRef(false);
  const isActive = useRef(active);
  isActive.current = active;
  const interact = () => {
    if (isActive.current) beforeInteraction?.();
  };
  const capabilitiesQuery = useQuery({
    queryKey: ['oob', server.id, 'power-capabilities'],
    queryFn: ({ signal }) => getPowerCapabilities(server.id, signal),
    enabled: active,
    retry: false,
    staleTime: Infinity,
    refetchOnMount: 'always',
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  // 电源动作只使用本次只读探测；加载或失败时不能回退库存中的旧能力。
  const current =
    active &&
    !capabilitiesQuery.isFetching &&
    !capabilitiesQuery.isError &&
    capabilitiesQuery.data?.success
      ? capabilitiesQuery.data.data
      : undefined;
  const actions = current?.capabilities.powerActions || [];
  const stateKnown =
    current?.powerState === 'On' || current?.powerState === 'Off';
  const supportsCurrentState = (value: PowerAction) =>
    Boolean(
      current?.enabled &&
        actions.includes(value) &&
        (current.powerState === 'Off'
          ? value === 'On' || value === 'PushPowerButton'
          : current.powerState === 'On' && value !== 'On'),
    );
  const operations = useQuery({
    queryKey: ['oob', server.id, 'power-operations'],
    retry: false,
    queryFn: ({ signal }) => getPowerOperations(server.id, signal),
    enabled: active,
    refetchInterval: active ? 15000 : false,
  });
  const execute = useMutation({
    retry: false,
    mutationFn: () => {
      if (
        !isActive.current ||
        !action ||
        confirmation !== server.name ||
        !supportsCurrentState(action) ||
        pendingId
      )
        return Promise.resolve(
          requestFailure(
            'INVALID_POWER_REQUEST',
            t('oob.power.confirm.invalid'),
          ),
        );
      return executePowerAction(server.id, {
        action,
        confirmation,
        requestId,
      });
    },
    onSuccess: (response) => {
      if (!response.success) {
        // 正式预检失败保证未建意图且未发设备命令；其它 5xx 和网络错误仍保留未知结果。
        const rejected =
          !response.dispatched ||
          (response.kind === 'business' &&
            response.status === 502 &&
            response.errorCode === 'POWER_PRECHECK_FAILED') ||
          (response.kind === 'business' &&
            response.status >= 400 &&
            response.status < 500 &&
            [
              'POWER_CONTROL_DISABLED',
              'POWER_UNSUPPORTED',
              'INVALID_POWER_REQUEST',
              'CONFIRMATION_REQUIRED',
              'SERVER_BUSY',
              'POWER_RESULT_PENDING',
              'IDEMPOTENCY_CONFLICT',
              'POWER_CONFLICT',
              'SERVER_NOT_FOUND',
              'VALIDATION_FAILED',
              'UNAUTHORIZED',
              'FORBIDDEN',
            ].includes(response.errorCode));
        setUnknownId(rejected ? '' : requestId);
        setAction(undefined);
        message.error(response.errorMessage);
        cache.invalidateQueries({
          queryKey: ['oob', server.id, 'power-operations'],
        });
        return;
      }
      const result = response.data;
      setAction(undefined);
      setConfirmation('');
      setUnknownId(
        ['Submitting', 'Submitted', 'Unknown'].includes(result.status)
          ? requestId
          : '',
      );
      message.info(result.message || result.status);
      cache.invalidateQueries({
        queryKey: ['oob', server.id, 'power-operations'],
      });
    },
    onError: (error: Error) => {
      void captureRuntimeException(error);
      // 意外客户端异常也保留原意图，防止用户误以为设备动作明确失败后重发。
      setUnknownId(requestId);
      setAction(undefined);
      message.error(error.message);
    },
    onSettled: () => {
      writing.current = false;
    },
  });
  /** 人工结果核对是独立审计意图，不复用设备电源动作方法，也不做自动核对。 */
  const review = useMutation({
    retry: false,
    mutationFn: () => {
      if (
        !isActive.current ||
        !reviewing ||
        reviewConfirmation !== server.name ||
        !reviewOutcome ||
        !reviewNote.trim() ||
        reviewNote.length > 500
      )
        return Promise.resolve(
          requestFailure('INVALID_REVIEW', t('oob.power.review.invalid')),
        );
      return reviewPowerOperation(server.id, reviewing.id, {
        confirmation: reviewConfirmation,
        outcome: reviewOutcome,
        note: reviewNote.trim(),
      });
    },
    onSuccess: (response) => {
      if (!response.success) {
        message.error(response.errorMessage);
        return;
      }
      const result = response.data;
      cache.setQueryData<RequestResult<PowerOperationDto[]>>(
        ['oob', server.id, 'power-operations'],
        (old) => ({
          success: true,
          status: 200,
          data: (old?.success ? old.data : initialOperations).map((item) =>
            item.id === result.id ? result : item,
          ),
        }),
      );
      if (unknownId === result.requestId) setUnknownId('');
      setReviewing(undefined);
      message.success(t('oob.power.review.saved'));
      cache.invalidateQueries({
        queryKey: ['oob', server.id, 'power-operations'],
      });
    },
    onError: (error: Error) => {
      void captureRuntimeException(error);
      message.error(error.message);
    },
  });
  const records = operations.data?.success
    ? operations.data.data
    : initialOperations;
  const unresolved = records.find((operation) =>
    ['Submitting', 'Submitted', 'Unknown'].includes(operation.status),
  );
  const pendingId = unresolved?.requestId || unknownId;
  useEffect(() => {
    if (active) return;
    // 隐藏标签只取消尚未提交的确认，已发出的请求及未知结果继续归属原服务器。
    if (!writing.current && !execute.isPending) {
      setAction(undefined);
      setConfirmation('');
    }
    if (!review.isPending) {
      setReviewing(undefined);
      setReviewConfirmation('');
    }
  }, [active, execute.isPending, review.isPending]);
  useEffect(() => {
    if (
      unknownId &&
      records.some(
        (operation) =>
          operation.requestId === unknownId &&
          [
            'Confirmed',
            'Rejected',
            'ConfirmedByUser',
            'NotExecutedByUser',
          ].includes(operation.status),
      )
    )
      setUnknownId('');
  }, [unknownId, records]);
  const actionLabel = (value: PowerAction) => t(`oob.power.${value}`);
  const capabilityError =
    capabilitiesQuery.data?.success === false &&
    capabilitiesQuery.data.errorCode !== 'CANCELED'
      ? capabilitiesQuery.data.errorMessage
      : capabilitiesQuery.error instanceof Error
        ? capabilitiesQuery.error.message
        : undefined;
  const canSubmit =
    active &&
    Boolean(action && supportsCurrentState(action)) &&
    confirmation === server.name &&
    !pendingId &&
    !execute.isPending;
  return (
    <Space
      orientation="vertical"
      size="large"
      style={{ width: '100%' }}
      onPointerDownCapture={interact}
      onFocusCapture={interact}
    >
      <Alert showIcon type="warning" title={t('oob.power.help')} />
      {operations.data?.success === false &&
        operations.data.errorCode !== 'CANCELED' && (
          <Alert showIcon type="error" title={operations.data.errorMessage} />
        )}
      {pendingId && (
        <Alert
          showIcon
          type="warning"
          title={t('oob.power.unknown')}
          description={<Typography.Text copyable>{pendingId}</Typography.Text>}
        />
      )}
      <Card
        styles={{ title: { whiteSpace: 'normal', paddingBlock: 12 } }}
        title={
          <Space
            wrap
            style={{ width: '100%', justifyContent: 'space-between' }}
          >
            <span>{t('oob.power.actions')}</span>
            <Button
              aria-label={t('oob.power.refreshCapabilities')}
              loading={capabilitiesQuery.isFetching}
              disabled={!active || execute.isPending}
              onClick={() => {
                if (!isActive.current) return;
                interact();
                void capabilitiesQuery.refetch();
              }}
            >
              {t('oob.power.refreshCapabilities')}
            </Button>
          </Space>
        }
      >
        <Space orientation="vertical" size="middle" style={{ width: '100%' }}>
          {capabilitiesQuery.isFetching && (
            <Typography.Text type="secondary">
              {t('oob.power.loadingCapabilities')}
            </Typography.Text>
          )}
          {capabilityError && (
            <Alert showIcon type="error" title={capabilityError} />
          )}
          {current && (
            <Typography.Text>
              {t('oob.powerState')}：
              {current.powerState || t('oob.power.stateUnknown')}
            </Typography.Text>
          )}
          {current?.enabled === false && (
            <Alert showIcon type="warning" title={t('oob.power.disabled')} />
          )}
          {current && !stateKnown && (
            <Alert
              showIcon
              type="info"
              title={t('oob.power.stateUnavailable')}
            />
          )}
          {actions.length ? (
            actions.map((value) => (
              <Space key={value} wrap size="middle">
                <Button
                  danger={value.startsWith('Force') || value === 'PowerCycle'}
                  disabled={
                    !supportsCurrentState(value) ||
                    Boolean(pendingId) ||
                    execute.isPending
                  }
                  onClick={() => {
                    if (!isActive.current) return;
                    interact();
                    setAction(value);
                    setConfirmation('');
                    setRequestId(createUuidV4());
                  }}
                >
                  {actionLabel(value)}
                </Button>
                <Typography.Text type="secondary">
                  {t(`oob.power.description.${value}`)}
                </Typography.Text>
              </Space>
            ))
          ) : current ? (
            <Typography.Text type="secondary">
              {t('oob.power.none')}
            </Typography.Text>
          ) : null}
        </Space>
      </Card>
      <Card
        title={t('oob.power.operations')}
        extra={
          <Button
            loading={operations.isFetching}
            disabled={!active}
            onClick={() => {
              if (!isActive.current) return;
              interact();
              void operations.refetch();
            }}
          >
            {t('oob.refresh')}
          </Button>
        }
      >
        <ManagedTable<PowerOperationDto>
          tableId="oob.power.operations.v1"
          rowKey="id"
          dataSource={records}
          scroll={{ x: 700 }}
          pagination={{ defaultPageSize: 10 }}
          columns={[
            {
              title: t('oob.power'),
              dataIndex: 'action',
              render: (value: PowerAction) => actionLabel(value),
            },
            {
              title: t('oob.power.operation.status'),
              dataIndex: 'status',
              render: (value: string) => (
                <Tag
                  color={
                    ['Confirmed', 'ConfirmedByUser'].includes(value)
                      ? 'success'
                      : value === 'Rejected'
                        ? 'error'
                        : 'warning'
                  }
                >
                  {value === 'ConfirmedByUser' || value === 'NotExecutedByUser'
                    ? t(`oob.power.review.${value}`)
                    : value}
                </Tag>
              ),
            },
            {
              title: t('oob.power.operation.time'),
              dataIndex: 'createdAt',
              render: (value) => formatTime(value),
            },
            { title: t('oob.power.operation.message'), dataIndex: 'message' },
            {
              title: t('oob.actions'),
              key: 'review',
              render: (_, operation) =>
                ['Submitting', 'Submitted', 'Unknown'].includes(
                  operation.status,
                ) ? (
                  <Button
                    type="link"
                    disabled={!active}
                    onClick={() => {
                      if (!isActive.current) return;
                      interact();
                      setReviewing(operation);
                      setReviewConfirmation('');
                      setReviewOutcome(undefined);
                      setReviewNote('');
                    }}
                  >
                    {t('oob.power.review.title')}
                  </Button>
                ) : null,
            },
          ]}
        />
      </Card>
      <Modal
        open={active && Boolean(action)}
        getContainer={getContainer}
        title={t('oob.power.confirm.title')}
        onCancel={execute.isPending ? undefined : () => setAction(undefined)}
        closable={!execute.isPending}
        mask={{ closable: !execute.isPending }}
        okText={t('oob.power.confirm.submit')}
        cancelText={t('oob.cancel')}
        confirmLoading={execute.isPending}
        cancelButtonProps={{ disabled: execute.isPending }}
        okButtonProps={{
          disabled: !canSubmit,
          danger: true,
        }}
        onOk={() => {
          // 同步防重覆盖 React 更新前的连续点击；原 requestId 始终只提交一次。
          if (!isActive.current || writing.current || !canSubmit) return;
          interact();
          writing.current = true;
          execute.mutate();
        }}
      >
        {action && (
          <Typography.Title level={5}>
            {actionLabel(action)} · {server.name}
          </Typography.Title>
        )}
        {action && (
          <Typography.Paragraph>
            {t(`oob.power.description.${action}`)}
          </Typography.Paragraph>
        )}
        {action && (action.startsWith('Force') || action === 'PowerCycle') && (
          <Alert
            showIcon
            type="error"
            title={t('oob.power.force')}
            style={{ marginBottom: 16 }}
          />
        )}
        <Typography.Paragraph>
          {t('oob.power.confirm.help', { name: server.name })}
        </Typography.Paragraph>
        <Input
          aria-label={t('oob.power.confirm.name')}
          value={confirmation}
          autoComplete="off"
          disabled={execute.isPending}
          onChange={(event) => setConfirmation(event.target.value)}
        />
      </Modal>
      <Modal
        open={active && Boolean(reviewing)}
        getContainer={getContainer}
        title={t('oob.power.review.title')}
        onCancel={review.isPending ? undefined : () => setReviewing(undefined)}
        closable={!review.isPending}
        mask={{ closable: !review.isPending }}
        confirmLoading={review.isPending}
        okText={t('oob.power.review.submit')}
        cancelText={t('oob.cancel')}
        okButtonProps={{
          disabled:
            !active ||
            reviewConfirmation !== server.name ||
            !reviewOutcome ||
            !reviewNote.trim() ||
            reviewNote.length > 500,
        }}
        onOk={() => {
          if (!isActive.current) return;
          interact();
          review.mutate();
        }}
      >
        <Alert
          type="info"
          showIcon
          title={t('oob.power.review.help')}
          style={{ marginBottom: 16 }}
        />
        <Typography.Paragraph>
          {server.name} · {reviewing ? actionLabel(reviewing.action) : ''} ·{' '}
          {reviewing?.requestId}
        </Typography.Paragraph>
        <Typography.Paragraph>
          {t('oob.power.confirm.help', { name: server.name })}
        </Typography.Paragraph>
        <Input
          aria-label={t('oob.power.confirm.name')}
          value={reviewConfirmation}
          autoComplete="off"
          onChange={(event) => setReviewConfirmation(event.target.value)}
        />
        <Typography.Paragraph style={{ marginTop: 16 }}>
          {t('oob.power.review.outcome')}
        </Typography.Paragraph>
        <Select
          aria-label={t('oob.power.review.outcome')}
          value={reviewOutcome}
          style={{ width: '100%' }}
          onChange={setReviewOutcome}
          options={[
            { value: 'Confirmed', label: t('oob.power.review.confirmed') },
            { value: 'NotExecuted', label: t('oob.power.review.notExecuted') },
          ]}
        />
        <Typography.Paragraph style={{ marginTop: 16 }}>
          {t('oob.power.review.note')}
        </Typography.Paragraph>
        <Input.TextArea
          aria-label={t('oob.power.review.note')}
          value={reviewNote}
          maxLength={500}
          showCount
          rows={4}
          onChange={(event) => setReviewNote(event.target.value)}
        />
      </Modal>
    </Space>
  );
}
