import { app, BrowserWindow, dialog, ipcMain, Menu, nativeImage, screen, session, shell, Tray, type IpcMainInvokeEvent } from 'electron';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { HostProcess } from './host-process';
import { gateCookieName, isAllowedPermission, isTrustedNavigation } from './protocol';
import { recordSmokeDiagnostics, runFirstRunSmoke } from './smoke';
import { releasePageInput } from './page-lifecycle';

const configuredData = process.env.OOB_DESKTOP_DATA;
if (process.env.OOB_DESKTOP_SMOKE === '1' && (!configuredData || existsSync(path.join(configuredData, 'data/oob.sqlite'))))
  throw new Error('完整桌面冒烟只允许使用显式指定且没有数据库的隔离数据目录');
if (configuredData) {
  if (!path.isAbsolute(configuredData)) throw new Error('桌面数据目录必须是绝对路径');
  app.setPath('userData', configuredData);
}
app.setName('带外管理系统');
let host: HostProcess | undefined;
let mainWindow: BrowserWindow | undefined;
let maintenanceWindow: BrowserWindow | undefined;
let tray: Tray | undefined;
let origin = '';
let quitting = false;
let shutdownComplete = false;
const partition = 'persist:oob-desktop';
const dataDirectory = app.getPath('userData');
const statePath = path.join(dataDirectory, 'window-state.json');

function showMain(): void {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  if (mainWindow.isMinimized()) mainWindow.restore();
  mainWindow.show();
  mainWindow.focus();
}

function validSender(event: Pick<IpcMainInvokeEvent, 'sender' | 'senderFrame'>): boolean {
  return Boolean(event.senderFrame && event.senderFrame === event.sender.mainFrame
    && isTrustedNavigation(event.senderFrame.url, origin)
    && [mainWindow, maintenanceWindow].some(window => window?.webContents === event.sender));
}

function resourcePath(name: string): string {
  return app.isPackaged ? path.join(process.resourcesPath, name) : path.resolve(__dirname, '../../..', 'resources', name);
}

function windowOptions(): Electron.BrowserWindowConstructorOptions {
  return {
    width: 1440, height: 960, minWidth: 1024, minHeight: 640, show: false,
    title: '带外管理系统', icon: resourcePath('icon.png'),
    webPreferences: {
      partition, preload: path.join(__dirname, '../preload/index.js'),
      nodeIntegration: false, contextIsolation: true, sandbox: true, webSecurity: true, spellcheck: false,
      // 本地 ISO 和控制台 Worker 依赖持续运行，隐藏到托盘不能冻结这些会话。
      backgroundThrottling: false,
    },
  };
}

function secureWindow(window: BrowserWindow): void {
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', event => {
    if (!isTrustedNavigation(event.url, origin)) event.preventDefault();
  });
  window.webContents.on('will-frame-navigate', event => {
    if (!isTrustedNavigation(event.url, origin)) event.preventDefault();
  });
  window.webContents.on('will-redirect', event => {
    if (!isTrustedNavigation(event.url, origin)) event.preventDefault();
  });
  window.webContents.on('will-attach-webview', event => event.preventDefault());
  window.on('blur', () => { window.webContents.setIgnoreMenuShortcuts(false); });
}

function restoreBounds(): Partial<Electron.Rectangle> {
  try {
    const value: unknown = JSON.parse(readFileSync(statePath, 'utf8'));
    if (typeof value !== 'object' || value === null) return {};
    const bounds = value as Record<string, unknown>;
    const width = bounds.width; const height = bounds.height; const x = bounds.x; const y = bounds.y;
    if (![width, height, x, y].every(v => typeof v === 'number' && Number.isInteger(v))) return {};
    const rect = { width: width as number, height: height as number, x: x as number, y: y as number };
    if (rect.width < 1024 || rect.height < 640 || rect.width > 20000 || rect.height > 20000) return {};
    if (!screen.getAllDisplays().some(display => {
      const area = display.workArea;
      return rect.x < area.x + area.width - 64 && rect.x + rect.width > area.x + 64
        && rect.y < area.y + area.height - 64 && rect.y + rect.height > area.y + 64;
    })) return {};
    return rect;
  } catch { return {}; }
}

function saveBounds(): void {
  if (!mainWindow || mainWindow.isDestroyed() || mainWindow.isMinimized() || mainWindow.isFullScreen()) return;
  try { writeFileSync(statePath, JSON.stringify(mainWindow.getNormalBounds())); } catch { /* 窗口偏好失败不能阻断安全退出。 */ }
}

async function openMaintenance(): Promise<void> {
  if (maintenanceWindow && !maintenanceWindow.isDestroyed()) { maintenanceWindow.show(); return; }
  maintenanceWindow = new BrowserWindow({ ...windowOptions(), width: 1100, height: 760, title: '数据维护' });
  secureWindow(maintenanceWindow);
  maintenanceWindow.on('closed', () => { maintenanceWindow = undefined; });
  await maintenanceWindow.loadURL(`${origin}/desktop/maintenance`);
  maintenanceWindow.show();
}

async function openBackups(): Promise<void> {
  const backups = path.join(dataDirectory, 'backups');
  mkdirSync(backups, { recursive: true });
  const error = await shell.openPath(backups);
  if (error) dialog.showErrorBox('无法打开备份目录', '请检查当前账号是否有目录访问权限。');
}

async function start(): Promise<void> {
  mkdirSync(dataDirectory, { recursive: true });
  const executable = process.env.OOB_DESKTOP_HOST_PATH
    ?? path.join(process.resourcesPath, 'host', `OobManagement.Desktop.Host${process.platform === 'win32' ? '.exe' : ''}`);
  if (!path.isAbsolute(executable) || !existsSync(executable)) throw new Error('未找到本地服务，请先运行桌面构建命令或重新安装应用。');
  host = new HostProcess({ executable, dataDirectory, onUnexpectedExit: () => {
    if (quitting) return;
    dialog.showErrorBox('本地服务已停止', '采样和控制台已暂停。请退出后重新启动应用；未完成的设备操作不会自动重发。');
    void stopAndQuit();
  } });
  const ready = await host.start();
  origin = ready.url;
  // 所有静态资源均在本机，禁用 HTTP 磁盘缓存，避免动态端口/首启导航的旧缓存元数据。
  const desktopSession = session.fromPartition(partition, { cache: false });
  desktopSession.setPermissionRequestHandler((contents, permission, callback, details) => {
    // 控制台只获得全屏、键鼠捕获及安全剪贴板写入，其余设备和通知权限默认拒绝。
    callback(isAllowedPermission(permission, contents.getURL(), details.requestingUrl, origin));
  });
  desktopSession.setPermissionCheckHandler((contents, permission, requestingOrigin, details) => Boolean(contents
    && isAllowedPermission(permission, contents.getURL(), details.requestingUrl ?? requestingOrigin, origin)));
  // 设备通信属于 .NET 驱动；页面资源与通道全部保持同源，不从在线 CDN 加载代码。
  desktopSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*', 'ws://*/*', 'wss://*/*'] }, (details, callback) => {
    const target = details.url.replace(/^ws:/, 'http:').replace(/^wss:/, 'https:');
    callback({ cancel: !isTrustedNavigation(target, origin) });
  });
  desktopSession.on('will-download', (event, item, contents) => {
    if (!contents || !isTrustedNavigation(contents.getURL(), origin)) { event.preventDefault(); return; }
    item.setSaveDialogOptions({ defaultPath: path.join(app.getPath('downloads'), path.basename(item.getFilename())) });
  });
  await desktopSession.cookies.set({
    url: origin, name: gateCookieName, value: host.launchSecret,
    httpOnly: true, secure: false, sameSite: 'strict', path: '/',
  });
  mainWindow = new BrowserWindow({ ...windowOptions(), ...restoreBounds() });
  secureWindow(mainWindow);
  // 测试调试协议需要已建立的 renderer；空白页由主进程加载，不参与产品导航。
  if (process.env.OOB_DESKTOP_SMOKE === '1') await mainWindow.loadURL('about:blank');
  const smokeDiagnostics = process.env.OOB_DESKTOP_SMOKE === '1' ? await recordSmokeDiagnostics(mainWindow, desktopSession) : undefined;
  mainWindow.on('close', event => {
    saveBounds();
    if (!quitting && tray) { event.preventDefault(); mainWindow?.hide(); }
  });
  mainWindow.on('closed', () => { mainWindow = undefined; if (!quitting) void stopAndQuit(); });
  mainWindow.webContents.on('render-process-gone', () => {
    if (!quitting) {
      dialog.showErrorBox('页面已停止', '控制台和本地 ISO 映射可能已结束。请重新启动应用，核对未完成的操作。');
      void stopAndQuit();
    }
  });
  const template: Electron.MenuItemConstructorOptions[] = [
    ...(process.platform === 'darwin' ? [{ role: 'appMenu' as const }] : []),
    { label: '应用', submenu: [
      { label: '显示主窗口', click: showMain },
      { label: '数据维护', click: () => { void openMaintenance(); } },
      { label: '打开备份目录', click: () => { void openBackups(); } },
      { type: 'separator' },
      { label: '退出', ...(process.platform === 'darwin' ? { accelerator: 'Cmd+Q' } : {}), click: () => { void stopAndQuit(); } },
    ] },
    { role: 'editMenu' },
    { label: '视图', submenu: [{ role: 'togglefullscreen' }, { role: 'resetZoom' }, { role: 'zoomIn' }, { role: 'zoomOut' }] },
  ];
  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
  try {
    const image = nativeImage.createFromPath(resourcePath('trayTemplate.png'));
    image.setTemplateImage(process.platform === 'darwin');
    tray = new Tray(image);
    tray.setToolTip('带外管理系统');
    tray.setContextMenu(Menu.buildFromTemplate([
      { label: '打开带外管理', click: showMain },
      { label: '数据维护', click: () => { void openMaintenance(); } },
      { label: '退出', click: () => { void stopAndQuit(); } },
    ]));
    tray.on('click', showMain);
  } catch { /* 无托盘环境关窗即正常退出，避免后台实例无法恢复。 */ }
  await mainWindow.loadURL(origin);
  showMain();
  if (process.env.OOB_DESKTOP_SMOKE === '1') {
    app.focus({ steal: true });
    const page = await runFirstRunSmoke(mainWindow, ready.initialized);
    if (process.env.OOB_DESKTOP_SCREENSHOT) await mainWindow.capturePage().then(image => writeFileSync(process.env.OOB_DESKTOP_SCREENSHOT!, image.toPNG()));
    process.stdout.write(`${JSON.stringify({ type: 'desktop-smoke', ...page, ...smokeDiagnostics })}\n`);
    await stopAndQuit(smokeDiagnostics!.consoleErrors.length || smokeDiagnostics!.failedRequests.length ? 1 : 0);
  }
}

async function stopAndQuit(exitCode = 0): Promise<void> {
  if (quitting) return;
  quitting = true;
  saveBounds();
  const released = await releasePageInput(mainWindow, origin);
  if (process.env.OOB_DESKTOP_SMOKE === '1') {
    const blurObserved = mainWindow && !mainWindow.isDestroyed()
      ? await mainWindow.webContents.executeJavaScript('window.__oobSmokeBlur===true', true).catch(() => false) : false;
    process.stdout.write(`${JSON.stringify({ type: 'desktop-release', released, blurObserved })}\n`);
  }
  maintenanceWindow?.destroy();
  mainWindow?.destroy();
  tray?.destroy();
  await host?.stop();
  shutdownComplete = true;
  app.exit(exitCode);
}

if (!app.requestSingleInstanceLock()) app.quit();
else {
  app.on('second-instance', showMain);
  app.on('activate', showMain);
  app.on('before-quit', event => { if (!shutdownComplete) { event.preventDefault(); void stopAndQuit(); } });
  app.on('window-all-closed', () => { if (!tray && !quitting) void stopAndQuit(); });
  ipcMain.handle('desktop:show-main', event => { if (!validSender(event)) throw new Error('来源未获允许'); showMain(); });
  ipcMain.handle('desktop:open-backups', async event => { if (!validSender(event)) throw new Error('来源未获允许'); await openBackups(); });
  ipcMain.on('desktop:console-focus', (event, captured: unknown) => {
    if (validSender(event) && typeof captured === 'boolean') event.sender.setIgnoreMenuShortcuts(captured);
  });
  app.whenReady().then(start).catch(error => {
    if (process.env.OOB_DESKTOP_SMOKE === '1') {
      process.stdout.write(`${JSON.stringify({ type: 'desktop-smoke-error', message: error instanceof Error ? error.message : '桌面冒烟未完成' })}\n`);
      void stopAndQuit(1);
      return;
    }
    dialog.showErrorBox('启动失败', error instanceof Error ? error.message : '本地服务无法启动');
    void stopAndQuit();
  });
}
