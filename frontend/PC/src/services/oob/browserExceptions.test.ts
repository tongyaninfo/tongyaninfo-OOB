import { beforeEach, describe, expect, it, vi } from 'vitest';

const report = vi.hoisted(() => vi.fn());
vi.mock('./api', () => ({ reportClientException: report }));

import {
  captureRuntimeException,
  installBrowserExceptionCapture,
  safeStackFrames,
  setExceptionCaptureIdentity,
} from './browserExceptions';
import { apiRequest } from './http';

describe('浏览器异常通道的脱敏与去递归', () => {
  beforeEach(() => {
    setExceptionCaptureIdentity(undefined);
    setExceptionCaptureIdentity('fixture-user');
    report
      .mockReset()
      .mockResolvedValue({ success: true, data: true, status: 200 });
  });
  it('真实error只上报类型与代码帧，不包含消息、查询串和锚点', async () => {
    const error = new TypeError('fixture-password must not leave this page');
    error.stack =
      'TypeError: fixture-password must not leave this page\n    at load (https://example.test/assets/app.js?token=fixture-secret#anchor:10:2)';
    await captureRuntimeException(error);
    expect(report).toHaveBeenCalledTimes(1);
    const payload = report.mock.calls[0][0];
    expect(payload.eventType).toBe('error');
    expect(payload.exceptionType).toBe('TypeError');
    expect(JSON.stringify(payload)).not.toMatch(
      /fixture-password|fixture-secret|token=|anchor/,
    );
    await captureRuntimeException(error);
    expect(report).toHaveBeenCalledTimes(1);
  });
  it('真实unhandledrejection上报自身失败不重试、不递归', async () => {
    report.mockRejectedValueOnce(new TypeError('reporting failed'));
    const error = new RangeError('not sent');
    error.stack =
      'RangeError: not sent\n    at second (https://example.test/rejection.js:20:1)';
    await expect(
      captureRuntimeException(error, 'unhandledrejection'),
    ).resolves.toBeUndefined();
    expect(report).toHaveBeenCalledTimes(1);
    expect(report.mock.calls[0][0].eventType).toBe('unhandledrejection');
  });
  it('监听可卸载，主动取消与业务结果不产生系统异常', async () => {
    const remove = installBrowserExceptionCapture('fixture-user');
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({
            success: false,
            data: null,
            errorCode: 'VALIDATION_FAILED',
            errorMessage: 'fixture',
          }),
          { status: 400 },
        ),
      ),
    );
    expect(await apiRequest('/api/test')).toMatchObject({
      success: false,
      errorCode: 'VALIDATION_FAILED',
    });
    await captureRuntimeException(new DOMException('aborted', 'AbortError'));
    expect(report).not.toHaveBeenCalled();
    remove();
    window.dispatchEvent(
      new ErrorEvent('error', { error: new SyntaxError('after cleanup') }),
    );
    expect(report).not.toHaveBeenCalled();
  });
  it('没有可识别代码帧时不回传原始文本', () => {
    expect(safeStackFrames('secret message\nunknown payload')).toBeUndefined();
  });
});

it('堆栈只输出文件基名行列号，at开头的敏感尾文本也不能原样发送', () => {
  const stack =
    'Error: private\n at password=fixture-secret\n at token=fixture-token (https://example.test/private/assets/app.tsx:123:45) password=fixture-secret\n at render (/Users/private/src/view.js:10:2) trailing-secret';
  expect(safeStackFrames(stack)).toBe('at app.tsx:123:45\nat view.js:10:2');
  expect(safeStackFrames(' at password=fixture-secret')).toBeUndefined();
});

it('切换身份取消旧在途请求并丢弃旧队列，新身份去重缓存独立', async () => {
  setExceptionCaptureIdentity(undefined);
  setExceptionCaptureIdentity('old-user');
  report.mockReset();
  let release!: () => void;
  report
    .mockImplementationOnce(
      (_body, signal: AbortSignal) =>
        new Promise((resolve) => {
          release = () => resolve({ success: false });
          signal.addEventListener('abort', release);
        }),
    )
    .mockResolvedValue({ success: true });
  const first = new TypeError('old-first');
  first.stack = 'at oldFirst (https://example.test/first.js:1:1)';
  const second = new RangeError('old-second');
  second.stack = 'at oldSecond (https://example.test/second.js:2:2)';
  const pendingFirst = captureRuntimeException(first);
  await Promise.resolve();
  const signal = report.mock.calls[0][1] as AbortSignal;
  const pendingSecond = captureRuntimeException(second);
  setExceptionCaptureIdentity('new-user');
  expect(signal.aborted).toBe(true);
  await Promise.all([pendingFirst, pendingSecond]);
  expect(report).toHaveBeenCalledTimes(1);
  await captureRuntimeException(second);
  expect(report).toHaveBeenCalledTimes(2);
  setExceptionCaptureIdentity(undefined);
  await captureRuntimeException(new SyntaxError('logged out'));
  expect(report).toHaveBeenCalledTimes(2);
});
