import { useQueryClient } from '@tanstack/react-query';
import { history } from '@umijs/max';
import React, { useEffect } from 'react';
import {
  clearExpiredSession,
  currentUser as queryCurrentUser,
} from '@/services/oob/auth';
import type { CurrentUserDto } from '@/services/oob/models';
import {
  sessionGeneration,
  subscribeSessionExpiry,
} from '@/services/oob/session';

const loginPath = '/user/login';

/** 服务端失效立即清掉旧身份；定期只读校验让停留中的页面也及时反映撤权。 */
export default function SessionGuard({
  user,
  onChanged,
}: {
  user?: CurrentUserDto;
  onChanged: (user?: CurrentUserDto) => void;
}) {
  const cache = useQueryClient();
  const onChangedRef = React.useRef(onChanged);
  onChangedRef.current = onChanged;
  useEffect(() => {
    if (!user) return;
    let active = true;
    let checking = false;
    const expected = sessionGeneration();
    const abort = new AbortController();
    const unsubscribe = subscribeSessionExpiry(() => {
      if (!active) return;
      active = false;
      abort.abort();
      // 先隔离回调、销毁页面，再异步关闭浏览器连接，避免旧账号数据回写。
      void clearExpiredSession();
      void cache.cancelQueries({ queryKey: ['oob'] });
      cache.removeQueries({ queryKey: ['oob'] });
      onChangedRef.current(undefined);
      const { pathname, search, hash } = history.location;
      if (pathname !== loginPath)
        history.replace(
          `${loginPath}?redirect=${encodeURIComponent(pathname + search + hash)}`,
        );
    });
    const check = async () => {
      if (!active || checking || document.hidden) return;
      checking = true;
      try {
        const result = await queryCurrentUser(abort.signal);
        if (
          active &&
          expected === sessionGeneration() &&
          result.success &&
          JSON.stringify(result.data) !== JSON.stringify(user)
        )
          onChangedRef.current(result.data);
      } finally {
        checking = false;
      }
    };
    const timer = setInterval(() => {
      void check();
    }, 15000);
    const focus = () => {
      void check();
    };
    window.addEventListener('focus', focus);
    return () => {
      active = false;
      abort.abort();
      clearInterval(timer);
      window.removeEventListener('focus', focus);
      unsubscribe();
    };
  }, [user, cache]);
  return null;
}
