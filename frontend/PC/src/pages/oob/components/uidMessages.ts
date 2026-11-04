/** UID定位灯的状态和显式操作文案，与服务器电源控制分开。 */
export const uidZhCN = {
  'oob.uid.title': 'UID 定位灯',
  'oob.uid.help': '点击按钮直接控制服务器定位灯，不改变主机电源状态。',
  'oob.uid.state.Off': '已熄灭',
  'oob.uid.state.Lit': '常亮',
  'oob.uid.state.Blinking': '闪烁',
  'oob.uid.state.Unknown': '未知',
  'oob.uid.on': '点亮 UID',
  'oob.uid.blink': '闪烁 UID',
  'oob.uid.off': '关闭 UID',
  'oob.uid.refresh': '刷新 UID 状态',
  'oob.uid.unavailable': '当前状态不可确认，请刷新查询后再操作。',
  'oob.uid.readOnly': '当前设备未提供可用的 UID 控制能力。',
  'oob.uid.updated': 'UID 状态已更新并完成读取核对。',
  'oob.uid.unknown': 'UID 操作结果尚未确认，未自动重发；请刷新状态核对。',
  'oob.uid.verify.failed':
    'UID 请求已返回，但未能读取确认结果；请刷新状态核对。',
  'oob.uid.reconciled': '已重新读取 UID 状态，可以按当前状态继续操作。',
} as const;

export const uidEnUS = {
  'oob.uid.title': 'UID locator light',
  'oob.uid.help':
    'Click an action to control the server locator light without changing system power.',
  'oob.uid.state.Off': 'Off',
  'oob.uid.state.Lit': 'Lit',
  'oob.uid.state.Blinking': 'Blinking',
  'oob.uid.state.Unknown': 'Unknown',
  'oob.uid.on': 'Turn UID on',
  'oob.uid.blink': 'Blink UID',
  'oob.uid.off': 'Turn UID off',
  'oob.uid.refresh': 'Refresh UID status',
  'oob.uid.unavailable':
    'The current state is unconfirmed. Refresh before sending a command.',
  'oob.uid.readOnly': 'UID control is not available for this device.',
  'oob.uid.updated':
    'UID status was updated and verified by reading the device.',
  'oob.uid.unknown':
    'The UID operation result is unconfirmed. It was not resent. Refresh the status to verify.',
  'oob.uid.verify.failed':
    'The UID request returned, but its result could not be read. Refresh the status to verify.',
  'oob.uid.reconciled':
    'UID status has been read again. You can continue using the current state.',
} as const;

export const uidZhTW = {
  'oob.uid.title': 'UID 定位燈',
  'oob.uid.help': '點擊按鈕直接控制伺服器定位燈，不改變主機電源狀態。',
  'oob.uid.state.Off': '已熄滅',
  'oob.uid.state.Lit': '常亮',
  'oob.uid.state.Blinking': '閃爍',
  'oob.uid.state.Unknown': '未知',
  'oob.uid.on': '點亮 UID',
  'oob.uid.blink': '閃爍 UID',
  'oob.uid.off': '關閉 UID',
  'oob.uid.refresh': '重新讀取 UID 狀態',
  'oob.uid.unavailable': '目前狀態無法確認，請重新查詢後再操作。',
  'oob.uid.readOnly': '目前裝置未提供可用的 UID 控制能力。',
  'oob.uid.updated': 'UID 狀態已更新並完成讀取核對。',
  'oob.uid.unknown': 'UID 操作結果尚未確認，未自動重發；請重新讀取狀態核對。',
  'oob.uid.verify.failed':
    'UID 請求已回傳，但未能讀取確認結果；請重新讀取狀態核對。',
  'oob.uid.reconciled': '已重新讀取 UID 狀態，可以依目前狀態繼續操作。',
} as const;
