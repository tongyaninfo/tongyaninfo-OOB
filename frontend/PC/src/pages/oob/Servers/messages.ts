export const configurationZhCN = {
  'oob.configuration.export': '导出配置',
  'oob.configuration.import': '导入配置',
  'oob.configuration.exportHelp':
    '导出全部服务器配置，包含带外账号和登录密码，不受当前搜索或分页影响。文件使用下方密码加密，请妥善保存文件和密码。',
  'oob.configuration.importHelp':
    '导入加密的服务器配置文件。按带外管理地址与控制器类型匹配：相同配置跳过，不同配置覆盖，不存在则新增。文件中未包含的服务器保留。',
  'oob.configuration.password': '文件密码',
  'oob.configuration.confirmPassword': '确认文件密码',
  'oob.configuration.passwordHelp': '8–128 个字符，不能全部为空白。',
  'oob.configuration.passwordInvalid': '请输入 8–128 个字符的文件密码',
  'oob.configuration.passwordMismatch': '两次输入的文件密码不一致',
  'oob.configuration.file': '配置文件（JSON，最大 4 MiB）',
  'oob.configuration.fileRequired': '请选择要导入的配置文件',
  'oob.configuration.fileTooLarge': '配置文件不能超过 4 MiB',
  'oob.configuration.fileInvalid': '文件不是有效的加密服务器配置，请重新选择',
  'oob.configuration.exportSaved': '加密配置文件已生成',
  'oob.configuration.importResult':
    '导入完成：新增 {added} 台，覆盖 {updated} 台，跳过 {skipped} 台。',
  'oob.configuration.importUnknown':
    '请求结果未知。请刷新列表核对后再重新导入，系统不会自动重试。',
  'oob.configuration.reload': '刷新列表',
  'oob.configuration.failed': '配置操作失败，请稍后重试',
  'oob.configuration.close': '关闭',
};

export const configurationEnUS: Record<keyof typeof configurationZhCN, string> =
  {
    'oob.configuration.export': 'Export configuration',
    'oob.configuration.import': 'Import configuration',
    'oob.configuration.exportHelp':
      'Export all server settings, including BMC accounts and login passwords, regardless of search or pagination. The file is encrypted with the password below. Keep both the file and password safe.',
    'oob.configuration.importHelp':
      'Import an encrypted server configuration file. Servers are matched by BMC address and controller type: identical settings are skipped, different settings are overwritten, and unmatched servers are added. Servers absent from the file are retained.',
    'oob.configuration.password': 'File password',
    'oob.configuration.confirmPassword': 'Confirm file password',
    'oob.configuration.passwordHelp':
      'Use 8–128 characters, not only whitespace.',
    'oob.configuration.passwordInvalid':
      'Enter a file password of 8–128 characters',
    'oob.configuration.passwordMismatch': 'The file passwords do not match',
    'oob.configuration.file': 'Configuration file (JSON, maximum 4 MiB)',
    'oob.configuration.fileRequired': 'Select a configuration file to import',
    'oob.configuration.fileTooLarge':
      'The configuration file must not exceed 4 MiB',
    'oob.configuration.fileInvalid':
      'Select a valid encrypted server configuration file',
    'oob.configuration.exportSaved': 'Encrypted configuration file generated',
    'oob.configuration.importResult':
      'Import complete: {added} added, {updated} overwritten, {skipped} skipped.',
    'oob.configuration.importUnknown':
      'The request outcome is unknown. Refresh the list to verify before importing again. The system will not retry automatically.',
    'oob.configuration.reload': 'Refresh list',
    'oob.configuration.failed':
      'The configuration operation failed. Try again later.',
    'oob.configuration.close': 'Close',
  };

export const configurationZhTW: Record<keyof typeof configurationZhCN, string> =
  {
    'oob.configuration.export': '匯出設定',
    'oob.configuration.import': '匯入設定',
    'oob.configuration.exportHelp':
      '匯出全部伺服器設定，包含帶外帳號和登入密碼，不受目前搜尋或分頁影響。檔案使用下方密碼加密，請妥善保存檔案和密碼。',
    'oob.configuration.importHelp':
      '匯入加密的伺服器設定檔。依帶外管理位址與控制器類型比對：相同設定略過，不同設定覆寫，不存在則新增。檔案中未包含的伺服器保留。',
    'oob.configuration.password': '檔案密碼',
    'oob.configuration.confirmPassword': '確認檔案密碼',
    'oob.configuration.passwordHelp': '8–128 個字元，不能全部為空白。',
    'oob.configuration.passwordInvalid': '請輸入 8–128 個字元的檔案密碼',
    'oob.configuration.passwordMismatch': '兩次輸入的檔案密碼不一致',
    'oob.configuration.file': '設定檔（JSON，最大 4 MiB）',
    'oob.configuration.fileRequired': '請選擇要匯入的設定檔',
    'oob.configuration.fileTooLarge': '設定檔不能超過 4 MiB',
    'oob.configuration.fileInvalid': '檔案不是有效的加密伺服器設定，請重新選擇',
    'oob.configuration.exportSaved': '加密設定檔已產生',
    'oob.configuration.importResult':
      '匯入完成：新增 {added} 台，覆寫 {updated} 台，略過 {skipped} 台。',
    'oob.configuration.importUnknown':
      '請求結果未知。請重新整理列表核對後再重新匯入，系統不會自動重試。',
    'oob.configuration.reload': '重新整理列表',
    'oob.configuration.failed': '設定操作失敗，請稍後重試',
    'oob.configuration.close': '關閉',
  };
