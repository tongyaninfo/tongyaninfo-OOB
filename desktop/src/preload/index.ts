import { contextBridge, ipcRenderer } from 'electron';

/** 在页面脚本读取 Umi 语言前设定桌面默认值，保留用户已选的三种正式语言。 */
function initializeDesktopLocale(storage: Pick<Storage, 'getItem' | 'setItem'>): void {
  const locale = storage.getItem('umi_locale');
  if (locale !== 'zh-CN' && locale !== 'en-US' && locale !== 'zh-TW')
    storage.setItem('umi_locale', 'zh-CN');
}

try {
  // 首启向导与 PC 共用当前 loopback origin；空白调试页没有可用的本地存储。
  // 动态端口改变后属于新 origin，不据此承诺跨次启动的语言偏好迁移。
  if (location.protocol === 'http:' && location.hostname === '127.0.0.1')
    initializeDesktopLocale(window.localStorage);
} catch { /* 存储不可用不能阻断桌面能力桥和页面初始化。 */ }

/** 桌面页面仅获得固定动作，不暴露文件系统、进程、任意 IPC 或启动秘密。 */
contextBridge.exposeInMainWorld('oobDesktop', Object.freeze({
  version: process.versions.electron,
  showMain: (): Promise<void> => ipcRenderer.invoke('desktop:show-main'),
  openBackups: (): Promise<void> => ipcRenderer.invoke('desktop:open-backups'),
}));

/** 捕获控制台时让组合键到达现有页面；普通表单恢复系统编辑菜单。 */
function reportConsoleFocus(): void {
  const active = document.activeElement;
  const pointer = document.pointerLockElement;
  const inConsole = (element: Element | null): boolean => Boolean(element?.closest('[role="application"]')
    || element instanceof HTMLIFrameElement && element.src.startsWith(`${location.origin}/api/oob/console-sessions/`));
  ipcRenderer.send('desktop:console-focus', document.hasFocus() && (inConsole(active) || inConsole(pointer)));
}
for (const event of ['focusin', 'focusout', 'pointerlockchange']) document.addEventListener(event, () => { setTimeout(reportConsoleFocus, 0); }, true);
window.addEventListener('focus', reportConsoleFocus);
window.addEventListener('blur', () => { ipcRenderer.send('desktop:console-focus', false); });
window.addEventListener('beforeunload', () => { ipcRenderer.send('desktop:console-focus', false); });
