import {
  LockOutlined,
  LogoutOutlined,
  SettingOutlined,
  SkinOutlined,
} from '@ant-design/icons';
import { type QueryClient, useQueryClient } from '@tanstack/react-query';
import { history, useIntl, useModel } from '@umijs/max';
import type { MenuProps } from 'antd';
import { App, Spin } from 'antd';
import React, { startTransition, useState } from 'react';
import ChangePasswordModal from '@/components/ChangePasswordModal';
import { outLogin } from '@/services/oob/auth';
import HeaderDropdown from '../HeaderDropdown';

type GlobalHeaderRightProps = {
  children?: React.ReactNode;
};

const menuItems = (label: (id: string) => string): MenuProps['items'] => [
  {
    key: 'password',
    icon: <LockOutlined />,
    label: label('oob.password.change'),
  },
  {
    key: 'settings',
    icon: <SettingOutlined />,
    label: label('menu.account.settings'),
  },
  {
    key: 'theme',
    icon: <SkinOutlined />,
    label: label('component.globalHeader.theme'),
  },
  {
    type: 'divider' as const,
  },
  {
    key: 'logout',
    icon: <LogoutOutlined />,
    label: label('menu.account.logout'),
  },
];

const loginOut = async (cache: QueryClient) => {
  // 只有后端会话真正失效后才清理页面身份，不吞掉退出失败。
  const result = await outLogin();
  if (!result.success) return result;
  // 模块级 QueryClient 不跟随页面身份销毁，退出成功后清除设备数据及未完成的读取。
  await cache.cancelQueries({ queryKey: ['oob'] });
  cache.removeQueries({ queryKey: ['oob'] });
  const { search, pathname } = window.location;
  const urlParams = new URL(window.location.href).searchParams;
  const searchParams = new URLSearchParams({
    redirect: pathname + search,
  });
  const redirect = urlParams.get('redirect');
  if (window.location.pathname !== '/user/login' && !redirect) {
    history.replace({
      pathname: '/user/login',
      search: searchParams.toString(),
    });
  }
  return result;
};

export const AvatarDropdown: React.FC<GlobalHeaderRightProps> = ({
  children,
}) => {
  const { initialState, setInitialState } = useModel('@@initialState');
  const { message } = App.useApp();
  const cache = useQueryClient();
  const intl = useIntl();
  const [passwordOpen, setPasswordOpen] = useState(false);

  const onMenuClick: MenuProps['onClick'] = (event) => {
    const { key } = event;
    if (key === 'password') {
      setPasswordOpen(true);
      return;
    }
    if (key === 'logout') {
      loginOut(cache)
        .then((result) => {
          if (!result.success) {
            message.error(result.errorMessage);
            return;
          }
          startTransition(() =>
            setInitialState((s) => ({
              ...s,
              currentUser: undefined,
              settings: undefined,
              themeMode: undefined,
              settingDrawerOpen: false,
            })),
          );
        })
        .catch((error: Error) => message.error(error.message));
      return;
    }
    if (key === 'theme') {
      setInitialState((s) => ({ ...s, settingDrawerOpen: true }));
      return;
    }
    history.push(`/account/${key}`);
  };

  if (!initialState) {
    return <Spin size="small" />;
  }

  const { currentUser } = initialState;

  if (!currentUser) {
    return <Spin size="small" />;
  }

  return (
    <>
      <HeaderDropdown
        placement="bottomRight"
        menu={{
          selectedKeys: [],
          onClick: onMenuClick,
          items: menuItems((id) => intl.formatMessage({ id })),
        }}
        arrow
      >
        {children}
      </HeaderDropdown>
      <ChangePasswordModal
        open={passwordOpen}
        onClose={() => setPasswordOpen(false)}
      />
    </>
  );
};
