import { useIntl } from '@umijs/max';
import enUS from '@/locales/en-US/oob';

/** 新页面统一走国际化，其他模板语言未翻译时使用完整英文回退。 */
export function useOobIntl() {
  const intl = useIntl();
  return (key: keyof typeof enUS, values?: Record<string, string | number>) =>
    intl.formatMessage({ id: key, defaultMessage: enUS[key] }, values);
}
