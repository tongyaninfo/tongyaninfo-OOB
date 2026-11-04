import { Line } from '@ant-design/plots';
import { useQuery } from '@tanstack/react-query';
import { useModel } from '@umijs/max';
import {
  Alert,
  Button,
  Card,
  DatePicker,
  Empty,
  Select,
  Space,
  Typography,
} from 'antd';
import dayjs, { type Dayjs } from 'dayjs';
import React, { useMemo, useState } from 'react';
import { getHistory } from '@/services/oob/api';
import type { HistoryPointDto, HistorySeriesDto } from '@/services/oob/models';
import { formatReading, formatTime, metricDisplayName } from '../format';
import { useOobIntl } from '../intl';
import ManagedTable from './ManagedTable';

/** 所有历史查询有明确时间范围，单位分图；采样缺口通过空值保留，不连成虚假读数。 */
export default function HistoryPanel({ id }: { id: string }) {
  const t = useOobIntl();
  const { initialState } = useModel('@@initialState');
  const [range, setRange] = useState<[Dayjs, Dayjs]>([
    dayjs().subtract(24, 'hour'),
    dayjs(),
  ]);
  const [submitted, setSubmitted] = useState(() => ({
    from: dayjs().subtract(24, 'hour').toISOString(),
    to: dayjs().toISOString(),
  }));
  // 首次只展示整机功率，避免一次挂载所有传感器明细；用户清空选择时仍可显式查看全部。
  const [selected, setSelected] = useState<string[]>(['system|power.watts']);
  const query = useQuery({
    queryKey: ['oob', id, 'history', submitted],
    retry: false,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    queryFn: ({ signal }) => getHistory(id, submitted, signal),
  });
  // 百分比沿唯一指标查询；仅有缺测空桶时不增加无意义选项。
  const data = (query.data?.success ? query.data.data : [])
    .filter(
      (series) =>
        series.metricCode !== 'fan.percent' ||
        series.points.some(
          (point) =>
            point.count > 0 &&
            point.average !== null &&
            Number.isFinite(point.average),
        ),
    )
    .map((series) => ({ ...series, name: metricDisplayName(series, t) }));
  const visible = data.filter(
    (series) =>
      !selected.length ||
      selected.includes(`${series.sensorKey}|${series.metricCode}`),
  );
  const groups = useMemo(() => {
    const units = new Map<string, HistorySeriesDto[]>();
    for (const series of visible)
      units.set(series.unit, [...(units.get(series.unit) || []), series]);
    return [...units.entries()];
  }, [visible]);
  return (
    <Space orientation="vertical" size="large" style={{ width: '100%' }}>
      <Typography.Paragraph type="secondary">
        {t('oob.history.help')}
      </Typography.Paragraph>
      <Space wrap>
        <DatePicker.RangePicker
          showTime
          value={range}
          allowClear={false}
          onChange={(value) => {
            if (value?.[0] && value[1]) setRange([value[0], value[1]]);
          }}
        />
        <Button
          type="primary"
          loading={query.isFetching}
          onClick={() => {
            const next = {
              from: range[0].toISOString(),
              to: range[1].toISOString(),
            };
            if (next.from === submitted.from && next.to === submitted.to)
              void query.refetch();
            else setSubmitted(next);
          }}
        >
          {t('oob.history.query')}
        </Button>
        <Select
          mode="multiple"
          allowClear
          showSearch={{ optionFilterProp: 'label' }}
          placeholder={t('oob.history.sensor')}
          style={{ minWidth: 280, maxWidth: 520 }}
          value={selected}
          onChange={setSelected}
          options={data.map((series) => ({
            value: `${series.sensorKey}|${series.metricCode}`,
            label: `${series.name} (${series.unit})`,
          }))}
        />
      </Space>
      {(query.error || query.data?.success === false) && (
        <Alert
          showIcon
          type="error"
          title={t('oob.load.failed')}
          description={
            query.data?.success === false
              ? query.data.errorMessage
              : (query.error as Error).message
          }
        />
      )}
      {!query.isLoading && !data.length && (
        <Empty description={t('oob.history.empty')} />
      )}
      {groups.map(([unit, series]) => {
        const points = series.flatMap((item) =>
          item.points.map((point) => ({
            time: formatTime(point.time),
            value: point.average,
            series: item.name,
            count: point.count,
          })),
        );
        return (
          <Card key={unit} title={unit} loading={query.isLoading}>
            <Line
              height={300}
              data={points}
              xField="time"
              yField="value"
              colorField="series"
              connectNulls={false}
              theme={
                initialState?.settings?.navTheme === 'realDark'
                  ? 'dark'
                  : 'light'
              }
              axis={{
                y: {
                  title: unit,
                  labelFormatter: (value: number | string) =>
                    formatReading(Number(value)),
                },
                x: { title: false, labelAutoRotate: true, labelAutoHide: true },
              }}
              tooltip={{
                title: 'time',
                items: [
                  {
                    channel: 'y',
                    name: unit,
                    valueFormatter: (value: number | null) =>
                      formatReading(value),
                  },
                ],
              }}
            />
          </Card>
        );
      })}
      {visible.map((series) => (
        <Card
          key={`${series.sensorKey}|${series.metricCode}`}
          size="small"
          title={`${series.name} · ${series.unit}`}
        >
          <ManagedTable<HistoryPointDto>
            tableId={`oob.history.points.v1:${id}:${encodeURIComponent(series.sensorKey)}:${encodeURIComponent(series.metricCode)}`}
            rowKey="time"
            size="small"
            dataSource={series.points}
            pagination={{ defaultPageSize: 5, showSizeChanger: true }}
            scroll={{ x: 650 }}
            columns={[
              {
                title: t('oob.observedAt'),
                dataIndex: 'time',
                render: (value) => formatTime(value),
              },
              {
                title: t('oob.history.average'),
                dataIndex: 'average',
                render: (value) => formatReading(value, series.unit),
              },
              {
                title: t('oob.history.minimum'),
                dataIndex: 'minimum',
                render: (value) => formatReading(value, series.unit),
              },
              {
                title: t('oob.history.maximum'),
                dataIndex: 'maximum',
                render: (value) => formatReading(value, series.unit),
              },
              { title: t('oob.history.count'), dataIndex: 'count' },
            ]}
          />
        </Card>
      ))}
    </Space>
  );
}
