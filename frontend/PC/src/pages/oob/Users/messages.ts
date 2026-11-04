export const usersZhCN = {
  'oob.users.error.ACCOUNT_EXISTS': '此账号已被未删除用户使用',
  'oob.users.error.USER_VERSION_CONFLICT':
    '用户已被修改，请关闭弹窗并刷新列表后重试',
  'oob.users.error.LAST_USER_MANAGER': '必须保留至少一个启用的用户管理账号',
  'oob.users.error.LAST_SYSTEM_ADMIN': '必须保留至少一个启用的系统管理员',
  'oob.users.error.LAST_SYSTEM_ADMIN_MANAGER':
    '必须保留至少一个启用且拥有用户管理权限的系统管理员',
  'oob.users.error.FORBIDDEN':
    '无权执行此操作；普通用户管理员不能修改系统管理员',
  'oob.users.error.INVALID_CURRENT_PASSWORD': '原密码不正确',
  'oob.users.error.PASSWORD_CONFIRMATION_MISMATCH': '两次输入的新密码不一致',
  'oob.users.error.INVALID_PASSWORD': '密码须为8至128个字符，不能全部为空白',
  'oob.users.error.INVALID_USER': '姓名和账号须为1至100字符，不能包含控制字符',
  'oob.users.error.USER_NOT_FOUND': '用户不存在或已删除，请刷新列表',
  'oob.users.error.UNAUTHORIZED': '登录状态已变更，请重新登录',
  'menu.oob.users': '用户管理',
  'oob.users.title': '用户管理',
  'oob.users.subtitle': '管理登录账号、状态及四项独立操作权限。',
  'oob.users.add': '新增用户',
  'oob.users.edit': '编辑用户',
  'oob.users.name': '姓名',
  'oob.users.username': '登录账号',
  'oob.users.search': '搜索姓名或登录账号',
  'oob.users.status': '账号状态',
  'oob.users.all': '未删除账号',
  'oob.users.enabled': '启用',
  'oob.users.disabled': '停用',
  'oob.users.deleted': '已删除',
  'oob.users.permissions': '权限',
  'oob.users.manage': '用户管理',
  'oob.users.console': '虚拟控制台',
  'oob.users.servers': '服务器管理',
  'oob.users.power': '电源操作',
  'oob.users.readonly': '仅查看看板与服务器',
  'oob.users.admin': '系统管理员',
  'oob.users.created': '创建时间',
  'oob.users.updated': '更新时间',
  'oob.users.delete': '删除用户',
  'oob.users.deleteConfirm':
    '确认删除 {name}？删除后该账号不能登录，账号名称可以重新使用。',
  'oob.users.disableConfirm':
    '确认停用 {name}？该用户的登录会话和控制台连接将失效。',
  'oob.users.enableConfirm': '确认启用 {name}？',
  'oob.users.reset': '重设密码',
  'oob.users.resetTitle': '重设 {name} 的密码',
  'oob.users.resetHint': '保存后该用户的所有登录会话和控制台连接将失效。',
  'oob.users.accountHint':
    '账号忽略首尾空格和大小写；未删除账号（含停用账号）不能重名。',
  'oob.users.permissionHint':
    '未勾选权限时只能查看看板与服务器。服务器管理包含配置、连接测试、库存刷新、手动采样与UID；电源操作包含开关机、重启、操作记录与结果核对；虚拟控制台包含键鼠和虚拟介质。',
  'oob.users.editHint':
    '修改账号、状态或权限后，该用户须重新登录。修改密码请使用“重设密码”。',
  'oob.users.protected': '仅系统管理员可修改此账号',
  'oob.users.saved': '用户已保存',
  'oob.users.deletedSaved': '用户已删除',
  'oob.users.statusSaved': '账号状态已更新',
  'oob.users.save': '保存',
  'oob.users.cancel': '取消',
  'oob.users.required': '请填写此项',
  'oob.users.unknown': '请求结果未知。请关闭弹窗并刷新列表核对，不要重复提交。',
  'oob.users.reload': '刷新列表',
  'oob.password.change': '修改密码',
  'oob.password.current': '原密码',
  'oob.password.new': '新密码',
  'oob.password.confirm': '确认新密码',
  'oob.password.initial': '初始密码',
  'oob.password.length': '密码长度须为 8–128 个字符',
  'oob.password.mismatch': '两次输入的密码不一致',
  'oob.password.changed': '密码已修改，请重新登录',
  'oob.password.resetSaved': '密码已重设',
  'oob.password.hint': '修改成功后所有旧会话将失效，请使用新密码重新登录。',
  'oob.password.unknown':
    '改密结果未知。请关闭弹窗并用新密码核对登录结果，勿重复提交。',
};

export const usersEnUS: Record<keyof typeof usersZhCN, string> = {
  'oob.users.error.ACCOUNT_EXISTS':
    'This account is already used by an account that has not been deleted.',
  'oob.users.error.USER_VERSION_CONFLICT':
    'This user was changed. Close the dialog and refresh the list before trying again.',
  'oob.users.error.LAST_USER_MANAGER':
    'Keep at least one enabled account with user management permission.',
  'oob.users.error.LAST_SYSTEM_ADMIN':
    'Keep at least one enabled system administrator.',
  'oob.users.error.LAST_SYSTEM_ADMIN_MANAGER':
    'Keep at least one enabled system administrator with user management permission.',
  'oob.users.error.FORBIDDEN':
    'You cannot perform this action. User managers cannot modify system administrators.',
  'oob.users.error.INVALID_CURRENT_PASSWORD':
    'The current password is incorrect.',
  'oob.users.error.PASSWORD_CONFIRMATION_MISMATCH':
    'The new passwords do not match.',
  'oob.users.error.INVALID_PASSWORD':
    'Use 8–128 characters, not only whitespace.',
  'oob.users.error.INVALID_USER':
    'Name and account must have 1–100 characters without control characters.',
  'oob.users.error.USER_NOT_FOUND':
    'This user does not exist or has been deleted. Refresh the list.',
  'oob.users.error.UNAUTHORIZED':
    'Your session has changed. Please sign in again.',
  'menu.oob.users': 'Users',
  'oob.users.title': 'User management',
  'oob.users.subtitle':
    'Manage accounts, status and four independent operation permissions.',
  'oob.users.add': 'Add user',
  'oob.users.edit': 'Edit user',
  'oob.users.name': 'Name',
  'oob.users.username': 'Login account',
  'oob.users.search': 'Search name or login account',
  'oob.users.status': 'Account status',
  'oob.users.all': 'Accounts not deleted',
  'oob.users.enabled': 'Enabled',
  'oob.users.disabled': 'Disabled',
  'oob.users.deleted': 'Deleted',
  'oob.users.permissions': 'Permissions',
  'oob.users.manage': 'User management',
  'oob.users.console': 'Virtual console',
  'oob.users.servers': 'Server management',
  'oob.users.power': 'Power operations',
  'oob.users.readonly': 'View dashboard and servers only',
  'oob.users.admin': 'System administrator',
  'oob.users.created': 'Created',
  'oob.users.updated': 'Updated',
  'oob.users.delete': 'Delete user',
  'oob.users.deleteConfirm':
    'Delete {name}? This account will no longer be able to sign in. Its login name can be reused.',
  'oob.users.disableConfirm':
    'Disable {name}? Their login sessions and console connections will expire.',
  'oob.users.enableConfirm': 'Enable {name}?',
  'oob.users.reset': 'Reset password',
  'oob.users.resetTitle': 'Reset password for {name}',
  'oob.users.resetHint':
    'Saving expires all login sessions and console connections for this user.',
  'oob.users.accountHint':
    'Account names are trimmed and compared without case. Accounts not deleted, including disabled accounts, must be unique.',
  'oob.users.permissionHint':
    'Without permissions, users can only view the dashboard and servers. Server management covers configuration, connection tests, inventory refresh, manual sampling and UID; power operations covers power on/off, restart, records and outcome review; virtual console includes keyboard, mouse and virtual media.',
  'oob.users.editHint':
    'Changing the account, status or permissions requires a new sign-in. Use Reset password to change the password.',
  'oob.users.protected': 'Only system administrators can modify this account',
  'oob.users.saved': 'User saved',
  'oob.users.deletedSaved': 'User deleted',
  'oob.users.statusSaved': 'Account status updated',
  'oob.users.save': 'Save',
  'oob.users.cancel': 'Cancel',
  'oob.users.required': 'This field is required',
  'oob.users.unknown':
    'The request outcome is unknown. Close this dialog and refresh the list to verify before submitting again.',
  'oob.users.reload': 'Refresh list',
  'oob.password.change': 'Change password',
  'oob.password.current': 'Current password',
  'oob.password.new': 'New password',
  'oob.password.confirm': 'Confirm new password',
  'oob.password.initial': 'Initial password',
  'oob.password.length': 'Use 8–128 characters',
  'oob.password.mismatch': 'The passwords do not match',
  'oob.password.changed': 'Password changed. Please sign in again.',
  'oob.password.resetSaved': 'Password reset',
  'oob.password.hint':
    'All existing sessions expire after this change. Sign in again with the new password.',
  'oob.password.unknown':
    'The password change outcome is unknown. Close this dialog and try signing in with the new password before submitting again.',
};

export const usersZhTW: Record<keyof typeof usersZhCN, string> = {
  'oob.users.error.ACCOUNT_EXISTS': '此帳號已被未刪除使用者使用',
  'oob.users.error.USER_VERSION_CONFLICT':
    '使用者已被修改，請關閉彈窗並重新整理列表後重試',
  'oob.users.error.LAST_USER_MANAGER': '必須保留至少一個啟用的使用者管理帳號',
  'oob.users.error.LAST_SYSTEM_ADMIN': '必須保留至少一個啟用的系統管理員',
  'oob.users.error.LAST_SYSTEM_ADMIN_MANAGER':
    '必須保留至少一個啟用且擁有使用者管理權限的系統管理員',
  'oob.users.error.FORBIDDEN':
    '無權執行此操作；一般使用者管理員不能修改系統管理員',
  'oob.users.error.INVALID_CURRENT_PASSWORD': '原密碼不正確',
  'oob.users.error.PASSWORD_CONFIRMATION_MISMATCH': '兩次輸入的新密碼不一致',
  'oob.users.error.INVALID_PASSWORD': '密碼須為8至128個字元，不能全部為空白',
  'oob.users.error.INVALID_USER': '姓名和帳號須為1至100字元，不能包含控制字元',
  'oob.users.error.USER_NOT_FOUND': '使用者不存在或已刪除，請重新整理列表',
  'oob.users.error.UNAUTHORIZED': '登入狀態已變更，請重新登入',
  'menu.oob.users': '使用者管理',
  'oob.users.title': '使用者管理',
  'oob.users.subtitle': '管理登入帳號、狀態及四項獨立操作權限。',
  'oob.users.add': '新增使用者',
  'oob.users.edit': '編輯使用者',
  'oob.users.name': '姓名',
  'oob.users.username': '登入帳號',
  'oob.users.search': '搜尋姓名或登入帳號',
  'oob.users.status': '帳號狀態',
  'oob.users.all': '未刪除帳號',
  'oob.users.enabled': '啟用',
  'oob.users.disabled': '停用',
  'oob.users.deleted': '已刪除',
  'oob.users.permissions': '權限',
  'oob.users.manage': '使用者管理',
  'oob.users.console': '虛擬控制台',
  'oob.users.servers': '伺服器管理',
  'oob.users.power': '電源操作',
  'oob.users.readonly': '僅查看看板與伺服器',
  'oob.users.admin': '系統管理員',
  'oob.users.created': '建立時間',
  'oob.users.updated': '更新時間',
  'oob.users.delete': '刪除使用者',
  'oob.users.deleteConfirm':
    '確認刪除 {name}？刪除後該帳號不能登入，帳號名稱可以重新使用。',
  'oob.users.disableConfirm':
    '確認停用 {name}？該使用者的登入工作階段和控制台連線將失效。',
  'oob.users.enableConfirm': '確認啟用 {name}？',
  'oob.users.reset': '重設密碼',
  'oob.users.resetTitle': '重設 {name} 的密碼',
  'oob.users.resetHint': '儲存後該使用者的所有登入工作階段和控制台連線將失效。',
  'oob.users.accountHint':
    '帳號忽略首尾空格和大小寫；未刪除帳號（含停用帳號）不能重名。',
  'oob.users.permissionHint':
    '未勾選權限時只能查看看板與伺服器。伺服器管理包含設定、連線測試、庫存重新整理、手動取樣與UID；電源操作包含開關機、重新啟動、操作記錄與結果核對；虛擬控制台包含鍵鼠和虛擬媒體。',
  'oob.users.editHint':
    '修改帳號、狀態或權限後，該使用者須重新登入。修改密碼請使用「重設密碼」。',
  'oob.users.protected': '僅系統管理員可修改此帳號',
  'oob.users.saved': '使用者已儲存',
  'oob.users.deletedSaved': '使用者已刪除',
  'oob.users.statusSaved': '帳號狀態已更新',
  'oob.users.save': '儲存',
  'oob.users.cancel': '取消',
  'oob.users.required': '請填寫此項',
  'oob.users.unknown':
    '請求結果未知。請關閉彈窗並重新整理列表核對，不要重複提交。',
  'oob.users.reload': '重新整理列表',
  'oob.password.change': '修改密碼',
  'oob.password.current': '原密碼',
  'oob.password.new': '新密碼',
  'oob.password.confirm': '確認新密碼',
  'oob.password.initial': '初始密碼',
  'oob.password.length': '密碼長度須為 8–128 個字元',
  'oob.password.mismatch': '兩次輸入的密碼不一致',
  'oob.password.changed': '密碼已修改，請重新登入',
  'oob.password.resetSaved': '密碼已重設',
  'oob.password.hint': '修改成功後所有舊工作階段將失效，請使用新密碼重新登入。',
  'oob.password.unknown':
    '改密結果未知。請關閉彈窗並用新密碼核對登入結果，勿重複提交。',
};
