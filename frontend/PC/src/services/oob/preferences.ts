import { getPreferences, savePreferences } from './api';

/** 仅持久化后端已确认的外观字段，禁止把菜单、权限、账号或认证信息塞入设置。 */
const layoutKeys = [
  'navTheme',
  'colorPrimary',
  'layout',
  'contentWidth',
  'fixedHeader',
  'fixSiderbar',
  'colorWeak',
  'splitMenus',
] as const;

/** 只比较可持久化外观，模板回调增删标题等展示字段不构成账号设置修改。 */
export function hasLayoutChanges(
  previous: Record<string, unknown> | undefined,
  next: Record<string, unknown>,
) {
  return layoutKeys.some((key) => !Object.is(previous?.[key], next[key]));
}

/** 表格缺省配置等价于中等尺寸和全部列显示，恢复/受控同步不创建保存动作。 */
export function hasTablePreferenceChanges(
  previous: Record<string, unknown> | undefined,
  next: Record<string, unknown>,
) {
  const serialize = (value: unknown) =>
    JSON.stringify(value, (_key, item) =>
      item && typeof item === 'object' && !Array.isArray(item)
        ? Object.fromEntries(
            Object.entries(item).sort(([left], [right]) =>
              left.localeCompare(right),
            ),
          )
        : item,
    );
  return (
    (previous?.size || 'middle') !== (next.size || 'middle') ||
    serialize(previous?.columnsState || {}) !==
      serialize(next.columnsState || {})
  );
}
let restored = false;
let generation = 0;
let current: Record<string, unknown> = {};
let saveTimer: ReturnType<typeof setTimeout> | undefined;
let saving = Promise.resolve();

/** 退出和身份变化取消待保存项，旧账户回调不得覆盖新账户设置。 */
export function clearPreferences() {
  generation++;
  restored = false;
  current = {};
  if (saveTimer) clearTimeout(saveTimer);
}

export async function restorePreferences() {
  clearPreferences();
  const expected = generation;
  const values = await getPreferences();
  if (expected === generation) {
    if (values.success) {
      current = values.data;
      restored = true;
    }
  }
  return values;
}

/** 防抖保存与串行请求兼顾拖动主题设置和表格设置，不以默认值覆盖未知账户数据。 */
export function updatePreference(
  key: 'layoutSettings' | 'tables',
  value: unknown,
  onError?: (error: unknown) => void,
) {
  if (!restored) return;
  current = { ...current, [key]: value };
  if (saveTimer) clearTimeout(saveTimer);
  const expected = generation;
  saveTimer = setTimeout(() => {
    const values = { ...current };
    saving = saving
      .catch(() => undefined)
      .then(async () => {
        if (expected !== generation) return;
        const result = await savePreferences(values);
        if (!result.success) onError?.(result);
      })
      .catch((error: unknown) => {
        onError?.(error);
      });
  }, 500);
}

/** 表格设置按稳定表编号合并，只在成功恢复后保存当前账号的尺寸和列配置。 */
export function updateTablePreference(
  id: string,
  values: Record<string, unknown>,
  onError?: (error: unknown) => void,
) {
  if (!hasTablePreferenceChanges(getTablePreference(id), values)) return;
  const tables =
    typeof current.tables === 'object' && current.tables
      ? (current.tables as Record<string, unknown>)
      : {};
  updatePreference('tables', { ...tables, [id]: values }, onError);
}

export function getTablePreference(
  id: string,
): Record<string, unknown> | undefined {
  const tables = current.tables as
    | Record<string, Record<string, unknown>>
    | undefined;
  return tables?.[id];
}

export function persistLayout(
  settings: Record<string, unknown>,
  onError?: (error: unknown) => void,
) {
  const allowed = Object.fromEntries(
    layoutKeys
      .filter((key) => settings[key] !== undefined)
      .map((key) => [key, settings[key]]),
  );
  updatePreference('layoutSettings', allowed, onError);
}
