import { afterEach, expect, it, vi } from 'vitest';
import { apiRequest } from './http';
import {
  beginSession,
  expireSession,
  isSessionExpired,
  sessionGeneration,
  subscribeSessionExpiry,
} from './session';

afterEach(() => {
  vi.unstubAllGlobals();
  beginSession();
});
it('当前登录的并发401只销毁一次身份，上一登录的迟到401不影响新身份', async () => {
  beginSession();
  const expired = vi.fn();
  const unsubscribe = subscribeSessionExpiry(expired);
  let finish!: (value: Response) => void;
  vi.stubGlobal(
    'fetch',
    vi.fn(
      () =>
        new Promise<Response>((resolve) => {
          finish = resolve;
        }),
    ),
  );
  const pending = apiRequest('/api/users');
  beginSession();
  finish(
    new Response(
      JSON.stringify({
        success: false,
        errorCode: 'UNAUTHORIZED',
        errorMessage: 'expired',
      }),
      { status: 401 },
    ),
  );
  await pending;
  expect(expired).not.toHaveBeenCalled();
  expect(isSessionExpired()).toBe(false);
  const current = sessionGeneration();
  expireSession(current);
  expireSession(current);
  expect(expired).toHaveBeenCalledTimes(1);
  unsubscribe();
});
it('登录密码错误不会被当成现有会话撤销，业务403仍保留错误', async () => {
  beginSession();
  const expired = vi.fn();
  const unsubscribe = subscribeSessionExpiry(expired);
  vi.stubGlobal(
    'fetch',
    vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          success: false,
          errorCode: 'FORBIDDEN',
          errorMessage: 'denied',
        }),
        { status: 403 },
      ),
    ),
  );
  const result = await apiRequest('/api/users');
  expect(result).toMatchObject({
    success: false,
    status: 403,
    errorCode: 'FORBIDDEN',
  });
  expect(expired).not.toHaveBeenCalled();
  unsubscribe();
});
