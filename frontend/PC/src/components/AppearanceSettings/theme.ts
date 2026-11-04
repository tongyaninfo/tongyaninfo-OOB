import type { Settings } from '@ant-design/pro-components';

export type ThemeMode = 'auto' | 'light' | 'realDark';
export type ResolvedTheme = Exclude<ThemeMode, 'auto'>;
const darkQuery = '(prefers-color-scheme: dark)';

/** 账号保存选择意图，布局和图表始终使用解析后的深浅色。 */
export function normalizeThemeMode(value: unknown): ThemeMode {
  return value === 'auto' || value === 'realDark' ? value : 'light';
}

export function resolveTheme(mode: ThemeMode): ResolvedTheme {
  if (mode !== 'auto') return mode;
  return typeof window !== 'undefined' &&
    typeof window.matchMedia === 'function' &&
    window.matchMedia(darkQuery).matches
    ? 'realDark'
    : 'light';
}

/** 系统变化只更新显示；兼容旧版 Safari，卸载时释放监听。 */
export function subscribeSystemTheme(listener: (theme: ResolvedTheme) => void) {
  if (
    typeof window === 'undefined' ||
    typeof window.matchMedia !== 'function'
  ) {
    listener('light');
    return () => undefined;
  }
  const query = window.matchMedia(darkQuery);
  const notify = (event: MediaQueryListEvent) =>
    listener(event.matches ? 'realDark' : 'light');
  listener(query.matches ? 'realDark' : 'light');
  if (typeof query.addEventListener === 'function') {
    query.addEventListener('change', notify);
    return () => query.removeEventListener('change', notify);
  }
  query.addListener(notify);
  return () => query.removeListener(notify);
}

export function preferenceSettings(
  settings: Partial<Settings>,
  mode: ThemeMode,
) {
  return { ...settings, navTheme: mode };
}
