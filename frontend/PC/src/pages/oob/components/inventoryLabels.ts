import enUS from '@/locales/en-US/hardware';
import zhCN from '@/locales/zh-CN/hardware';
import zhTW from '@/locales/zh-TW/hardware';

/** 设备原key映射到稳定资源ID；三语言资源按应用标准目录维护，未知字段保留原文。 */
function label(
  category: 'attribute' | 'kind' | 'status',
  key: string,
  locale: string,
) {
  const messages: Record<string, string> =
    locale === 'zh-CN' ? zhCN : locale === 'zh-TW' ? zhTW : enUS;
  return messages[`oob.hardware.${category}.${key}`] || key;
}
export const inventoryAttributeLabel = (key: string, locale: string) =>
  label('attribute', key, locale);
export const inventoryKindLabel = (kind: string, locale: string) =>
  label('kind', kind, locale);
export const inventoryStatusLabel = (status: string, locale: string) =>
  label('status', status, locale);
/** 数组计数属于协议元数据，展示时隐藏；未知业务字段及原值完整保留。 */
export const visibleInventoryAttributes = (
  values: Record<string, string | null>,
) => Object.entries(values).filter(([key]) => !key.includes('@odata.count'));
export const inventoryAttributeKeys = Object.keys(enUS)
  .filter((key) => key.startsWith('oob.hardware.attribute.'))
  .map((key) => key.slice('oob.hardware.attribute.'.length));
