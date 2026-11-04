import { getLocale, setLocale } from '@umijs/max';

/** 仅约束当前产品入口，保留模板其他语言资源供后续扩展。 */
export const supportedLocales = ['zh-CN', 'zh-TW', 'en-US'] as const;
export function isSupportedLocale(locale: string) {
  return supportedLocales.includes(locale as (typeof supportedLocales)[number]);
}
export function filterSupportedLocales<T extends { lang: string }>(
  locales: T[],
): T[] {
  return locales.filter((locale) => isSupportedLocale(locale.lang));
}

/** 旧模板的语言缓存可能已下线；统一回退，不能让登录页和主框架各用一种语言。 */
export function restoreSupportedLocale() {
  if (!isSupportedLocale(getLocale())) setLocale('zh-CN', false);
}
