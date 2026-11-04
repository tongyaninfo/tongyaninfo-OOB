/** 总览专用文案由主语言入口合并，传感器原始名称仍按设备返回展示。 */
import {
  aggregateEnUS,
  aggregateZhCN,
  aggregateZhTW,
} from './aggregateMessages';
import { fanDetailsEnUS, fanDetailsZhCN, fanDetailsZhTW } from './fanMessages';
import {
  historyTrendsEnUS,
  historyTrendsZhCN,
  historyTrendsZhTW,
} from './historyMessages';

export const overviewZhCN = {
  ...aggregateZhCN,
  ...fanDetailsZhCN,
  ...historyTrendsZhCN,
  'oob.monitor.temperatures': 'CPU / 阵列卡温度',
  'oob.monitor.averageRpm': '平均转速',
  'oob.monitor.averageSpeedPercent': '平均转速百分比（%）',
  'oob.monitor.fanAverageCount':
    '有效读数：RPM {rpm}/{total}，转速百分比（%）{speedPercent}/{total}',
  'oob.monitor.percentHelp':
    '各类控制器统一使用转速百分比（%）；百分比与 RPM 沿用设备原始读数，不互相换算，未提供的读数显示 -。',
  'oob.monitor.cpuSlot': 'CPU {number}',
  'oob.monitor.cpu': 'CPU 温度',
  'oob.monitor.array': '阵列卡温度',
  'oob.monitor.fans': '风扇读数',
  'oob.monitor.power': '整机功率',
  'oob.monitor.recent': '最近 15 分钟',
  'oob.monitor.legend': '图例',
  'oob.monitor.noCpu': '当前接口未提供 CPU 温度',
  'oob.monitor.noArray': '当前接口未提供阵列卡温度',
  'oob.monitor.noFans': '当前接口未提供风扇读数',
  'oob.monitor.noPower': '当前接口未提供整机功率',
  'oob.monitor.noHistory': '近期暂无有效样本',
  'oob.monitor.fanSpeedPercent': '控制器仅提供百分比，未提供 RPM',
  'oob.monitor.fanBoth': '转速百分比（%）与 RPM 分别展示，不相互换算',
  'oob.monitor.arrayZone': '周边区域温度',
  'oob.monitor.capacity': '可用电源容量',
  'oob.monitor.peak': '设备窗口峰值',
  'oob.monitor.lastKnown': '上次采集值',
  'oob.monitor.disconnected': '实时连接中断，显示上次采集值',
  'oob.monitor.interval': '采样间隔 {seconds} 秒',
  'oob.monitor.health': '设备报告：{health}',
  'oob.monitor.historyHint': '断线及缺测显示断点，不补零',
} as const;

export const overviewEnUS = {
  ...aggregateEnUS,
  ...fanDetailsEnUS,
  ...historyTrendsEnUS,
  'oob.monitor.temperatures': 'CPU / RAID controller temperature',
  'oob.monitor.averageRpm': 'Average speed',
  'oob.monitor.averageSpeedPercent': 'Average speed percentage (%)',
  'oob.monitor.fanAverageCount':
    'Available readings: RPM {rpm}/{total}, speed percentage (%) {speedPercent}/{total}',
  'oob.monitor.percentHelp':
    'All controllers use speed percentage (%). Percentage and RPM retain device readings and are never converted into each other. Unavailable readings show -.',
  'oob.monitor.cpuSlot': 'CPU {number}',
  'oob.monitor.cpu': 'CPU temperature',
  'oob.monitor.array': 'RAID controller temperature',
  'oob.monitor.fans': 'Fan readings',
  'oob.monitor.power': 'System power',
  'oob.monitor.recent': 'Last 15 minutes',
  'oob.monitor.legend': 'Legend',
  'oob.monitor.noCpu': 'CPU temperature is not provided by this interface',
  'oob.monitor.noArray':
    'RAID controller temperature is not provided by this interface',
  'oob.monitor.noFans': 'Fan readings are not provided by this interface',
  'oob.monitor.noPower': 'System power is not provided by this interface',
  'oob.monitor.noHistory': 'No valid recent samples',
  'oob.monitor.fanSpeedPercent':
    'This controller provides percentages; RPM is unavailable',
  'oob.monitor.fanBoth':
    'Speed percentage (%) and RPM are displayed separately, never converted into each other',
  'oob.monitor.arrayZone': 'Surrounding zone temperature',
  'oob.monitor.capacity': 'Available power capacity',
  'oob.monitor.peak': 'Device window peak',
  'oob.monitor.lastKnown': 'Last collected reading',
  'oob.monitor.disconnected':
    'Live connection lost; showing last collected readings',
  'oob.monitor.interval': 'Sampling every {seconds} seconds',
  'oob.monitor.health': 'Device reports: {health}',
  'oob.monitor.historyHint':
    'Disconnections and missing samples remain gaps, never zeroes',
} as const;

export const overviewZhTW = {
  ...aggregateZhTW,
  ...fanDetailsZhTW,
  ...historyTrendsZhTW,
  'oob.monitor.temperatures': 'CPU / 陣列卡溫度',
  'oob.monitor.averageRpm': '平均轉速',
  'oob.monitor.averageSpeedPercent': '平均轉速百分比（%）',
  'oob.monitor.fanAverageCount':
    '有效讀數：RPM {rpm}/{total}，轉速百分比（%）{speedPercent}/{total}',
  'oob.monitor.percentHelp':
    '各類控制器統一使用轉速百分比（%）；百分比與 RPM 沿用裝置原始讀數，不互相換算，未提供的讀數顯示 -。',
  'oob.monitor.cpuSlot': 'CPU {number}',
  'oob.monitor.cpu': 'CPU 溫度',
  'oob.monitor.array': '陣列卡溫度',
  'oob.monitor.fans': '風扇讀數',
  'oob.monitor.power': '整機功率',
  'oob.monitor.recent': '最近 15 分鐘',
  'oob.monitor.legend': '圖例',
  'oob.monitor.noCpu': '當前接口未提供 CPU 溫度',
  'oob.monitor.noArray': '當前接口未提供陣列卡溫度',
  'oob.monitor.noFans': '當前接口未提供風扇讀數',
  'oob.monitor.noPower': '當前接口未提供整機功率',
  'oob.monitor.noHistory': '近期暫無有效樣本',
  'oob.monitor.fanSpeedPercent': '控制器僅提供百分比，未提供 RPM',
  'oob.monitor.fanBoth': '轉速百分比（%）與 RPM 分別顯示，不相互換算',
  'oob.monitor.arrayZone': '周邊區域溫度',
  'oob.monitor.capacity': '可用電源容量',
  'oob.monitor.peak': '設備窗口峰值',
  'oob.monitor.lastKnown': '上次採集值',
  'oob.monitor.disconnected': '實時連接中斷，顯示上次採集值',
  'oob.monitor.interval': '採樣間隔 {seconds} 秒',
  'oob.monitor.health': '設備報告：{health}',
  'oob.monitor.historyHint': '斷線及缺測顯示斷點，不補零',
} as const;
