type SessionListener = () => void;
const listeners = new Set<SessionListener>();
let generation = 0;
let expired = false;

/** 认证代次隔离旧请求：上一登录的迟到 401 不能踢掉刚建立的新身份。 */
export const sessionGeneration = () => generation;
export const isSessionExpired = () => expired;
export function beginSession() {
  generation++;
  expired = false;
}
export function expireSession(expected = generation) {
  if (expected !== generation || expired) return;
  expired = true;
  for (const listener of listeners) listener();
}
export function subscribeSessionExpiry(listener: SessionListener) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}
