import type { Settings as LayoutSettings } from '@ant-design/pro-components';
import { SettingDrawer } from '@ant-design/pro-components';
import { DesktopOutlined, MoonOutlined, SunOutlined } from '@ant-design/icons';
import { useIntl } from '@umijs/max';
import { notification, Segmented, Typography } from 'antd';
import React, { useEffect, useRef } from 'react';
import { hasLayoutChanges, persistLayout } from '@/services/oob/preferences';
import {
  normalizeThemeMode,
  preferenceSettings,
  resolveTheme,
  subscribeSystemTheme,
  type ThemeMode,
} from './theme';
import './index.css';

type Props = {
  settings?: Partial<LayoutSettings>;
  themeMode?: ThemeMode;
  open?: boolean;
  onOpenChange: (open: boolean) => void;
  onChange: (settings: Partial<LayoutSettings>, themeMode: ThemeMode) => void;
};

/** 锁版 SettingDrawer 挂载会主动回传规范化设置；初始化只同步展示，不写账号偏好。 */
export default function AppearanceSettings({
  settings,
  themeMode = normalizeThemeMode(settings?.navTheme),
  open,
  onOpenChange,
  onChange,
}: Props) {
  const initializing = useRef(true);
  const previous = useRef(settings);
  const previousMode = useRef(themeMode);
  const onChangeRef = useRef(onChange);
  previous.current = settings;
  previousMode.current = themeMode;
  onChangeRef.current = onChange;
  const intl = useIntl();
  const label = (id: string, defaultMessage: string) =>
    intl.formatMessage({ id, defaultMessage });

  useEffect(() => {
    if (themeMode !== 'auto') return;
    return subscribeSystemTheme((navTheme) => {
      // 手动选择已发生但 effect 尚未清理时，旧媒体事件也不能恢复自动模式。
      if (
        previousMode.current !== 'auto' ||
        previous.current?.navTheme === navTheme
      )
        return;
      const next = { ...previous.current, navTheme };
      previous.current = next;
      // 媒体查询不是用户修改，不进入账号偏好保存队列。
      onChangeRef.current(next, 'auto');
    });
  }, [themeMode]);

  const change = (
    next: Partial<LayoutSettings>,
    mode: ThemeMode,
    save: boolean,
  ) => {
    const preference = preferenceSettings(next, mode);
    const changed = hasLayoutChanges(
      preferenceSettings(previous.current || {}, previousMode.current),
      preference,
    );
    previous.current = next;
    previousMode.current = mode;
    if (save && changed)
      persistLayout(preference, () =>
        notification.error({
          title: label('app.setting.save.failed', '设置保存失败'),
        }),
      );
    onChange(next, mode);
  };

  return (
    <SettingDrawer
      disableUrlParams
      enableDarkTheme
      collapse={open}
      onCollapseChange={onOpenChange}
      settings={settings}
      hideHintAlert
      hideCopyButton
      drawerProps={{
        rootClassName: 'oob-appearance-settings',
        title: (
          <div>
            <Typography.Paragraph>
              {label('app.setting.pagestyle', '整体风格设置')}
            </Typography.Paragraph>
            <Segmented<ThemeMode>
              block
              name="oob-theme-mode"
              aria-label={label('app.setting.pagestyle', '整体风格设置')}
              value={themeMode}
              options={[
                {
                  value: 'auto',
                  label: label('app.setting.pagestyle.auto', '自动'),
                  icon: <DesktopOutlined />,
                },
                {
                  value: 'light',
                  label: label('app.setting.pagestyle.light.short', '浅色'),
                  icon: <SunOutlined />,
                },
                {
                  value: 'realDark',
                  label: label('app.setting.pagestyle.dark.short', '深色'),
                  icon: <MoonOutlined />,
                },
              ]}
              onChange={(mode) =>
                change(
                  { ...settings, navTheme: resolveTheme(mode) },
                  mode,
                  true,
                )
              }
            />
            <Typography.Paragraph
              type="secondary"
              style={{ marginBlock: '8px 0', fontSize: 12, fontWeight: 400 }}
            >
              {label(
                'app.setting.pagestyle.auto.hint',
                '自动模式跟随操作系统的浅色或深色设置。',
              )}
            </Typography.Paragraph>
          </div>
        ),
      }}
      onSettingChange={(next) => {
        const initial = initializing.current;
        initializing.current = false;
        // 原抽屉在切换导航布局时可能强制浅色，保留用户选择的主题模式。
        change(
          { ...next, navTheme: resolveTheme(previousMode.current) },
          previousMode.current,
          !initial && !!open,
        );
      }}
    />
  );
}
