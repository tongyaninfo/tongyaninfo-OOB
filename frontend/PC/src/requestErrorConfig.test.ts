import { message, notification } from 'antd';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { captureRuntimeException } from '@/services/oob/browserExceptions';
import { errorConfig } from './requestErrorConfig';

vi.mock('@/services/oob/browserExceptions', () => ({
  captureRuntimeException: vi.fn(),
}));

vi.mock('antd', () => ({
  message: {
    warning: vi.fn(),
    error: vi.fn(),
  },
  notification: {
    open: vi.fn(),
  },
}));

vi.mock('@umijs/max', () => ({
  getIntl: vi.fn(() => ({
    formatMessage: vi.fn(({ defaultMessage }) => defaultMessage),
  })),
}));

describe('requestErrorConfig', () => {
  // biome-ignore lint/style/noNonNullAssertion: config handlers are always defined
  const errorThrower = errorConfig.errorConfig!.errorThrower!;
  // biome-ignore lint/style/noNonNullAssertion: config handlers are always defined
  const errorHandler = errorConfig.errorConfig!.errorHandler!;

  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('预期业务失败结果', () => {
    it('业务失败保留响应信封，不构造或抛出异常', () => {
      const response = {
        success: false,
        data: null,
        errorCode: 'VALIDATION_FAILED',
        errorMessage: 'Bad Request',
        showType: 2,
      };
      expect(() => errorThrower(response)).not.toThrow();
      expect(response).toMatchObject({
        success: false,
        errorCode: 'VALIDATION_FAILED',
      });
      expect(message.error).toHaveBeenCalledWith('Bad Request');
    });
    it('成功响应不显示失败消息', () => {
      expect(() =>
        errorThrower({ success: true, data: { id: 1 } }),
      ).not.toThrow();
      expect(message.error).not.toHaveBeenCalled();
    });
    it('业务通知直接显示，不通过BizError控制流程', () => {
      expect(() =>
        errorThrower({
          success: false,
          errorCode: 403,
          errorMessage: 'Forbidden',
          showType: 3,
        }),
      ).not.toThrow();
      expect(notification.open).toHaveBeenCalledWith({
        title: 403,
        description: 'Forbidden',
      });
    });
  });

  describe('errorHandler', () => {
    it('skipErrorHandler不再从错误处理器抛出控制流异常', () => {
      const error = new Error('Test error');
      const opts = { skipErrorHandler: true };

      expect(() => {
        errorHandler(error, opts);
      }).not.toThrow();
    });

    it('should handle SILENT showType', () => {
      const error: any = new Error('Silent error');
      error.name = 'BizError';
      error.info = {
        errorCode: 1001,
        errorMessage: 'Silent error',
        showType: 0,
      };

      errorHandler(error, {});

      expect(message.warning).not.toHaveBeenCalled();
      expect(message.error).not.toHaveBeenCalled();
      expect(notification.open).not.toHaveBeenCalled();
    });

    it('should handle WARN_MESSAGE showType', () => {
      const error: any = new Error('Warning');
      error.name = 'BizError';
      error.info = {
        errorCode: 1002,
        errorMessage: 'This is a warning',
        showType: 1,
      };

      errorHandler(error, {});

      expect(message.warning).toHaveBeenCalledWith('This is a warning');
    });

    it('should handle ERROR_MESSAGE showType', () => {
      const error: any = new Error('Error message');
      error.name = 'BizError';
      error.info = {
        errorCode: 1003,
        errorMessage: 'This is an error',
        showType: 2,
      };

      errorHandler(error, {});

      expect(message.error).toHaveBeenCalledWith('This is an error');
    });

    it('should handle NOTIFICATION showType', () => {
      const error: any = new Error('Notification');
      error.name = 'BizError';
      error.info = {
        errorCode: 1004,
        errorMessage: 'This is a notification',
        showType: 3,
      };

      errorHandler(error, {});

      expect(notification.open).toHaveBeenCalledWith({
        title: 1004,
        description: 'This is a notification',
      });
    });

    it('should handle REDIRECT showType', () => {
      const error: any = new Error('Redirect');
      error.name = 'BizError';
      error.info = {
        errorCode: 401,
        errorMessage: 'Unauthorized',
        showType: 9,
      };

      errorHandler(error, {});

      // REDIRECT 分支不应触发任何消息/通知提示
      expect(message.warning).not.toHaveBeenCalled();
      expect(message.error).not.toHaveBeenCalled();
      expect(notification.open).not.toHaveBeenCalled();
    });

    it('should handle default case for unknown showType', () => {
      const error: any = new Error('Unknown type');
      error.name = 'BizError';
      error.info = {
        errorCode: 1005,
        errorMessage: 'Unknown error type',
        showType: 99,
      };

      errorHandler(error, {});

      expect(message.error).toHaveBeenCalledWith('Unknown error type');
    });

    it('should handle axios response error', () => {
      const error: any = new Error('Axios error');
      error.response = {
        status: 500,
        data: {},
      };

      errorHandler(error, {});

      expect(message.error).toHaveBeenCalledWith('Response status:500');
    });

    it('should handle offline error', () => {
      const error: any = new Error('Network error');
      error.request = {};

      const originalOnLine = navigator.onLine;
      Object.defineProperty(navigator, 'onLine', {
        writable: true,
        value: false,
      });

      try {
        errorHandler(error, {});

        expect(message.error).toHaveBeenCalledWith(
          'Network unavailable. Please check your connection and try again.',
        );
      } finally {
        Object.defineProperty(navigator, 'onLine', {
          writable: true,
          value: originalOnLine,
        });
      }
    });

    it('should handle request error with no response', () => {
      const error: any = new Error('Request error');
      error.request = {};

      errorHandler(error, {});

      expect(message.error).toHaveBeenCalledWith(
        'None response! Please retry.',
      );
    });

    it('should handle generic error', () => {
      const error: any = new Error('Generic error');

      errorHandler(error, {});

      expect(message.error).toHaveBeenCalledWith(
        'Request error, please retry.',
      );
    });
  });

  describe('requestInterceptors', () => {
    // The interceptor is registered as a plain function (not a tuple),
    // so narrow the union type to a callable for the test.
    const interceptor = errorConfig.requestInterceptors?.[0] as (config: {
      url?: string;
      method?: string;
    }) => { url?: string };

    it('should pass through config without modification', () => {
      const config = {
        url: 'https://api.example.com/users',
        method: 'GET',
      };

      const result = interceptor(config);

      // Token attachment is intentionally commented out in the source;
      // interceptor currently returns config as-is
      expect(result.url).toBe('https://api.example.com/users');
    });

    it('should handle URL without config', () => {
      const config = {};

      const result = interceptor(config);

      expect(result.url).toBeUndefined();
    });
  });
});

it('HTTP400/403业务信封显示原失败，取消与业务错误均不生成ClientException', () => {
  vi.clearAllMocks();
  const handler = errorConfig.errorConfig?.errorHandler as (
    error: unknown,
    options: unknown,
  ) => void;
  for (const status of [400, 403])
    handler?.(
      {
        name: 'AxiosError',
        response: {
          status,
          data: {
            success: false,
            data: null,
            errorCode: 'FORBIDDEN',
            errorMessage: 'fixture rejected',
          },
        },
      },
      {},
    );
  handler?.({ name: 'CanceledError', code: 'ERR_CANCELED' }, {});
  handler?.({ name: 'AbortError' }, {});
  expect(message.error).toHaveBeenCalledWith('fixture rejected');
  expect(captureRuntimeException).not.toHaveBeenCalled();
});
