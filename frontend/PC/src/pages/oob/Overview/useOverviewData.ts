import { useQueries } from '@tanstack/react-query';
import { useState } from 'react';
import { getRecent, getServer } from '@/services/oob/api';
import type { ServerTelemetryDto } from '@/services/oob/models';
import { controllerTemperatures } from './controllers';
import { isOverviewMetric } from './metrics';
import type { OverviewServerData } from './overviewData.types';
import {
  buildRecentHistory,
  type ConnectionGap,
  RECENT_WINDOW_MS,
} from './recent';

/** 首次进入固定读取窗口；翻页、推送、聚焦及网络恢复均不改变查询身份。 */
export default function useOverviewData(
  snapshots: ServerTelemetryDto[],
  events: Record<string, ServerTelemetryDto[]>,
  gaps: ConnectionGap[],
  now: number,
): OverviewServerData[] {
  const [to] = useState(Date.now);
  const inventoryQueries = useQueries({
    queries: snapshots.map(({ server }) => ({
      queryKey: ['oob', server.id, 'overview-inventory'],
      queryFn: ({ signal }: { signal: AbortSignal }) =>
        getServer(server.id, signal),
      retry: false,
      staleTime: Infinity,
      refetchOnMount: 'always' as const,
      refetchOnWindowFocus: false,
      refetchOnReconnect: false,
    })),
  });
  const recentQueries = useQueries({
    queries: snapshots.map(({ server }) => ({
      queryKey: ['oob', server.id, 'recent', to],
      queryFn: ({ signal }: { signal: AbortSignal }) =>
        getRecent(
          server.id,
          {
            from: new Date(to - RECENT_WINDOW_MS).toISOString(),
            to: new Date(to).toISOString(),
          },
          signal,
        ),
      retry: false,
      staleTime: Infinity,
      refetchOnMount: 'always' as const,
      refetchOnWindowFocus: false,
      refetchOnReconnect: false,
    })),
  });

  return snapshots.map((snapshot, index) => {
    const inventoryQuery = inventoryQueries[index];
    const recentQuery = recentQueries[index];
    const inventory = inventoryQuery.data?.success
      ? inventoryQuery.data.data.inventory
      : null;
    const recent = recentQuery.data?.success ? recentQuery.data.data : null;
    // 先后快照可以缺少某个探头，历史中的唯一库存映射同样保留，不按数组顺序猜测。
    const controllerSensorIds = new Set(
      [
        ...controllerTemperatures(inventory, snapshot.telemetry.metrics),
        ...controllerTemperatures(inventory, recent?.series || []),
      ].flatMap((controller) =>
        controller.temperature ? [controller.temperature.sensorKey] : [],
      ),
    );
    const keepMetric = (metric: {
      sensorKey: string;
      metricCode: string;
      name: string;
      unit: string;
    }) =>
      isOverviewMetric(metric) ||
      (metric.metricCode === 'temperature.celsius' &&
        controllerSensorIds.has(metric.sensorKey));
    const combinedGaps = [
      ...gaps,
      ...(recent?.gaps || []).map((gap) => ({
        from: Date.parse(gap.from),
        to: Date.parse(gap.to),
      })),
    ];
    const issues: OverviewServerData['issues'] = [];
    const queries = [
      ['inventory', inventoryQuery],
      ['history', recentQuery],
    ] as const;
    for (const [kind, query] of queries) {
      if (query.data?.success === false) {
        if (query.data.errorCode !== 'CANCELED')
          issues.push({ kind, message: query.data.errorMessage });
      } else if (query.error) {
        issues.push({ kind, message: query.error.message });
      }
    }
    return {
      snapshot,
      inventory,
      history: buildRecentHistory(
        (recent?.series || []).filter(keepMetric),
        (events[snapshot.server.id] || []).map((event) => ({
          ...event,
          telemetry: {
            ...event.telemetry,
            metrics: event.telemetry.metrics.filter(keepMetric),
          },
        })),
        combinedGaps,
        now,
        to,
      ),
      gaps: combinedGaps,
      loading: inventoryQuery.isLoading || recentQuery.isLoading,
      issues,
    };
  });
}
