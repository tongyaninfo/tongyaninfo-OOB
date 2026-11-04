import { DownloadOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Card,
  Form,
  Input,
  InputNumber,
  Select,
  Space,
  Spin,
  Switch,
  Typography,
} from 'antd';
import React, { useEffect, useRef, useState } from 'react';
import {
  getMailReportConfiguration,
  previewMailReport,
  saveMailReportConfiguration,
} from '@/services/oob/api';
import type {
  MailReportConfigurationDto,
  MailReportConfigurationRequest,
  MailReportPreviewDto,
} from '@/services/oob/models';
import { sessionGeneration } from '@/services/oob/session';
import { formatTime } from '../format';
import { useOobIntl } from '../intl';
import { customHistoryWindow, historyWindow } from '../Overview/historyTrends';
import ReportHtml from './ReportHtml';

const periods = ['Daily', 'Weekly', 'Monthly'] as const;
const weekdays = [1, 2, 3, 4, 5, 6, 7] as const;
type FormValues = Pick<MailReportConfigurationRequest, 'schedules'>;
const queryKey = ['oob', 'mail', 'reports', 'configuration'];

function initialRange() {
  const window = historyWindow('24h', Date.now());
  return {
    from: formatTime(window.from).replace(' ', 'T'),
    to: formatTime(window.to).replace(' ', 'T'),
  };
}

/** 计划显式保存后才生效；报表预览只查询已入库数据，不发送邮件或访问真机。 */
export default function Reports() {
  const t = useOobIntl();
  const { message } = App.useApp();
  const client = useQueryClient();
  const [form] = Form.useForm<FormValues>();
  const schedules = Form.useWatch('schedules', form);
  const [configuration, setConfiguration] =
    useState<MailReportConfigurationDto>();
  const [saving, setSaving] = useState(false);
  const [blocked, setBlocked] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [saveError, setSaveError] = useState('');
  const [range, setRange] = useState(initialRange);
  const [preview, setPreview] = useState<MailReportPreviewDto>();
  const [previewError, setPreviewError] = useState('');
  const [previewing, setPreviewing] = useState(false);
  const active = useRef(true);
  const writing = useRef(false);
  const previewRequest = useRef<AbortController | undefined>(undefined);
  const previewGeneration = useRef(0);
  useEffect(() => {
    active.current = true;
    return () => {
      active.current = false;
      previewGeneration.current++;
      previewRequest.current?.abort();
    };
  }, []);
  const query = useQuery({
    queryKey,
    queryFn: ({ signal }) => getMailReportConfiguration(signal),
    retry: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  const loaded = query.data?.success ? query.data.data : undefined;
  const restore = (value: MailReportConfigurationDto) => {
    form.setFieldsValue({
      schedules: periods.map((period) => {
        const schedule = value.schedules.find((item) => item.period === period);
        return {
          period,
          enabled: schedule?.enabled || false,
          toAddress: schedule?.toAddress || '',
          sendTime: schedule?.sendTime || '08:00',
          dayOfWeek: schedule?.dayOfWeek || 1,
          dayOfMonth: schedule?.dayOfMonth || 1,
        };
      }),
    });
    setConfiguration(value);
    setBlocked(false);
    setDirty(false);
  };
  useEffect(() => {
    // 后台失效重查保留草稿及其原始并发版本；重新载入才主动覆盖本地编辑。
    if (!configuration && loaded) restore(loaded);
  }, [configuration, loaded, form]);
  const reload = async () => {
    setSaveError('');
    const session = sessionGeneration();
    const result = await query.refetch();
    if (
      active.current &&
      session === sessionGeneration() &&
      result.data?.success
    )
      restore(result.data.data);
  };
  const save = async (values: FormValues) => {
    if (writing.current || blocked || !configuration?.configured) return;
    writing.current = true;
    setSaving(true);
    setSaveError('');
    const session = sessionGeneration();
    try {
      const result = await saveMailReportConfiguration({
        version: configuration.version,
        schedules: periods.map((period, index) => {
          const previous = configuration.schedules.find(
            (item) => item.period === period,
          );
          return {
            ...values.schedules[index],
            period,
            toAddress: values.schedules[index].toAddress.trim(),
            // 未展示的日期字段保留服务端原值，避免无关计划被识别为修改并重置生效时间。
            dayOfWeek:
              values.schedules[index].dayOfWeek ?? previous?.dayOfWeek ?? 1,
            dayOfMonth:
              values.schedules[index].dayOfMonth ?? previous?.dayOfMonth ?? 1,
          };
        }),
      });
      if (!active.current || session !== sessionGeneration()) return;
      if (result.success) {
        restore(result.data);
        client.setQueryData(queryKey, result);
        // 计划与SMTP复用同一并发版本，另一页重新读取时必须使用保存后的版本。
        void client.invalidateQueries({
          queryKey: ['oob', 'mail', 'configuration'],
        });
        message.success(t('oob.mail.reports.saved'));
      } else {
        const unknown =
          result.dispatched &&
          (result.kind !== 'business' || result.status >= 500);
        setBlocked(unknown || result.status === 409);
        setSaveError(unknown ? t('oob.mail.saveUnknown') : result.errorMessage);
      }
    } finally {
      writing.current = false;
      if (active.current) setSaving(false);
    }
  };
  const changeRange = (field: 'from' | 'to', value: string) => {
    previewGeneration.current++;
    previewRequest.current?.abort();
    setRange((previous) => ({ ...previous, [field]: value }));
    setPreview(undefined);
    setPreviewError('');
    setPreviewing(false);
  };
  const generate = async () => {
    // 原生日期输入在秒为零时可能省略秒；仍按上海墙钟校验后只转换一次。
    const wallClock = (value: string) =>
      `${value.replace('T', ' ')}${value.length === 16 ? ':00' : ''}`;
    const window = customHistoryWindow(
      wallClock(range.from),
      wallClock(range.to),
    );
    setPreview(undefined);
    if (!window) {
      setPreviewError(t('oob.mail.reports.invalidRange'));
      return;
    }
    previewRequest.current?.abort();
    const controller = new AbortController();
    previewRequest.current = controller;
    const generation = ++previewGeneration.current;
    const session = sessionGeneration();
    setPreviewing(true);
    setPreviewError('');
    try {
      const result = await previewMailReport(
        { from: window.from, to: window.to },
        controller.signal,
      );
      if (
        !active.current ||
        session !== sessionGeneration() ||
        generation !== previewGeneration.current
      )
        return;
      if (result.success) setPreview(result.data);
      else if (result.kind !== 'canceled') setPreviewError(result.errorMessage);
    } finally {
      if (active.current && generation === previewGeneration.current)
        setPreviewing(false);
    }
  };
  const download = () => {
    if (!preview) return;
    // 仅下载本次已完成的预览快照，避免下载按钮隐式发起新查询或发件。
    const url = URL.createObjectURL(
      new Blob([preview.htmlBody], { type: 'text/html;charset=utf-8' }),
    );
    const link = document.createElement('a');
    link.href = url;
    link.download = `oob-operation-report-${formatTime(preview.from).slice(0, 10)}.html`;
    link.click();
    URL.revokeObjectURL(url);
  };
  const previewSection = (
    <Card title={t('oob.mail.reports.historyTitle')}>
      <Typography.Paragraph type="secondary">
        {t('oob.mail.reports.previewHelp')}
      </Typography.Paragraph>
      <Space wrap align="end" style={{ marginBottom: 16 }}>
        <div>
          <label htmlFor="oob-report-from">{t('oob.history.from')}</label>
          <Input
            id="oob-report-from"
            type="datetime-local"
            step={1}
            value={range.from}
            onChange={(event) => changeRange('from', event.target.value)}
            style={{ display: 'block', width: 245 }}
          />
        </div>
        <div>
          <label htmlFor="oob-report-to">{t('oob.history.to')}</label>
          <Input
            id="oob-report-to"
            type="datetime-local"
            step={1}
            value={range.to}
            onChange={(event) => changeRange('to', event.target.value)}
            style={{ display: 'block', width: 245 }}
          />
        </div>
        <Button
          type="primary"
          aria-label={t('oob.mail.reports.preview')}
          loading={previewing}
          onClick={() => void generate()}
        >
          {t('oob.mail.reports.preview')}
        </Button>
        <Button
          aria-label={t('oob.mail.reports.download')}
          icon={<DownloadOutlined aria-hidden="true" />}
          disabled={!preview || previewing}
          onClick={download}
        >
          {t('oob.mail.reports.download')}
        </Button>
      </Space>
      {previewError && (
        <Alert
          type="error"
          showIcon
          title={previewError}
          style={{ marginBottom: 16 }}
        />
      )}
      {preview && (
        <>
          <Typography.Title level={5}>{preview.subject}</Typography.Title>
          <Typography.Paragraph type="secondary">
            {formatTime(preview.from)} — {formatTime(preview.to)}
          </Typography.Paragraph>
          <ReportHtml
            html={preview.htmlBody}
            title={t('oob.mail.reports.previewTitle')}
          />
        </>
      )}
    </Card>
  );
  return (
    <Space orientation="vertical" size="large" style={{ width: '100%' }}>
      <div>
        <Typography.Paragraph type="secondary">
          {t('oob.mail.reports.scheduleHelp')}
        </Typography.Paragraph>
        {!configuration && query.isPending && (
          <Spin aria-label={t('oob.mail.loading')} />
        )}
        {query.data?.success === false && (
          <Alert
            type="error"
            showIcon
            title={query.data.errorMessage}
            style={{ marginBottom: 16 }}
          />
        )}
        {saveError && (
          <Alert
            type="error"
            showIcon
            title={saveError}
            style={{ marginBottom: 16 }}
          />
        )}
        {configuration && !configuration.configured && (
          <Alert
            type="warning"
            showIcon
            title={t('oob.mail.unsaved')}
            style={{ marginBottom: 16 }}
          />
        )}
        {configuration && (
          <Form
            form={form}
            layout="vertical"
            disabled={saving || query.isFetching}
            onFinish={(values) => void save(values)}
            onValuesChange={() => setDirty(true)}
          >
            <div
              style={{
                display: 'flex',
                gap: 16,
                flexWrap: 'wrap',
                marginBottom: 16,
              }}
            >
              {periods.map((period, index) => {
                const enabled = Boolean(schedules?.[index]?.enabled);
                const label = t(`oob.mail.reports.${period}`);
                const saved = configuration.schedules.find(
                  (item) => item.period === period,
                );
                return (
                  <Card
                    key={period}
                    title={label}
                    style={{ flex: '1 1 280px', minWidth: 0 }}
                  >
                    <Form.Item
                      name={['schedules', index, 'enabled']}
                      label={t('oob.mail.reports.enabled')}
                      valuePropName="checked"
                    >
                      <Switch
                        aria-label={`${label} ${t('oob.mail.reports.enabled')}`}
                        disabled={
                          !configuration.configured ||
                          saving ||
                          query.isFetching
                        }
                      />
                    </Form.Item>
                    <Form.Item
                      name={['schedules', index, 'toAddress']}
                      label={t('oob.mail.recipient')}
                      rules={[
                        { required: enabled, message: t('oob.mail.required') },
                        { type: 'email', message: t('oob.mail.invalidEmail') },
                      ]}
                    >
                      <Input
                        aria-label={`${label} ${t('oob.mail.recipient')}`}
                        maxLength={320}
                        disabled={!enabled || saving || query.isFetching}
                        autoComplete="off"
                      />
                    </Form.Item>
                    <Form.Item
                      name={['schedules', index, 'sendTime']}
                      label={t('oob.mail.reports.sendTime')}
                      rules={[
                        {
                          required: true,
                          pattern: /^([01]\d|2[0-3]):[0-5]\d$/,
                          message: t('oob.mail.reports.invalidTime'),
                        },
                      ]}
                    >
                      <Input
                        aria-label={`${label} ${t('oob.mail.reports.sendTime')}`}
                        type="time"
                        step={60}
                        disabled={!enabled || saving || query.isFetching}
                      />
                    </Form.Item>
                    {period === 'Weekly' && (
                      <Form.Item
                        name={['schedules', index, 'dayOfWeek']}
                        label={t('oob.mail.reports.weekday')}
                      >
                        <Select
                          aria-label={`${label} ${t('oob.mail.reports.weekday')}`}
                          disabled={!enabled || saving || query.isFetching}
                          options={weekdays.map((value) => ({
                            value,
                            label: t(`oob.mail.reports.weekday.${value}`),
                          }))}
                        />
                      </Form.Item>
                    )}
                    {period === 'Monthly' && (
                      <Form.Item
                        name={['schedules', index, 'dayOfMonth']}
                        label={t('oob.mail.reports.monthday')}
                        extra={t('oob.mail.reports.monthdayHelp')}
                      >
                        <InputNumber
                          aria-label={`${label} ${t('oob.mail.reports.monthday')}`}
                          min={1}
                          max={31}
                          precision={0}
                          disabled={!enabled || saving || query.isFetching}
                          style={{ width: '100%' }}
                        />
                      </Form.Item>
                    )}
                    <Typography.Text type="secondary">
                      {t('oob.mail.reports.nextSend')}：
                      {formatTime(saved?.nextSendAt)}
                    </Typography.Text>
                  </Card>
                );
              })}
            </div>
            <Space wrap>
              <Button
                type="primary"
                htmlType="submit"
                loading={saving}
                disabled={
                  blocked ||
                  !configuration.configured ||
                  saving ||
                  query.isFetching
                }
              >
                {t('oob.mail.reports.save')}
              </Button>
              <Button
                loading={query.isFetching}
                disabled={saving}
                onClick={() => void reload()}
              >
                {t('oob.mail.reload')}
              </Button>
            </Space>
            {dirty && (
              <Typography.Paragraph type="secondary" style={{ marginTop: 12 }}>
                {t('oob.mail.reports.unsaved')}
              </Typography.Paragraph>
            )}
          </Form>
        )}
        {!configuration && !query.isPending && (
          <Button loading={query.isFetching} onClick={() => void reload()}>
            {t('oob.retry')}
          </Button>
        )}
      </div>
      {previewSection}
    </Space>
  );
}
