import { ReloadOutlined } from '@ant-design/icons';
import { Button, DatePicker, Segmented, Space, Typography } from 'antd';
import dayjs, { type Dayjs } from 'dayjs';
import React, { useState } from 'react';
import { formatTime } from '../format';
import { useOobIntl } from '../intl';
import useStyles from './HistoryRangeControls.style';
import {
  customHistoryWindow,
  HISTORY_INPUT_FORMAT,
  type HistoryPeriod,
  type HistoryWindow,
  historyWindow,
} from './historyTrends';

type Range = [Dayjs | null, Dayjs | null] | null;
const rangeText = (range: Range): [string, string] => [
  range?.[0]?.format(HISTORY_INPUT_FORMAT) || '',
  range?.[1]?.format(HISTORY_INPUT_FORMAT) || '',
];

function inputRange(window: HistoryWindow): Range {
  // 日期组件只编辑墙钟值；UTC载体避免浏览器夏令时将02:30等合法上海时间改写。
  return [window.from, window.to].map((value) =>
    dayjs.utc(formatTime(value)),
  ) as [Dayjs, Dayjs];
}

/** 预设立即查询；自选范围作为草稿保留，只有明确点击查询才提交。 */
export default function HistoryRangeControls({
  window,
  loading,
  onQuery,
}: {
  window: HistoryWindow;
  loading: boolean;
  onQuery: (window: HistoryWindow) => void;
}) {
  const t = useOobIntl();
  const { styles } = useStyles();
  const [period, setPeriod] = useState<HistoryPeriod>(window.period);
  const [range, setRange] = useState<Range>(() => inputRange(window));
  const [text, setText] = useState(() => rangeText(inputRange(window)));
  const [invalid, setInvalid] = useState(false);
  const selectPeriod = (next: HistoryPeriod) => {
    setPeriod(next);
    setInvalid(false);
    if (next === 'custom') {
      const value = inputRange(window);
      setRange(value);
      setText(rangeText(value));
    } else onQuery(historyWindow(next, Date.now()));
  };
  const query = () => {
    if (period !== 'custom') {
      onQuery(historyWindow(period, Date.now()));
      return;
    }
    const next = customHistoryWindow(text[0], text[1]);
    setInvalid(!next);
    if (next) onQuery(next);
  };
  const updateRange = (next: Range) => {
    setRange(next);
    setText(rangeText(next));
    setInvalid(false);
  };
  return (
    <Space orientation="vertical" size="small" style={{ width: '100%' }}>
      <Space wrap>
        <Segmented<HistoryPeriod>
          aria-label={t('oob.monitor.history.period')}
          value={period}
          onChange={selectPeriod}
          options={[
            { value: '24h', label: t('oob.monitor.history.24h') },
            { value: '7d', label: t('oob.monitor.history.7d') },
            { value: 'month', label: t('oob.monitor.history.month') },
            { value: 'custom', label: t('oob.monitor.history.custom') },
          ]}
        />
        {period === 'custom' && (
          <DatePicker.RangePicker
            classNames={{ popup: { root: styles.pickerPopup } }}
            showTime
            needConfirm={false}
            preserveInvalidOnBlur
            order={false}
            format={HISTORY_INPUT_FORMAT}
            value={range}
            onChange={updateRange}
            onCalendarChange={updateRange}
            onBlur={(event, info) => {
              // 无效手输不沿用之前有效的草稿；保留原文并在查询时一并校验。
              if (!(event.target instanceof HTMLInputElement)) return;
              const value = event.target.value;
              setText((previous) =>
                info.range === 'start'
                  ? [value, previous[1]]
                  : [previous[0], value],
              );
            }}
            placeholder={[
              t('oob.monitor.history.from'),
              t('oob.monitor.history.to'),
            ]}
            status={invalid ? 'error' : undefined}
            style={{ maxWidth: '100%' }}
          />
        )}
        <Button
          icon={<ReloadOutlined aria-hidden="true" />}
          loading={loading}
          onClick={query}
        >
          {t('oob.history.query')}
        </Button>
      </Space>
      {period === 'custom' && (
        <Typography.Text type="secondary">
          {t('oob.monitor.history.customHelp')}
        </Typography.Text>
      )}
      {invalid && (
        <Typography.Text type="danger" role="alert">
          {t('oob.monitor.history.invalidRange')}
        </Typography.Text>
      )}
    </Space>
  );
}
