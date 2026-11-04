import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ExceptionLogDto } from '@/services/oob/models';

const list = vi.hoisted(() => vi.fn());
const detail = vi.hoisted(() => vi.fn());
vi.mock('@/services/oob/api', () => ({
  getExceptions: list,
  getException: detail,
}));
vi.mock('@/services/oob/browserExceptions', () => ({
  captureRuntimeException: vi.fn(),
}));
vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));
vi.mock('@ant-design/pro-components', () => ({
  PageContainer: ({ children }: { children: React.ReactNode }) => (
    <div>{children}</div>
  ),
}));

import Exceptions from './index';

const fixture: ExceptionLogDto = {
  id: '9007199254740993',
  occurredAt: '2026-10-25T00:00:00Z',
  kind: 'SystemException',
  level: 'Error',
  source: 'Http',
  operation: 'fixture',
  errorCode: 'UNEXPECTED_ERROR',
  message: 'Safe system failure summary',
  exceptionType: 'InvalidOperationException',
  stackTrace: '<script>untrusted log is text</script>',
  traceId: 'fixture-trace',
  serverId: null,
  userId: null,
  requestPath: '/api/test',
  requestMethod: 'GET',
  httpStatusCode: 500,
};
afterEach(() => {
  vi.unstubAllGlobals();
});
describe('异常信息查询、详情和安全复制', () => {
  it('长ID详情使用正式接口，日志作为纯文本展示，只复制脱敏详情对象', async () => {
    list.mockResolvedValue({
      success: true,
      data: {
        items: [{ ...fixture, stackTrace: null }],
        total: 1,
        current: 1,
        pageSize: 20,
      },
      status: 200,
    });
    detail.mockResolvedValue({ success: true, data: fixture, status: 200 });
    const copy = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal(
      'navigator',
      Object.create(navigator, { clipboard: { value: { writeText: copy } } }),
    );
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    render(
      <App>
        <QueryClientProvider client={client}>
          <Exceptions />
        </QueryClientProvider>
      </App>,
    );
    fireEvent.click(await screen.findByRole('button', { name: 'oob.details' }));
    await screen.findByText(fixture.stackTrace || '');
    expect(detail).toHaveBeenCalledWith(
      '9007199254740993',
      expect.any(AbortSignal),
    );
    expect(document.querySelector('script')).toBeNull();
    fireEvent.click(
      screen.getByRole('button', { name: 'oob.exceptions.copy' }),
    );
    await waitFor(() => expect(copy).toHaveBeenCalledTimes(1));
    expect(JSON.parse(copy.mock.calls[0][0])).toEqual(fixture);
    client.clear();
  });
  it('业务权限拒绝在页面显示失败，不能当成空成功列表', async () => {
    list.mockResolvedValue({
      success: false,
      data: null,
      errorCode: 'FORBIDDEN',
      errorMessage: 'fixture access denied',
      status: 403,
      kind: 'business',
      dispatched: true,
    });
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    render(
      <App>
        <QueryClientProvider client={client}>
          <Exceptions />
        </QueryClientProvider>
      </App>,
    );
    expect(
      await screen.findByText('fixture access denied'),
    ).toBeInTheDocument();
    client.clear();
  });
});
