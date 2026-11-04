import type { useOobIntl } from '../intl';

const errors = {
  ACCOUNT_EXISTS: 'oob.users.error.ACCOUNT_EXISTS',
  USER_VERSION_CONFLICT: 'oob.users.error.USER_VERSION_CONFLICT',
  LAST_USER_MANAGER: 'oob.users.error.LAST_USER_MANAGER',
  LAST_SYSTEM_ADMIN: 'oob.users.error.LAST_SYSTEM_ADMIN',
  LAST_SYSTEM_ADMIN_MANAGER: 'oob.users.error.LAST_SYSTEM_ADMIN_MANAGER',
  FORBIDDEN: 'oob.users.error.FORBIDDEN',
  INVALID_CURRENT_PASSWORD: 'oob.users.error.INVALID_CURRENT_PASSWORD',
  PASSWORD_CONFIRMATION_MISMATCH:
    'oob.users.error.PASSWORD_CONFIRMATION_MISMATCH',
  INVALID_PASSWORD: 'oob.users.error.INVALID_PASSWORD',
  INVALID_USER: 'oob.users.error.INVALID_USER',
  USER_NOT_FOUND: 'oob.users.error.USER_NOT_FOUND',
  UNAUTHORIZED: 'oob.users.error.UNAUTHORIZED',
} as const;
/** 按正式错误码翻译，不解析服务端中文判断状态。 */
export function userError(
  failure: { errorCode: string; errorMessage: string },
  t: ReturnType<typeof useOobIntl>,
) {
  const key = errors[failure.errorCode as keyof typeof errors];
  return key ? t(key) : failure.errorMessage;
}
