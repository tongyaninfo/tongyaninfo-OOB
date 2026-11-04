import { useEffect, useRef } from 'react';
import type { RequestFailure } from '@/services/oob/http';
import { sessionGeneration } from '@/services/oob/session';

/** 每次异步动作绑定账号代次及组件生存期，旧身份的响应不能回写页面。 */
export function useRequestScope() {
  const active = useRef(true);
  const pending = useRef(false);
  useEffect(() => {
    active.current = true;
    return () => {
      active.current = false;
    };
  }, []);
  return {
    active,
    pending,
    capture: () => {
      const expected = sessionGeneration();
      return () => active.current && sessionGeneration() === expected;
    },
  };
}
export const outcomeUnknown = (result: RequestFailure) =>
  result.dispatched && (result.kind !== 'business' || result.status >= 500);
export const requiresReload = (result: RequestFailure) =>
  outcomeUnknown(result) || result.status === 409;
export const lines = (value?: string) => [
  ...new Set(
    (value || '')
      .split(/\r?\n/)
      .map((line) => line.trim())
      .filter(Boolean),
  ),
];
const secureUrl = (url: URL) =>
  url.protocol === 'https:' ||
  (url.protocol === 'http:' &&
    (url.hostname === 'localhost' ||
      url.hostname === '[::1]' ||
      /^127\./.test(url.hostname)));
export function validOrigin(value: string) {
  try {
    const url = new URL(value);
    return (
      !value.includes('*') &&
      secureUrl(url) &&
      !url.username &&
      !url.password &&
      url.origin === value
    );
  } catch {
    return false;
  }
}
export function validRedirect(value: string) {
  try {
    const url = new URL(value);
    return (
      !value.includes('*') &&
      secureUrl(url) &&
      !url.hash &&
      !url.username &&
      !url.password
    );
  } catch {
    return false;
  }
}
/** URL解析器用于IPv6语法校验，不发网络请求；IPv4避免接受URL解析器的简写地址。 */
export function validNetwork(value: string) {
  const segments = value.split('/');
  if (segments.length > 2) return false;
  const [address, prefix] = segments;
  const v4 =
    /^(?:\d{1,3}\.){3}\d{1,3}$/.test(address) &&
    address.split('.').every((part) => Number(part) <= 255);
  let v6 = false;
  let mapped = false;
  if (address.includes(':') && !/[[\]%]/.test(address)) {
    try {
      const host = new URL(`http://[${address}]/`).hostname;
      v6 = host.startsWith('[');
      mapped = host.startsWith('[::ffff:');
    } catch {
      v6 = false;
    }
  }
  return (
    (v4 || v6) &&
    (prefix === undefined ||
      (/^\d{1,3}$/.test(prefix) &&
        Number(prefix) <= (v4 ? 32 : 128) &&
        (!mapped || Number(prefix) >= 96)))
  );
}
export const interactive = (target: EventTarget | null) =>
  target instanceof HTMLElement &&
  Boolean(
    target.closest(
      'button,a,input,textarea,select,[role=button],[role=combobox]',
    ),
  );
export function detailRow<T>(select: (row: T) => void) {
  return (row: T) => ({
    tabIndex: 0,
    onClick: (event: React.MouseEvent) => {
      if (!interactive(event.target)) select(row);
    },
    onKeyDown: (event: React.KeyboardEvent) => {
      if (
        !interactive(event.target) &&
        !event.repeat &&
        !event.nativeEvent.isComposing &&
        !event.altKey &&
        !event.ctrlKey &&
        !event.metaKey &&
        !event.shiftKey &&
        ['Enter', ' '].includes(event.key)
      ) {
        event.preventDefault();
        select(row);
      }
    },
  });
}
