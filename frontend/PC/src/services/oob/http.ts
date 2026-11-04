import type { components } from './generated';
import type { CsrfDto } from './models';
import { expireSession, sessionGeneration } from './session';

/** 统一信封继承后端合同；HTTP与客户端状态为传输层派生信息。 */
export type ApiResult<T> = Omit<
  components['schemas']['JsonElementApiResult'],
  'data'
> & { data: T };
export type RequestFailure = {
  success: false;
  data: null;
  errorCode: string;
  errorMessage: string;
  status: number;
  traceId?: string;
  kind:
    | 'business'
    | 'http'
    | 'network'
    | 'invalid-response'
    | 'canceled'
    | 'client';
  dispatched: boolean;
};
export type RequestResult<T> =
  | { success: true; data: T; status: number }
  | RequestFailure;
export function requestFailure(
  errorCode: string,
  errorMessage: string,
  status = 0,
  kind: RequestFailure['kind'] = 'business',
  dispatched = false,
): RequestFailure {
  return {
    success: false,
    data: null,
    errorCode,
    errorMessage,
    status,
    kind,
    dispatched,
  };
}

let unexpectedError: ((error: unknown) => void) | undefined;
export function setUnexpectedErrorHandler(handler?: (error: unknown) => void) {
  unexpectedError = handler;
}
let csrfToken: string | undefined;
let csrfLoading: Promise<RequestResult<string>> | undefined;

/** 取消属于调用方控制流；HTTP业务拒绝按结果返回，只有真实传输/解析异常进入异常采集。 */
async function send<T>(
  url: string,
  options: RequestInit,
  reportUnexpected = true,
): Promise<RequestResult<T>> {
  if (options.signal?.aborted)
    return requestFailure('CANCELED', '请求已取消', 0, 'canceled');
  let dispatched = false;
  const expectedSession = sessionGeneration();
  try {
    dispatched = true;
    const response = await fetch(url, {
      ...options,
      credentials: 'same-origin',
    });
    if (response.status === 401 && url !== '/api/auth/login')
      expireSession(expectedSession);
    const envelope = (await response.json()) as ApiResult<T>;
    if (!envelope || typeof envelope.success !== 'boolean') {
      if (reportUnexpected)
        unexpectedError?.(new TypeError('Invalid API response envelope'));
      return requestFailure(
        'INVALID_RESPONSE',
        '服务器响应格式无效',
        response.status,
        'invalid-response',
        true,
      );
    }
    if (!response.ok || !envelope.success) {
      const failure = requestFailure(
        envelope.errorCode || `HTTP_${response.status}`,
        envelope.errorMessage || `HTTP ${response.status}`,
        response.status,
        envelope.success === false ? 'business' : 'http',
        true,
      );
      failure.traceId = response.headers.get('X-Trace-Id') || undefined;
      return failure;
    }
    return { success: true, data: envelope.data, status: response.status };
  } catch (error) {
    if (
      options.signal?.aborted ||
      (error instanceof DOMException && error.name === 'AbortError')
    )
      return requestFailure(
        'CANCELED',
        '请求已取消',
        0,
        'canceled',
        dispatched,
      );
    if (reportUnexpected && !url.startsWith('/api/oob/exceptions/client'))
      unexpectedError?.(error);
    return requestFailure(
      error instanceof SyntaxError ? 'INVALID_RESPONSE' : 'NETWORK_ERROR',
      error instanceof SyntaxError
        ? '服务器响应格式无效'
        : '网络请求失败，请检查连接',
      0,
      error instanceof SyntaxError ? 'invalid-response' : 'network',
      dispatched,
    );
  }
}

/** 防伪令牌只在内存中保存，并发写请求共享同一次初始化，失败也不抛业务异常。 */
async function getCsrfToken(
  reportUnexpected = true,
): Promise<RequestResult<string>> {
  if (csrfToken) return { success: true, data: csrfToken, status: 200 };
  if (!csrfLoading) {
    csrfLoading = send<CsrfDto>(
      '/api/auth/csrf',
      { method: 'GET' },
      reportUnexpected,
    )
      .then((result): RequestResult<string> => {
        if (!result.success) return { ...result, dispatched: false };
        if (!result.data?.token)
          return requestFailure('CSRF_UNAVAILABLE', '防伪验证初始化失败');
        csrfToken = result.data.token;
        return { success: true, data: csrfToken, status: result.status };
      })
      .finally(() => {
        csrfLoading = undefined;
      });
  }
  return csrfLoading;
}
export function resetCsrfToken() {
  csrfToken = undefined;
}

/** 所有业务失败以结果返回。写请求不重试；dispatched保留电源动作是否可能已发送的边界。 */
export async function apiRequest<T>(
  url: string,
  options: RequestInit & { reportUnexpected?: boolean } = {},
): Promise<RequestResult<T>> {
  const { reportUnexpected = true, ...requestOptions } = options;
  if (options.signal?.aborted)
    return requestFailure('CANCELED', '请求已取消', 0, 'canceled');
  const method = (options.method || 'GET').toUpperCase();
  const headers = new Headers(options.headers);
  if (options.body !== undefined && !headers.has('Content-Type'))
    headers.set('Content-Type', 'application/json');
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
    const token = await getCsrfToken(reportUnexpected);
    if (!token.success) return token;
    headers.set('X-CSRF-TOKEN', token.data);
  }
  return send<T>(url, { ...requestOptions, method, headers }, reportUnexpected);
}

export type BinaryUploadProgress = {
  loaded: number;
  total: number;
  percent: number;
  bytesPerSecond: number;
};

/** 大镜像使用浏览器上传流和真实进度事件；复用同一内存防伪令牌，不自动重试未知结果。 */
export async function uploadBinary<T>(
  url: string,
  file: File,
  onProgress?: (progress: BinaryUploadProgress) => void,
  signal?: AbortSignal,
): Promise<RequestResult<T>> {
  if (signal?.aborted)
    return requestFailure('CANCELED', '请求已取消', 0, 'canceled');
  const token = await getCsrfToken();
  if (!token.success) return token;
  if (signal?.aborted)
    return requestFailure('CANCELED', '请求已取消', 0, 'canceled');
  const expectedSession = sessionGeneration();
  return new Promise((resolve) => {
    const request = new XMLHttpRequest();
    let dispatched = false;
    let settled = false;
    let previousAt = performance.now();
    let previousBytes = 0;
    const abort = () => request.abort();
    const finish = (result: RequestResult<T>) => {
      if (settled) return;
      settled = true;
      signal?.removeEventListener('abort', abort);
      resolve(result);
    };
    request.upload.onprogress = (event) => {
      const now = performance.now();
      const total = event.lengthComputable ? event.total : file.size;
      const seconds = (now - previousAt) / 1000;
      onProgress?.({
        loaded: event.loaded,
        total,
        percent: total > 0 ? Math.min(100, (event.loaded / total) * 100) : 0,
        bytesPerSecond:
          seconds > 0
            ? Math.max(0, (event.loaded - previousBytes) / seconds)
            : 0,
      });
      previousAt = now;
      previousBytes = event.loaded;
    };
    request.onload = () => {
      if (request.status === 401) expireSession(expectedSession);
      try {
        const envelope = JSON.parse(request.responseText) as ApiResult<T>;
        if (!envelope || typeof envelope.success !== 'boolean')
          throw new TypeError('Invalid API response envelope');
        if (
          request.status < 200 ||
          request.status >= 300 ||
          !envelope.success
        ) {
          const result = requestFailure(
            envelope.errorCode || `HTTP_${request.status}`,
            envelope.errorMessage || `HTTP ${request.status}`,
            request.status,
            envelope.success === false ? 'business' : 'http',
            true,
          );
          result.traceId = request.getResponseHeader('X-Trace-Id') || undefined;
          finish(result);
        } else
          finish({
            success: true,
            data: envelope.data,
            status: request.status,
          });
      } catch (error) {
        unexpectedError?.(error);
        finish(
          requestFailure(
            'INVALID_RESPONSE',
            '服务器响应格式无效',
            request.status,
            'invalid-response',
            true,
          ),
        );
      }
    };
    request.onerror = request.ontimeout = () => {
      unexpectedError?.(new TypeError('Binary upload connection failed'));
      finish(
        requestFailure(
          'NETWORK_ERROR',
          '网络请求失败，请检查连接',
          0,
          'network',
          dispatched,
        ),
      );
    };
    request.onabort = () =>
      finish(
        requestFailure('CANCELED', '请求已取消', 0, 'canceled', dispatched),
      );
    try {
      request.open('POST', url, true);
      request.withCredentials = true;
      request.setRequestHeader('Content-Type', 'application/octet-stream');
      request.setRequestHeader('X-CSRF-TOKEN', token.data);
      signal?.addEventListener('abort', abort, { once: true });
      if (signal?.aborted) {
        finish(requestFailure('CANCELED', '请求已取消', 0, 'canceled'));
        return;
      }
      dispatched = true;
      request.send(file);
    } catch (error) {
      unexpectedError?.(error);
      finish(
        requestFailure(
          'NETWORK_ERROR',
          '网络请求失败，请检查连接',
          0,
          'network',
          dispatched,
        ),
      );
    }
  });
}
