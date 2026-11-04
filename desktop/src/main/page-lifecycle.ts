import type { BrowserWindow } from 'electron';
import { setTimeout as delay } from 'node:timers/promises';
import { isTrustedNavigation } from './protocol';

/** 先走现有 blur 释放链并给 postMessage/WS 有界调度时间，再销毁页面与宿主。 */
export async function releasePageInput(window: BrowserWindow | undefined, origin: string): Promise<boolean> {
  if (!window || window.isDestroyed() || !isTrustedNavigation(window.webContents.getURL(), origin)) return false;
  return Promise.race([
    window.webContents.executeJavaScript(`new Promise(resolve => {
      if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
      window.dispatchEvent(new Event('blur'));
      setTimeout(() => resolve(true), 150);
    })`, true).then(() => true, () => false),
    delay(800).then(() => false),
  ]);
}
