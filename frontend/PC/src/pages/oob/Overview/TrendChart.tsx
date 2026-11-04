import { G2, Line } from '@ant-design/plots';
import { useModel } from '@umijs/max';
import React, { useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { HistorySeriesDto } from '@/services/oob/models';
import { formatReading, formatTime } from '../format';
import { useOobIntl } from '../intl';
import { sensorIdentity, trendPoints, trendSeriesLabel } from './metrics';
import useStyles from './TrendChart.style';

export type TrendChartProps = {
  series: HistorySeriesDto[];
  unit: string;
  scopeKey: string;
  title?: React.ReactNode;
  height?: number;
  xLabelFormatter?: (value: number | string) => string;
  /** 聚合点的有效参与数量等补充信息，仅由已加载数据计算。 */
  pointDescription?: (time: number) => string | undefined;
  /** 需要显示虚线的稳定 sensorIdentity；不依赖翻译后的图例名称。 */
  dashedSeries?: readonly string[];
};
const clock = new Intl.DateTimeFormat('zh-CN', {
  timeZone: 'Asia/Shanghai',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hour12: false,
});
function recentTime(value: number | string) {
  const time =
    typeof value === 'number'
      ? value
      : /^\d+$/.test(value)
        ? Number(value)
        : Date.parse(value);
  return Number.isFinite(time) ? clock.format(time) : String(value);
}

/** 每个服务器/面板单独持有显示状态；切换scope即重新挂载，旧选择不能带入下一设备。 */
export default function TrendChart(props: TrendChartProps) {
  return <ScopedTrendChart key={props.scopeKey} {...props} />;
}

/** 纯展示已保存的数据；图例开关只过滤本地曲线，不查询、采样、插值或修改任何读数。 */
function ScopedTrendChart({
  series,
  unit,
  title,
  height = 150,
  xLabelFormatter = recentTime,
  pointDescription,
  dashedSeries,
}: TrendChartProps) {
  const t = useOobIntl();
  const { styles } = useStyles();
  const { initialState } = useModel('@@initialState');
  const [hidden, setHidden] = useState<Set<string>>(() => new Set());
  const chartContainer = useRef<HTMLDivElement>(null);
  const [chartWidth, setChartWidth] = useState<number>();
  const entries = useMemo(() => {
    const unique = [
      ...new Map(series.map((item) => [sensorIdentity(item), item])).values(),
    ];
    return unique.map((item) => {
      const label = trendSeriesLabel(item, unique);
      return {
        key: sensorIdentity(item),
        label,
        dashed: dashedSeries?.includes(sensorIdentity(item)) === true,
        points: trendPoints([{ ...item, name: label }]).map((point) => ({
          ...point,
          dashed: dashedSeries?.includes(sensorIdentity(item)) === true,
        })),
      };
    });
  }, [series, dashedSeries]);
  const domain = [...new Set(entries.map((entry) => entry.label))].sort(
    (left, right) => left.localeCompare(right, undefined, { numeric: true }),
  );
  const chartTheme =
    initialState?.settings?.navTheme === 'realDark' ? 'dark' : 'light';
  const palette = useMemo(
    () =>
      (chartTheme === 'dark' ? G2.Dark({}) : G2.Light({}))
        .category20 as string[],
    [chartTheme],
  );
  const colors = domain.map((_, index) => palette[index % palette.length]);
  const visible = entries.filter((entry) => !hidden.has(entry.key));
  const points = visible.flatMap((entry) => entry.points);
  const allHidden = entries.length > 0 && visible.length === 0;
  const hasVisiblePoints = points.some((point) => point.value !== null);
  useLayoutEffect(() => {
    if (!hasVisiblePoints || !chartContainer.current) return;
    const container = chartContainer.current;
    const measure = () => {
      // clientWidth 不受 Modal 开场 transform 缩放影响，避免把动画中的尺寸固化。
      const width = container.clientWidth;
      if (width > 0)
        setChartWidth((previous) => (previous === width ? previous : width));
    };
    // G2 的 autoFit 只监听窗口变化；侧栏展开、网格收缩和弹窗打开需按容器宽度重绘。
    // 不裁切画布或缩放位图，坐标轴、曲线与悬浮命中区域使用同一真实尺寸。
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(container);
    return () => observer.disconnect();
  }, [hasVisiblePoints]);
  const toggle = (key: string) =>
    setHidden((previous) => {
      const next = new Set(previous);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  return (
    <>
      <div className={styles.header}>
        <span>{title ?? t('oob.monitor.recent')}</span>
        <span>{unit}</span>
      </div>
      {hasVisiblePoints ? (
        <div
          ref={chartContainer}
          className={styles.chart}
          style={{ height }}
          role="img"
          aria-label={`${typeof title === 'string' ? title : t('oob.monitor.recent')} ${unit}`}
        >
          <Line
            width={chartWidth}
            height={height}
            autoFit={chartWidth === undefined}
            containerStyle={{ width: '100%', height: '100%', minWidth: 0 }}
            data={points}
            xField="time"
            yField="value"
            colorField="series"
            connectNulls={false}
            animate={false}
            style={{
              // G2 的折线样式回调接收该条曲线的所有原始点，缺口不改变线型。
              lineDash: (data: { dashed: boolean }[]) =>
                data[0]?.dashed ? [6, 4] : [],
            }}
            scale={{
              x: { type: 'time' },
              color: { type: 'ordinal', domain, range: colors },
            }}
            theme={chartTheme}
            axis={{
              x: {
                title: false,
                tickCount: 3,
                labelAutoHide: true,
                labelAutoRotate: false,
                labelFormatter: xLabelFormatter,
              },
              y: {
                title: false,
                labelAutoHide: true,
                labelFormatter: (value: number | string) =>
                  formatReading(Number(value)),
              },
            }}
            legend={false}
            tooltip={{
              title: (datum: { time: number }) =>
                [
                  formatTime(new Date(datum.time).toISOString()),
                  pointDescription?.(datum.time),
                ]
                  .filter(Boolean)
                  .join(' · '),
              // 只格式化提示文字，不舍入曲线数据或后续统计的输入。
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
        </div>
      ) : (
        <div className={styles.empty} role={allHidden ? 'status' : undefined}>
          {allHidden ? t('oob.monitor.legend.allHidden') : '-'}
        </div>
      )}
      {entries.length > 0 && (
        <ul className={styles.legend} aria-label={t('oob.monitor.legend')}>
          {[...entries]
            .sort((left, right) =>
              left.label.localeCompare(right.label, undefined, {
                numeric: true,
              }),
            )
            .map((entry) => {
              const shown = !hidden.has(entry.key);
              return (
                <li key={entry.key} className={styles.item}>
                  <button
                    type="button"
                    className={`${styles.button} ${shown ? '' : styles.hidden}`}
                    aria-pressed={shown}
                    title={t(
                      shown
                        ? 'oob.monitor.legend.hide'
                        : 'oob.monitor.legend.show',
                      { name: entry.label },
                    )}
                    onClick={() => toggle(entry.key)}
                    onKeyDown={(event) => {
                      if (event.key !== 'Enter' && event.key !== ' ') return;
                      // 显式阻止浏览器再次合成click；按住按键不连续翻转，也不滚动页面。
                      event.preventDefault();
                      if (!event.repeat) toggle(entry.key);
                    }}
                    onKeyUp={(event) => {
                      if (event.key === 'Enter' || event.key === ' ')
                        event.preventDefault();
                    }}
                  >
                    <span
                      aria-hidden="true"
                      className={styles.marker}
                      style={{
                        borderTopColor: colors[domain.indexOf(entry.label)],
                        borderTopStyle: entry.dashed ? 'dashed' : 'solid',
                      }}
                    />
                    <span className={styles.label}>{entry.label}</span>
                  </button>
                </li>
              );
            })}
        </ul>
      )}
    </>
  );
}
