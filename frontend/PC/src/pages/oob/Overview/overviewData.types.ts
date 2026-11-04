import type {
  DeviceInventory,
  HistorySeriesDto,
  ServerTelemetryDto,
} from '@/services/oob/models';
import type { ConnectionGap } from './recent';

/** 聚合页与单机页共用同一份本地查询结果，切换页面不重新请求或订阅。 */
export type OverviewServerData = {
  snapshot: ServerTelemetryDto;
  inventory: DeviceInventory | null;
  history: HistorySeriesDto[];
  gaps: ConnectionGap[];
  loading: boolean;
  issues: { kind: 'inventory' | 'history'; message: string }[];
};
