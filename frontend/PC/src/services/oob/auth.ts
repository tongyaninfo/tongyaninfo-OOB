import {
  getExceptionCaptureIdentity,
  setExceptionCaptureIdentity,
} from './browserExceptions';
import { stopConsoleConnections } from './consoleLifecycle';
import { apiRequest, resetCsrfToken } from './http';
import type { ChangePasswordRequest, CurrentUserDto } from './models';
import { clearPreferences } from './preferences';
import { stopTelemetryConnections } from './realtime';
import { beginSession, expireSession, sessionGeneration } from './session';

/** 当前用户合同沿用模板展示字段，所有权限均取服务端返回。 */
export async function currentUser(signal?: AbortSignal) {
  const expected = sessionGeneration();
  const result = await apiRequest<CurrentUserDto>('/api/auth/current-user', {
    signal,
  });
  if (expected === sessionGeneration()) {
    if (result.success) setExceptionCaptureIdentity(result.data.userid);
    else if (result.status === 401) setExceptionCaptureIdentity(undefined);
  }
  return result;
}

/** 保留原模板登录处理状态，身份由后端 Cookie 建立，不在浏览器保存密码。 */
export async function login(values: API.LoginParams): Promise<API.LoginResult> {
  const user = await apiRequest<CurrentUserDto>('/api/auth/login', {
    method: 'POST',
    body: JSON.stringify({
      username: values.username,
      password: values.password,
    }),
  });
  if (!user.success)
    return { status: 'error', type: 'account', currentAuthority: '' };
  resetCsrfToken();
  beginSession();
  setExceptionCaptureIdentity(user.data.userid);
  return { status: 'ok', type: 'account', currentAuthority: user.data.access };
}

/** 退出后令牌失效，新身份首次写请求重新获取。 */
export async function outLogin() {
  const previousIdentity = getExceptionCaptureIdentity();
  setExceptionCaptureIdentity(undefined);
  await Promise.all([stopTelemetryConnections(), stopConsoleConnections()]);
  const result = await apiRequest<unknown>('/api/auth/logout', {
    method: 'POST',
  });
  if (result.success) {
    clearPreferences();
    resetCsrfToken();
  } else setExceptionCaptureIdentity(previousIdentity);
  return result;
}

/** 服务端改密已撤销所有旧会话；成功后立即销毁当前页面身份。 */
export async function changePassword(values: ChangePasswordRequest) {
  const result = await apiRequest<boolean>('/api/auth/change-password', {
    method: 'POST',
    body: JSON.stringify(values),
  });
  if (result.success) expireSession();
  return result;
}

/** 失效时仅释放浏览器资源，不再补发退出或介质卸载等写请求。 */
export async function clearExpiredSession() {
  setExceptionCaptureIdentity(undefined);
  clearPreferences();
  resetCsrfToken();
  await Promise.allSettled([
    stopTelemetryConnections(),
    stopConsoleConnections(true),
  ]);
}
