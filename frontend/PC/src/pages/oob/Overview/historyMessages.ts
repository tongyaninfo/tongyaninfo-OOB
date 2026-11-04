/** 总览交互与本地历史查询三语言资源，业务值及设备型号保持原文。 */
export const historyTrendsZhCN = {
  'oob.monitor.legend.hide': '隐藏 {name}',
  'oob.monitor.legend.show': '显示 {name}',
  'oob.monitor.legend.allHidden': '所有曲线已隐藏，点击图例重新显示',
  'oob.monitor.history.query': '查询历史趋势',
  'oob.monitor.history.title': '{name} · {panel} · 历史趋势',
  'oob.monitor.history.period': '历史时间范围',
  'oob.monitor.history.24h': '过去一天',
  'oob.monitor.history.7d': '过去一周',
  'oob.monitor.history.month': '过去一个月',
  'oob.monitor.history.custom': '自选范围',
  'oob.monitor.history.from': '开始时间',
  'oob.monitor.history.to': '结束时间',
  'oob.monitor.history.customHelp':
    '时间按上海时区（UTC+8）选择，单次最多查询366天；选定后点击查询。',
  'oob.monitor.history.invalidRange':
    '请选择有效的开始和结束时间，结束时间须晚于开始时间，且范围不超过366天。',
  'oob.monitor.history.local':
    '仅查询本地数据库已入库的采集数据，缺失时段保持空白。',
  'oob.monitor.history.window': '查询区间：{from} — {to}',
  'oob.monitor.history.bucket': '每 {minutes} 分钟聚合均值',
  'oob.monitor.history.aggregateHelp':
    '先计算每个设备的时间桶均值，再统计平均、最低、最高值；运行功率合计为该桶有数据的服务器功率均值之和，缺测不补零。',
  'oob.monitor.history.fanHelp':
    '各风扇在同一时间桶内的均值等权平均；所有服务器统一展示转速百分比（%），与 RPM 分图显示，采样缺口不补值。',
  'oob.monitor.history.empty': '所选时间范围内，本地没有可用采样数据',
} as const;

export const historyTrendsEnUS = {
  'oob.monitor.legend.hide': 'Hide {name}',
  'oob.monitor.legend.show': 'Show {name}',
  'oob.monitor.legend.allHidden':
    'All lines are hidden. Select a legend item to show one.',
  'oob.monitor.history.query': 'Query historical trends',
  'oob.monitor.history.title': '{name} · {panel} · Historical trends',
  'oob.monitor.history.period': 'History period',
  'oob.monitor.history.24h': 'Last day',
  'oob.monitor.history.7d': 'Last week',
  'oob.monitor.history.month': 'Last month',
  'oob.monitor.history.custom': 'Custom range',
  'oob.monitor.history.from': 'Start time',
  'oob.monitor.history.to': 'End time',
  'oob.monitor.history.customHelp':
    'Select times in Shanghai (UTC+8), up to 366 days per query. Then select Query.',
  'oob.monitor.history.invalidRange':
    'Select valid start and end times. The end must be after the start and the range must not exceed 366 days.',
  'oob.monitor.history.local':
    'Only persisted samples from the local database are queried. Missing periods remain empty.',
  'oob.monitor.history.window': 'Query period: {from} — {to}',
  'oob.monitor.history.bucket': 'Mean values in {minutes}-minute buckets',
  'oob.monitor.history.aggregateHelp':
    'Mean, minimum and maximum are calculated from each device’s bucket mean. Total power sums the bucket means of servers with data; missing samples are not replaced with zero.',
  'oob.monitor.history.fanHelp':
    'Fan bucket means are weighted equally. All servers use speed percentage (%), shown separately from RPM. Missing samples remain gaps.',
  'oob.monitor.history.empty':
    'No available local samples in the selected period',
} as const;

export const historyTrendsZhTW = {
  'oob.monitor.legend.hide': '隱藏 {name}',
  'oob.monitor.legend.show': '顯示 {name}',
  'oob.monitor.legend.allHidden': '所有曲線已隱藏，點擊圖例重新顯示',
  'oob.monitor.history.query': '查詢歷史趨勢',
  'oob.monitor.history.title': '{name} · {panel} · 歷史趨勢',
  'oob.monitor.history.period': '歷史時間範圍',
  'oob.monitor.history.24h': '過去一天',
  'oob.monitor.history.7d': '過去一週',
  'oob.monitor.history.month': '過去一個月',
  'oob.monitor.history.custom': '自選範圍',
  'oob.monitor.history.from': '開始時間',
  'oob.monitor.history.to': '結束時間',
  'oob.monitor.history.customHelp':
    '時間按上海時區（UTC+8）選擇，單次最多查詢366天；選定後點擊查詢。',
  'oob.monitor.history.invalidRange':
    '請選擇有效的開始和結束時間，結束時間須晚於開始時間，且範圍不超過366天。',
  'oob.monitor.history.local':
    '僅查詢本地資料庫已儲存的採樣資料，缺失時段保持空白。',
  'oob.monitor.history.window': '查詢區間：{from} — {to}',
  'oob.monitor.history.bucket': '每 {minutes} 分鐘聚合平均值',
  'oob.monitor.history.aggregateHelp':
    '先計算每個裝置的時間桶平均值，再統計平均、最低、最高值；運行功率合計為該桶有資料的伺服器功率平均值之和，缺失不補零。',
  'oob.monitor.history.fanHelp':
    '各風扇在同一時間桶內的平均值等權平均；所有伺服器統一顯示轉速百分比（%），與 RPM 分圖顯示，採樣缺口不補值。',
  'oob.monitor.history.empty': '所選時間範圍內，本地沒有可用採樣資料',
} as const;
