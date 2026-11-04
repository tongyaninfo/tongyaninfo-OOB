import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  resetCsrfToken,
  setUnexpectedErrorHandler,
  uploadBinary,
} from './http';

class UploadRequest {
  static current: UploadRequest | undefined;
  upload: {
    onprogress?: (event: {
      loaded: number;
      total: number;
      lengthComputable: boolean;
    }) => void;
  } = {};
  withCredentials = false;
  status = 200;
  responseText = JSON.stringify({
    success: true,
    data: { id: '9007199254740993' },
  });
  onload?: () => void;
  onerror?: () => void;
  ontimeout?: () => void;
  onabort?: () => void;
  open = vi.fn();
  send = vi.fn();
  setRequestHeader = vi.fn();
  getResponseHeader = vi.fn(() => null);
  constructor() {
    UploadRequest.current = this;
  }
  abort() {
    this.onabort?.();
  }
}
const unexpected = vi.fn();
beforeEach(() => {
  resetCsrfToken();
  UploadRequest.current = undefined;
  unexpected.mockReset();
  setUnexpectedErrorHandler(unexpected);
  vi.stubGlobal('XMLHttpRequest', UploadRequest);
  vi.stubGlobal(
    'fetch',
    vi
      .fn()
      .mockResolvedValue(
        new Response(
          JSON.stringify({ success: true, data: { token: 'fixture-csrf' } }),
          { status: 200 },
        ),
      ),
  );
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('镜像上传的实际进度与未知结果', () => {
  it('原样流式发送文件并共享防伪，速率来自上传事件，完成后保留长ID字符串', async () => {
    vi.spyOn(performance, 'now')
      .mockReturnValueOnce(1000)
      .mockReturnValue(2000);
    const progress = vi.fn();
    const file = new File(['12345678'], 'boot.iso');
    const promise = uploadBinary<{ id: string }>(
      '/api/oob/media-images?fileName=boot.iso',
      file,
      progress,
    );
    await vi.waitFor(() =>
      expect(UploadRequest.current?.send).toHaveBeenCalledWith(file),
    );
    const request = UploadRequest.current as UploadRequest;
    expect(request.withCredentials).toBe(true);
    expect(request.setRequestHeader).toHaveBeenCalledWith(
      'X-CSRF-TOKEN',
      'fixture-csrf',
    );
    expect(request.setRequestHeader).toHaveBeenCalledWith(
      'Content-Type',
      'application/octet-stream',
    );
    request.upload.onprogress?.({
      loaded: 4,
      total: 8,
      lengthComputable: true,
    });
    expect(progress).toHaveBeenCalledWith({
      loaded: 4,
      total: 8,
      percent: 50,
      bytesPerSecond: 4,
    });
    request.onload?.();
    expect(await promise).toEqual({
      success: true,
      status: 200,
      data: { id: '9007199254740993' },
    });
  });
  it('上传中断保留可能已发送状态，网络错误不自动重传大文件', async () => {
    const promise = uploadBinary(
      '/api/oob/media-images?fileName=boot.iso',
      new File(['fixture'], 'boot.iso'),
    );
    await vi.waitFor(() =>
      expect(UploadRequest.current?.send).toHaveBeenCalledOnce(),
    );
    UploadRequest.current?.onerror?.();
    expect(await promise).toMatchObject({
      success: false,
      kind: 'network',
      dispatched: true,
    });
    expect(UploadRequest.current?.send).toHaveBeenCalledOnce();
    expect(unexpected).toHaveBeenCalledOnce();
  });
  it('用户取消上传触发abort，业务拒绝不会记录系统异常', async () => {
    const abort = new AbortController();
    const promise = uploadBinary(
      '/api/oob/media-images?fileName=boot.iso',
      new File(['fixture'], 'boot.iso'),
      undefined,
      abort.signal,
    );
    await vi.waitFor(() =>
      expect(UploadRequest.current?.send).toHaveBeenCalledOnce(),
    );
    abort.abort();
    expect(await promise).toMatchObject({
      success: false,
      kind: 'canceled',
      dispatched: true,
    });
    expect(unexpected).not.toHaveBeenCalled();
    const denied = uploadBinary(
      '/api/oob/media-images?fileName=boot.iso',
      new File(['fixture'], 'boot.iso'),
    );
    await vi.waitFor(() =>
      expect(UploadRequest.current?.send).toHaveBeenCalledOnce(),
    );
    const request = UploadRequest.current as UploadRequest;
    request.status = 403;
    request.responseText = JSON.stringify({
      success: false,
      errorCode: 'FORBIDDEN',
      errorMessage: 'Denied',
      data: null,
    });
    request.onload?.();
    expect(await denied).toMatchObject({
      success: false,
      kind: 'business',
      status: 403,
      errorCode: 'FORBIDDEN',
    });
    expect(unexpected).not.toHaveBeenCalled();
  });
});
