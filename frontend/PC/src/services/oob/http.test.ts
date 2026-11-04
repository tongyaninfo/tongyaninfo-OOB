import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiRequest, resetCsrfToken, setUnexpectedErrorHandler } from './http';

const fetchMock = vi.fn();
const unexpected = vi.fn();
const response = (data: unknown, status = 200) =>
  new Response(JSON.stringify(data), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
describe('业务结果、实际异常与防伪边界', () => {
  beforeEach(() => {
    resetCsrfToken();
    fetchMock.mockReset();
    unexpected.mockReset();
    setUnexpectedErrorHandler(unexpected);
    vi.stubGlobal('fetch', fetchMock);
  });
  it('查询只读接口并保留成功信封，不获取令牌或发送写请求', async () => {
    fetchMock.mockResolvedValueOnce(
      response({ success: true, data: { count: 0 } }),
    );
    expect(await apiRequest('/api/oob/dashboard')).toEqual({
      success: true,
      data: { count: 0 },
      status: 200,
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
  it('写请求获取同源令牌，长ID始终保留字符串', async () => {
    fetchMock
      .mockResolvedValueOnce(
        response({ success: true, data: { token: 'fixture-csrf' } }),
      )
      .mockResolvedValueOnce(
        response({ success: true, data: { id: '9007199254740993' } }),
      );
    const result = await apiRequest<{ id: string }>('/api/oob/servers', {
      method: 'POST',
      body: '{}',
    });
    expect(result.success && result.data.id).toBe('9007199254740993');
    expect(fetchMock.mock.calls[1][1].headers.get('X-CSRF-TOKEN')).toBe(
      'fixture-csrf',
    );
    expect(fetchMock.mock.calls[1][1].credentials).toBe('same-origin');
  });
  it('HTTP业务拒绝返回结果，不抛异常、不上报系统异常', async () => {
    fetchMock.mockResolvedValueOnce(
      response(
        {
          success: false,
          data: null,
          errorCode: 'FORBIDDEN',
          errorMessage: 'denied',
        },
        403,
      ),
    );
    await expect(apiRequest('/api/oob/dashboard')).resolves.toMatchObject({
      success: false,
      errorCode: 'FORBIDDEN',
      errorMessage: 'denied',
      status: 403,
      kind: 'business',
    });
    expect(unexpected).not.toHaveBeenCalled();
  });
  it('镜像原始二进制上传保留内容类型并仍执行同源防伪校验', async () => {
    fetchMock
      .mockResolvedValueOnce(
        response({ success: true, data: { token: 'fixture-csrf' } }),
      )
      .mockResolvedValueOnce(
        response({ success: true, data: { id: '9007199254740993' } }),
      );
    const file = new File(['fixture'], 'boot.iso');
    await apiRequest('/api/oob/media-images?fileName=boot.iso', {
      method: 'POST',
      body: file,
      headers: { 'Content-Type': 'application/octet-stream' },
    });
    const options = fetchMock.mock.calls[1][1];
    expect(options.body).toBe(file);
    expect(options.headers.get('Content-Type')).toBe(
      'application/octet-stream',
    );
    expect(options.headers.get('X-CSRF-TOKEN')).toBe('fixture-csrf');
    expect(options.credentials).toBe('same-origin');
  });
  it('发送后网络异常保留可能已发送状态，捕获异常但不重发电源动作', async () => {
    const error = new TypeError('connection lost');
    fetchMock
      .mockResolvedValueOnce(
        response({ success: true, data: { token: 'fixture' } }),
      )
      .mockRejectedValueOnce(error);
    expect(
      await apiRequest('/api/oob/servers/123/power', {
        method: 'POST',
        body: '{}',
      }),
    ).toMatchObject({
      success: false,
      errorCode: 'NETWORK_ERROR',
      dispatched: true,
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(unexpected).toHaveBeenCalledWith(error);
  });
  it('防伪初始化失败时明确原业务动作未发送', async () => {
    fetchMock.mockResolvedValueOnce(
      response({ success: false, errorCode: 'UNAUTHORIZED', data: null }, 401),
    );
    expect(
      await apiRequest('/api/oob/servers/123/power', { method: 'POST' }),
    ).toMatchObject({
      success: false,
      dispatched: false,
      errorCode: 'UNAUTHORIZED',
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(unexpected).not.toHaveBeenCalled();
  });
  it('主动取消不发送请求且不生成异常', async () => {
    const controller = new AbortController();
    controller.abort();
    expect(
      await apiRequest('/api/oob/dashboard', { signal: controller.signal }),
    ).toMatchObject({
      success: false,
      errorCode: 'CANCELED',
      dispatched: false,
    });
    expect(fetchMock).not.toHaveBeenCalled();
    expect(unexpected).not.toHaveBeenCalled();
  });
  it('异常上报通道自身失败不递归上报', async () => {
    fetchMock
      .mockResolvedValueOnce(
        response({ success: true, data: { token: 'fixture' } }),
      )
      .mockRejectedValueOnce(new TypeError('failed'));
    expect(
      await apiRequest('/api/oob/exceptions/client', {
        method: 'POST',
        body: '{}',
      }),
    ).toMatchObject({ success: false });
    expect(unexpected).not.toHaveBeenCalled();
  });
});
