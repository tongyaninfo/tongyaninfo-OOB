import type { BrowserWindow, Session } from 'electron';
import { randomBytes } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';

export interface SmokeDiagnostics {
  consoleErrors: { source: string; line: number }[];
  failedRequests: { path: string; method: string; status?: number; error?: string }[];
  expectedAnonymousResponses: number;
  canceledRequests: number;
}

async function verifyKeyboardRouting(window: BrowserWindow): Promise<void> {
  window.show(); window.focus();
  await window.webContents.executeJavaScript(`(() => {
    const root=document.createElement('div'); root.id='desktop-keyboard-smoke';
    const input=document.createElement('input'); input.id='desktop-editor-smoke'; input.value='desktop-selection-check';
    window.__oobSmokeEditorKeys=[]; input.addEventListener('keydown',event=>window.__oobSmokeEditorKeys.push({code:event.code,meta:event.metaKey,ctrl:event.ctrlKey}));
    const consoleArea=document.createElement('div'); consoleArea.id='desktop-console-smoke'; consoleArea.role='application'; consoleArea.tabIndex=0;
    consoleArea.addEventListener('keydown', event => { event.preventDefault(); window.__oobSmokeKeys.push(event.code); });
    window.__oobSmokeKeys=[]; root.append(input,consoleArea); document.body.append(root); input.focus(); input.setSelectionRange(0,0);
  })()`, true);
  await waitFor(window, 'document.hasFocus()', '桌面窗口未获得输入焦点');
  const modifier = process.platform === 'darwin' ? 'command' : 'control';
  const key = (keyCode: string) => {
    window.webContents.sendInputEvent({ type: 'keyDown', keyCode, modifiers: [modifier] });
    window.webContents.sendInputEvent({ type: 'keyUp', keyCode, modifiers: [modifier] });
  };
  await delay(100);
  key('A');
  await delay(100);
  const editorReceived = await window.webContents.executeJavaScript('window.__oobSmokeEditorKeys.some(key => key.code === "KeyA" && (key.meta || key.ctrl))', true) as boolean;
  if (!editorReceived) throw new Error('普通输入框未收到编辑组合键');
  // sendInputEvent 在 macOS 不执行 Cocoa key-equivalent；单独验证 Chromium 编辑命令。
  window.webContents.selectAll();
  await delay(100);
  const selected = await window.webContents.executeJavaScript("(() => {const input=document.querySelector('#desktop-editor-smoke');return input.selectionStart===0 && input.selectionEnd===input.value.length})()", true) as boolean;
  if (!selected) {
    const focus = await window.webContents.executeJavaScript("({focused:document.hasFocus(),active:document.activeElement?.id,start:document.querySelector('#desktop-editor-smoke').selectionStart,end:document.querySelector('#desktop-editor-smoke').selectionEnd,keys:window.__oobSmokeEditorKeys})", true) as unknown;
    throw new Error(`普通输入框的系统编辑快捷键未生效：${JSON.stringify(focus)}`);
  }
  await window.webContents.executeJavaScript("document.querySelector('#desktop-console-smoke').focus()", true);
  await delay(100);
  for (const code of ['A', 'C', 'V', ...(process.platform === 'darwin' ? ['Q'] : [])]) key(code);
  await delay(100);
  const captured = await window.webContents.executeJavaScript('window.__oobSmokeKeys', true) as string[];
  for (const code of ['KeyA', 'KeyC', 'KeyV', ...(process.platform === 'darwin' ? ['KeyQ'] : [])])
    if (!captured.includes(code)) throw new Error('控制台组合键被桌面菜单抢占');
  await window.webContents.executeJavaScript(`(() => {
    document.querySelector('#desktop-keyboard-smoke').remove();
    delete window.__oobSmokeKeys;
    delete window.__oobSmokeEditorKeys;
    window.__oobSmokeBlur=false;
    window.addEventListener('blur',()=>{window.__oobSmokeBlur=true},{once:true});
    window.scrollTo(0,0);
    if(document.scrollingElement)document.scrollingElement.scrollTop=0;
    for(const element of document.querySelectorAll('main'))element.scrollTop=0;
  })()`, true);
  await delay(100);
}

function safePath(value: string): string {
  try { return new URL(value).pathname; } catch { return '[unknown]'; }
}

/** 仅测试模式收集安全位置和状态码；不收集 headers、请求正文、Cookie、密码或查询串。 */
export async function recordSmokeDiagnostics(window: BrowserWindow, session: Session): Promise<SmokeDiagnostics> {
  const diagnostics: SmokeDiagnostics = { consoleErrors: [], failedRequests: [], expectedAnonymousResponses: 0, canceledRequests: 0 };
  window.webContents.on('console-message', event => {
    if (event.level !== 'error') return;
    if (event.message.includes('401') && safePath(event.sourceId) === '/api/auth/current-user') return;
    diagnostics.consoleErrors.push({ source: safePath(event.sourceId), line: event.lineNumber });
  });
  session.webRequest.onCompleted(details => {
    if (details.statusCode < 400) return;
    const endpoint = safePath(details.url);
    if (details.statusCode === 401 && endpoint === '/api/auth/current-user') diagnostics.expectedAnonymousResponses++;
    else diagnostics.failedRequests.push({ path: endpoint, method: details.method, status: details.statusCode });
  });
  // CDP 提供 canceled 标记，能准确区分导航取消（含 Font 的 ERR_CACHE_MISS）和真正失败。
  const requests = new Map<string, { path: string; method: string }>();
  window.webContents.debugger.attach('1.3');
  window.webContents.debugger.on('message', (_event, method, parameters: Record<string, unknown>) => {
    const id = parameters.requestId;
    if (typeof id !== 'string') return;
    if (method === 'Network.requestWillBeSent') {
      const request = parameters.request as { url?: string; method?: string } | undefined;
      if (request?.url && request.method) requests.set(id, { path: safePath(request.url), method: request.method });
    }
    if (method === 'Network.loadingFailed') {
      if (parameters.canceled === true || parameters.errorText === 'net::ERR_ABORTED') diagnostics.canceledRequests++;
      else {
        const request = requests.get(id);
        if (request) diagnostics.failedRequests.push({ ...request, error: String(parameters.errorText) });
      }
      requests.delete(id);
    }
    if (method === 'Network.loadingFinished') requests.delete(id);
  });
  await window.webContents.debugger.sendCommand('Network.enable');
  return diagnostics;
}

async function waitFor(window: BrowserWindow, expression: string, message: string): Promise<void> {
  const deadline = Date.now() + 30000;
  while (Date.now() < deadline) {
    try { if (await window.webContents.executeJavaScript(expression, true)) return; } catch { /* 页面导航时等待新上下文。 */ }
    await delay(100);
  }
  throw new Error(message);
}

/** 真实初始化和 UI 登录，仅用于新的隔离数据目录，不创建服务器或发送设备操作。 */
export async function runFirstRunSmoke(window: BrowserWindow, initialized: boolean): Promise<{ initialized: boolean; authenticated: boolean; title: string; body: number; node: string; fontsLoaded: boolean; keyboardRouting: boolean }> {
  if (initialized) throw new Error('完整冒烟必须使用新的隔离数据目录');
  await waitFor(window, "!!document.querySelector('#setup') && !document.querySelector('#submit').disabled", '初始化表单未就绪');
  const password = `Smoke-${randomBytes(24).toString('base64url')}`;
  await window.webContents.executeJavaScript(`(() => {
    document.querySelector('#password').value = ${JSON.stringify(password)};
    document.querySelector('#confirm').value = ${JSON.stringify(password)};
    document.querySelector('#setup').requestSubmit();
  })()`, true);
  await waitFor(window, "!!document.querySelector('input#username') && !!document.querySelector('input#password')", '初始化后未进入现有 PC 登录页');
  await window.webContents.executeJavaScript(`(() => {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
    const username = document.querySelector('input#username');
    const password = document.querySelector('input#password');
    setter.call(username, 'admin'); username.dispatchEvent(new Event('input', {bubbles:true}));
    setter.call(password, ${JSON.stringify(password)}); password.dispatchEvent(new Event('input', {bubbles:true}));
    password.closest('form').requestSubmit();
  })()`, true);
  await waitFor(window, "location.pathname.startsWith('/oob/')", '现有 PC 登录表单未完成登录');
  const authenticated = await window.webContents.executeJavaScript(`fetch('/api/auth/current-user').then(async response => {
    const result = await response.json();
    return response.ok && result.success && result.data?.username === 'admin';
  })`, true) as boolean;
  if (!authenticated) throw new Error('登录后当前账号核验未通过');
  await verifyKeyboardRouting(window);
  await delay(500);
  await window.webContents.executeJavaScript('document.fonts.ready.then(() => true)', true);
  const page = await window.webContents.executeJavaScript('({title:document.title,body:document.body.innerText.length,node:typeof require,fontsLoaded:document.fonts.check(\'400 16px "AlibabaSans"\') && document.fonts.check(\'700 16px "AlibabaSans"\')})', true) as { title: string; body: number; node: string; fontsLoaded: boolean };
  if (!page.fontsLoaded) throw new Error('本地字体未完成加载');
  return { initialized: true, authenticated, keyboardRouting: true, ...page };
}
