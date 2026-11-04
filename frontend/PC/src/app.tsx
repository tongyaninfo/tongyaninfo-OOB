import type { Settings as LayoutSettings } from '@ant-design/pro-components';
import type { RequestConfig, RunTimeLayoutConfig } from '@umijs/max';
import { history, Link } from '@umijs/max';
import { notification } from 'antd';
import dayjs from 'dayjs';
import relativeTime from 'dayjs/plugin/relativeTime';
import React, { useEffect } from 'react';
import AppearanceSettings from '@/components/AppearanceSettings';
import {
  normalizeThemeMode,
  resolveTheme,
  type ThemeMode,
} from '@/components/AppearanceSettings/theme';
import SessionGuard from '@/components/SessionGuard';
import { restoreSupportedLocale } from '@/locales/supported';
import ConsoleWorkspaceHost from '@/pages/oob/Consoles/ConsoleWorkspaceHost';
import {
  captureRuntimeException,
  installBrowserExceptionCapture,
} from '@/services/oob/browserExceptions';
import { restorePreferences } from '@/services/oob/preferences';

// Initialize dayjs plugins globally
dayjs.extend(relativeTime);

import {
  AvatarDropdown,
  DocLink,
  ErrorBoundary,
  Footer,
  LangDropdown,
  OfflineBanner,
} from '@/components';
import { currentUser as queryCurrentUser } from '@/services/oob/auth';
import type { CurrentUserDto } from '@/services/oob/models';
import defaultSettings from '../config/defaultSettings';
import { errorConfig } from './requestErrorConfig';

const loginPath = '/user/login';

/**
 * @see https://umijs.org/docs/api/runtime-config#getinitialstate
 * */
export async function getInitialState(): Promise<{
  settings?: Partial<LayoutSettings>;
  themeMode?: ThemeMode;
  currentUser?: CurrentUserDto;
  loading?: boolean;
  fetchUserInfo?: () => Promise<CurrentUserDto | undefined>;
  settingDrawerOpen?: boolean;
}> {
  restoreSupportedLocale();
  const fetchUserInfo = async () => {
    try {
      const msg = await queryCurrentUser();
      if (msg.success) return msg.data;
      if (msg.status !== 401) return undefined;
      const { pathname, search, hash } = history.location;
      history.replace(
        `${loginPath}?redirect=${encodeURIComponent(pathname + search + hash)}`,
      );
    } catch (error) {
      void captureRuntimeException(error);
      const { pathname, search, hash } = history.location;
      history.replace(
        `${loginPath}?redirect=${encodeURIComponent(pathname + search + hash)}`,
      );
    }
    return undefined;
  };
  // 如果不是登录页面，执行
  const { location } = history;
  if (
    ![loginPath, '/user/register', '/user/register-result'].includes(
      location.pathname,
    )
  ) {
    const currentUser = await fetchUserInfo();
    let layoutSettings = {};
    if (currentUser) {
      try {
        const preferences = await restorePreferences();
        if (preferences.success)
          layoutSettings = preferences.data.layoutSettings || {};
        else notification.warning({ title: preferences.errorMessage });
      } catch (error) {
        void captureRuntimeException(error);
        notification.warning({
          title: '外观设置恢复失败 / Appearance settings unavailable',
          description:
            '本次主题调整暂不覆盖账户设置。 / Existing account settings will be preserved.',
        });
      }
    }
    const settings = { ...defaultSettings, ...layoutSettings };
    const themeMode = normalizeThemeMode(settings.navTheme);
    return {
      fetchUserInfo,
      currentUser,
      settings: {
        ...settings,
        navTheme: resolveTheme(themeMode),
      } as Partial<LayoutSettings>,
      themeMode,
      settingDrawerOpen: false,
    };
  }
  return {
    fetchUserInfo,
    settings: defaultSettings as Partial<LayoutSettings>,
    settingDrawerOpen: false,
  };
}

// ProLayout 支持的api https://procomponents.ant.design/components/layout
export const layout: RunTimeLayoutConfig = ({
  initialState,
  setInitialState,
}) => {
  return {
    menuItemRender: (item, dom) => {
      if (item.path) {
        return (
          <Link to={item.path} prefetch>
            {dom}
          </Link>
        );
      }
      return dom;
    },
    actionsRender: () => {
      // `locale: false` opts out of the language switcher. ProLayout's own
      // `locale` prop is a locale string, so narrow to the boolean toggle here.
      const localeEnabled =
        (initialState?.settings as { locale?: boolean })?.locale !== false;
      return [
        <DocLink key="docs" />,
        ...(localeEnabled ? [<LangDropdown key="lang" />] : []),
      ];
    },
    avatarProps: {
      src: initialState?.currentUser?.avatar,
      title: initialState?.currentUser?.name || 'ProUser',
      render: (_, avatarChildren) => (
        <AvatarDropdown>{avatarChildren}</AvatarDropdown>
      ),
    },
    // waterMarkProps: {
    //   content: initialState?.currentUser?.name,
    // },
    footerRender: () => <Footer />,
    onPageChange: () => {
      const { location } = history;
      // 如果没有登录，重定向到 login
      if (!initialState?.currentUser && location.pathname !== loginPath) {
        history.replace(
          `${loginPath}?redirect=${encodeURIComponent(location.pathname + location.search + location.hash)}`,
        );
      }
    },
    // 工作区采用干净的品牌底色，不再加载模板外部装饰图。
    bgLayoutImgList: [],
    // 业务导航不暴露模板开发工具；接口文档仍可从本地 Swagger 独立访问。
    links: [],
    // Replace ProLayout's default ErrorBoundary with our offline-aware version,
    // so chunk load errors show friendly messages instead of "Something went wrong."
    ErrorBoundary,
    menuHeaderRender: undefined,
    // 自定义 403 页面
    // unAccessible: <div>unAccessible</div>,
    // 增加一个 loading 的状态
    childrenRender: (children) => {
      // if (initialState?.loading) return <PageLoading />;
      return (
        <>
          <RuntimeExceptionCapture userId={initialState?.currentUser?.userid} />
          <SessionGuard
            user={initialState?.currentUser}
            onChanged={(currentUser) =>
              setInitialState((state) => ({
                ...state,
                currentUser,
                ...(currentUser
                  ? {}
                  : {
                      settings: undefined,
                      themeMode: undefined,
                      settingDrawerOpen: false,
                    }),
              }))
            }
          />
          {children}
          <ConsoleWorkspaceHost user={initialState?.currentUser} />
          <AppearanceSettings
            key={initialState?.currentUser?.userid || 'anonymous'}
            open={initialState?.settingDrawerOpen}
            onOpenChange={(open) => {
              setInitialState((s) => ({
                ...s,
                settingDrawerOpen: open,
              }));
            }}
            settings={initialState?.settings}
            themeMode={initialState?.themeMode}
            onChange={(settings, themeMode) => {
              setInitialState((s) => ({
                ...s,
                settings,
                themeMode,
              }));
            }}
          />
        </>
      );
    },
    ...initialState?.settings,
    token: {
      ...defaultSettings.token,
      // ProLayout 默认叠加渐变；工作区保持纯色，深色不能沿用浅色品牌底色。
      bgLayout:
        initialState?.settings?.navTheme === 'realDark' ? '#000000' : '#f3f6fb',
    },
  };
};

/**
 * @name request 配置，可以配置错误处理
 * 它基于 axios 提供了一套统一的网络请求和错误处理方案。
 * @doc https://umijs.org/docs/max/request#配置
 */
export const request: RequestConfig = {
  baseURL: '',
  ...errorConfig,
};

function RuntimeExceptionCapture({ userId }: { userId?: string }) {
  useEffect(() => installBrowserExceptionCapture(userId), [userId]);
  return null;
}

export function rootContainer(container: React.ReactNode) {
  return (
    <>
      <OfflineBanner />
      <ErrorBoundary>{container}</ErrorBoundary>
    </>
  );
}
