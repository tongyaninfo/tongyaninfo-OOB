import type { RequestOptions } from '@@/plugin-request/request';
import type { RequestConfig } from '@umijs/max';
import { getIntl } from '@umijs/max';
import { message, notification } from 'antd';
import { captureRuntimeException } from '@/services/oob/browserExceptions';

// 错误处理方案： 错误类型
enum ErrorShowType {
  SILENT = 0,
  WARN_MESSAGE = 1,
  ERROR_MESSAGE = 2,
  NOTIFICATION = 3,
  REDIRECT = 9,
}
// 与后端约定的响应数据格式
interface ResponseStructure {
  success: boolean;
  data: unknown;
  errorCode?: number | string;
  errorMessage?: string;
  showType?: ErrorShowType;
}

/** 所有已识别的业务信封直接显示，不能转换成前端系统异常。 */
function showBusinessFailure(result: ResponseStructure) {
  if (result.showType === ErrorShowType.SILENT) return;
  if (result.showType === ErrorShowType.WARN_MESSAGE)
    message.warning(result.errorMessage);
  else if (result.showType === ErrorShowType.NOTIFICATION)
    notification.open({
      title: result.errorCode,
      description: result.errorMessage,
    });
  else if (result.showType === ErrorShowType.REDIRECT)
    window.location.href = '/user/login';
  else message.error(result.errorMessage);
}

/**
 * @name 错误处理
 * pro 自带的错误处理， 可以在这里做自己的改动
 * @doc https://umijs.org/docs/max/request#配置
 */
export const errorConfig: RequestConfig = {
  // 错误处理： umi@3 的错误处理方案。
  errorConfig: {
    // 预期业务失败保留响应信封，不构造或抛出 BizError。模板调用方可直接判断 success。
    errorThrower: (res) => {
      const result = res as unknown as ResponseStructure;
      if (result.success === false) showBusinessFailure(result);
    },
    // 错误接收及处理
    errorHandler: (error: any, opts: any) => {
      // Axios 的 HTTP拒绝仍可能携带正式业务信封，先分类，再决定是否上报。
      if (
        ['AbortError', 'CanceledError'].includes(error?.name) ||
        error?.code === 'ERR_CANCELED' ||
        error?.__CANCEL__ === true
      )
        return;
      const business = error?.response?.data as ResponseStructure | undefined;
      if (business?.success === false) {
        if (!opts?.skipErrorHandler) showBusinessFailure(business);
        return;
      }
      if (error?.name !== 'BizError') void captureRuntimeException(error);
      if (opts?.skipErrorHandler) return;
      // 兼容模板外部旧调用方的既有BizError，不再由本系统构造。
      if (error.name === 'BizError') {
        const errorInfo: ResponseStructure | undefined = error.info;
        if (errorInfo) {
          const { errorMessage, errorCode } = errorInfo;
          switch (errorInfo.showType) {
            case ErrorShowType.SILENT:
              // do nothing
              break;
            case ErrorShowType.WARN_MESSAGE:
              message.warning(errorMessage);
              break;
            case ErrorShowType.ERROR_MESSAGE:
              message.error(errorMessage);
              break;
            case ErrorShowType.NOTIFICATION:
              notification.open({
                title: errorCode,
                description: errorMessage,
              });
              break;
            case ErrorShowType.REDIRECT:
              window.location.href = '/user/login';
              break;
            default:
              message.error(errorMessage);
          }
        }
      } else if (error.response) {
        // Axios 的错误
        // 请求成功发出且服务器也响应了状态码，但状态代码超出了 2xx 的范围
        message.error(`Response status:${error.response.status}`);
      } else if (typeof navigator !== 'undefined' && !navigator.onLine) {
        message.error(
          getIntl().formatMessage({
            id: 'app.request.offline',
            defaultMessage:
              'Network unavailable. Please check your connection and try again.',
          }),
        );
      } else if (error.request) {
        message.error('None response! Please retry.');
      } else {
        message.error('Request error, please retry.');
      }
    },
  },

  // 请求拦截器
  requestInterceptors: [
    (config: RequestOptions) => {
      // 拦截请求配置，进行个性化处理。
      // 示例：为请求附加 token（按需启用）
      // const token = localStorage.getItem('token');
      // if (token) {
      //   config.headers = { ...config.headers, Authorization: `Bearer ${token}` };
      // }
      return config;
    },
  ],

  // 响应拦截器
  responseInterceptors: [],
};
