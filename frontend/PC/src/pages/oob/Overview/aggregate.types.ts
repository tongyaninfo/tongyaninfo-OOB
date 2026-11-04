import type { HistorySeriesDto } from '@/services/oob/models';

export type AggregateKind =
  | 'cpu'
  | 'array'
  | 'inlet'
  | 'exhaust'
  | 'fanRpm'
  | 'fanSpeedPercent'
  | 'power';
export type AggregateStat = 'average' | 'minimum' | 'maximum' | 'total';
export type AggregateReadingState =
  | 'available'
  | 'missing'
  | 'unsupported'
  | 'stale'
  | 'offline'
  | 'disabled'
  | 'ambiguous'
  | 'invalid';

/** 每项带服务器身份和原采样时间；未发现实体的提示行不计入设备分母。 */
export type AggregateReading = {
  key: string;
  serverId: string;
  serverName: string;
  label: string;
  model?: string;
  slot?: string;
  value: number | null;
  unit: string;
  observedAt: string | null;
  /** 保留设备原始可用性，不能从离线、停用或过期等展示状态反推设备能力。 */
  availability?: string;
  state: AggregateReadingState;
  counted: boolean;
  health?: string | null;
};
export type AggregateCoverage = {
  validCount: number;
  totalCount: number;
  serverNames: string[];
};
/** 压力只比较同一台服务器的当前整机功率与设备声明的可用功率容量。 */
export type PowerPressureRow = {
  serverId: string;
  serverName: string;
  usedWatts: number | null;
  capacityWatts: number | null;
  percent: number | null;
  reason: 'power' | 'capacity' | null;
  powerState: AggregateReadingState;
};
export type AggregatePanel = {
  kind: AggregateKind;
  unit: string;
  readings: AggregateReading[];
  stats: Record<AggregateStat, number | null>;
  validCount: number;
  totalCount: number;
  /** name 为 average/minimum/maximum/total，由展示层统一翻译。 */
  series: HistorySeriesDto[];
  coverage: Record<number, AggregateCoverage>;
  /** 仅整机功率面板使用，缺测设备仍保留一行且不伪造百分比。 */
  pressureRows?: PowerPressureRow[];
};
export type AggregateResult = Record<AggregateKind, AggregatePanel>;
