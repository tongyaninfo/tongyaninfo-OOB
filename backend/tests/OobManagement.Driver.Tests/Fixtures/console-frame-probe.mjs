import assert from 'node:assert/strict';
import vm from 'node:vm';

// 公开 USB HID 标准码，用于核对整个 104 键布局；不包含厂商脚本或设备凭据。
const codes = {
  Enter:40,Escape:41,Backspace:42,Tab:43,Space:44,Minus:45,Equal:46,BracketLeft:47,BracketRight:48,Backslash:49,
  Semicolon:51,Quote:52,Backquote:53,Comma:54,Period:55,Slash:56,CapsLock:57,PrintScreen:70,ScrollLock:71,Pause:72,
  Insert:73,Home:74,PageUp:75,Delete:76,End:77,PageDown:78,ArrowRight:79,ArrowLeft:80,ArrowDown:81,ArrowUp:82,
  NumLock:83,NumpadDivide:84,NumpadMultiply:85,NumpadSubtract:86,NumpadAdd:87,NumpadEnter:88,Numpad0:98,NumpadDecimal:99,
  ContextMenu:101,ControlLeft:224,ShiftLeft:225,AltLeft:226,MetaLeft:227,ControlRight:228,ShiftRight:229,AltRight:230,MetaRight:231,
};
for (let index = 0; index < 26; index++) codes[`Key${String.fromCharCode(65 + index)}`] = 4 + index;
for (let index = 1; index <= 9; index++) { codes[`Digit${index}`] = 29 + index; codes[`Numpad${index}`] = 88 + index; }
codes.Digit0 = 39;
for (let index = 1; index <= 12; index++) codes[`F${index}`] = 57 + index;

/** 执行 provider 实际交付的最小 frame；所有 DOM、Worker、renderer 和设备均为内存替身。 */
function createFrame(vendor, html, workerSupported = true) {
  const listeners = new Map(), calls = [], messages = [], mediaActions = [];
  const status = { textContent: '' };
  let rectangle = { left:100, top:100, width:800, height:600, right:900, bottom:700 };
  let native, boot, login, mediaLogin, deviceInfo, deviceStatus, mediaDisconnect, apiError, media;
  let deviceList=[{deviceId:7,deviceType:2,isMapped:false}];
  let rendererCreations = 0;
  let now=0, timerSequence=0;
  const timers=new Map(), timerHistory=new Map();
  // 单调时钟与保留的旧回调可重现浏览器定时器节流、清除后已排队任务，不依赖真实等待。
  function advance(milliseconds, fire=true) {
    const target=now+milliseconds;
    while(fire) {
      const next=[...timers.entries()].filter(([,timer])=>timer.at<=target).sort((a,b)=>a[1].at-b[1].at||a[0]-b[0])[0];
      if(!next)break;
      now=Math.max(now,next[1].at);timers.delete(next[0]);next[1].callback();
    }
    now=target;
  }
  const parent = { postMessage: message => messages.push(message) };
  const canvas = {
    getBoundingClientRect: () => rectangle, addEventListener() {}, focus() {},
    dispatchEvent(event) { calls.push({ type:event.type, buttons:event.buttons, button:event.button, which:event.which, x:event.clientX, y:event.clientY }); return true; },
  };
  class FakeEvent { constructor(type, values = {}) { this.type=type; this.which=0; Object.assign(this, values); } }
  class FakeFile { constructor(name, size) { this.name=name; this.size=size; } slice() { return new ArrayBuffer(0); } }
  class FakeWorker { postMessage() {} terminate() {} }
  class FakeMedia {
    constructor() { media=this; }
    setFile(file) { this.file=file; }
    setMedia_sz(size) { this.size=size; }
    createWebSocket() { this.created=true; }
    closeVMconnection() { this.ejected=true; }
    getWebsocket() { return { close() {} }; }
  }
  const context = {
    console, URL, ArrayBuffer, Uint8Array, File:FakeFile, Worker:workerSupported ? FakeWorker : undefined,
    WebSocket:class {}, PointerEvent:FakeEvent, MouseEvent:FakeEvent, vm_conn:[], vmConnType:{CDROM:2}, VMconnection:FakeMedia,
    location:{ origin:'https://app.invalid', host:'app.invalid', hostname:'app.invalid', protocol:'https:', pathname:'/api/oob/console-sessions/9007199254740993/frame/html/console.html', href:'https://app.invalid/api/oob/console-sessions/9007199254740993/frame/html/console.html' },
    innerWidth:1000, innerHeight:800, parent, Util:{}, PacketHandler:{}, Logger:{}, MediaWorkerHandler:{},
    document:{ hidden:false, getElementById:id => id==='status'||id==='notice' ? status : canvas, addEventListener(type, handler) { (listeners.get(`document:${type}`) || listeners.set(`document:${type}`, []).get(`document:${type}`)).push(handler); } },
    setInterval(callback) { boot=callback; return 1; }, clearInterval() {},
    performance:{now:()=>now},
    setTimeout(callback, milliseconds) { const id=++timerSequence;timers.set(id,{callback,at:now+milliseconds});timerHistory.set(id,callback);return id; },
    clearTimeout(id) { timers.delete(id); },
    $:Object.assign(() => {}, { log() {} }),
    addEventListener(type, handler) { (listeners.get(type) || listeners.set(type, []).get(type)).push(handler); },
  };
  context.window=context; context.self=context;
  context.Renderer=function() {
    rendererCreations++;
    const keyboard = { kmap:Array(256).fill(false), mouse_ax:0, mouse_ay:0, mouse_btn:0, bind_all() {}, unbind_all() {},
      send_vkey(code, down) { const hid=codes[code]; if (hid) { this.kmap[hid]=down; calls.push({type:'key', code, hid, down}); } },
      mouse_send(rx, ry, x, y, buttons, wheel) { calls.push({type:'mouse', x, y, buttons, wheel}); },
    };
    native={ keyboard, decoder:new context.Worker(), worker:{ kbd_send(packet) { calls.push({type:'keyboard-release', packet}); }, close() {} } };
    return native;
  };
  context.RPViewer=function(canvasId, apiErrorCallback) {
    assert.equal(canvasId,'kvmCanvas');
    // 已对照受审 utility.js/RPViewer 原始正文：原厂会将 function.length===0 也判为空回调。
    const nativeIsNullOrEmpty=value=>value===null||value===undefined||value.length===0;
    assert.equal(nativeIsNullOrEmpty(apiErrorCallback),false,'Dell 原厂构造器拒绝零形参错误回调');
    assert.equal(typeof apiErrorCallback,'function');
    apiError=apiErrorCallback;
    native={};
    const api=(name, types, callback=()=>{})=>{native[name]=(...args)=>{
      assert.equal(args.length,types.length,`${name} 参数个数不符原厂 API`);
      types.forEach((type,index)=>assert.equal(typeof args[index],type,`${name} 参数类型不符原厂 API`));
      return callback(...args);
    };};
    for(const name of ['setRPDebugMode','setRPSecureConnectionAlways','setRPSupportReconnect','setRPMaintainAspectRatio',
      'setRPAllowSharingRequests','setRPMouseInputSupport','setRPKeyboardInputSupport','setRPFocus','activateRPVMedia']) api(name,['boolean']);
    api('activateRPVMedia',['boolean'],value=>{mediaActions.push({action:'activate',value});});
    api('setRPCredential',['string','string']); api('setRPServerConfiguration',['string','number']);
    api('setRPEmbeddedViewerSize',['number','number']); api('setRPVMPort',['number']);
    for(const name of ['disableRPCertPopup','connectRPViewer','closeRPViewerSession']) api(name,[]);
    api('registerRPLoginResponseCallback',['function'],callback=>{login=callback;});
    api('registerRPVMLoginResponseCallback',['function'],callback=>{mediaLogin=callback;});
    api('registerRPVMDeviceInfoUpdateCallback',['function'],callback=>{deviceInfo=callback;});
    api('registerRPVMDeviceStatusUpdateCallback',['function'],callback=>{deviceStatus=callback;});
    api('registerRPVMSessionDisconnectCallback',['function'],callback=>{mediaDisconnect=callback;});
    api('getKeyboardHandler',[],()=>({sendUSBKey(hid,down){calls.push({type:'key',hid,down});}}));
    api('getVMDeviceList',[],()=>deviceList);
    api('mapRPDevice',['number','object'],(id,file)=>{assert.ok(file instanceof FakeFile);media={id,file};mediaActions.push({action:'map',id,file});});
    api('unMapRPDevice',['number'],id=>{if(media?.id===id)media.ejected=true;mediaActions.push({action:'unmap',id});});
    return native;
  };
  context.RPViewer.RP_LOGIN_RESULT={LOGIN_SUCCESS:0};
  context.RPViewer.RP_MEDIA_REMOTE_TYPE={REMOTE_TYPE_CD_DVD:2};
  context.RPViewer.RP_MEDIA_REMOTE_STATUS={REMOTE_OK:0,REMOTE_STATUS_UNKNOWN:-1};
  context.RPViewer.RP_MEDIA_LOCAL_STATUS={LOCAL_OK:0,LOCAL_STATUS_UNKNOWN:-1};
  vm.createContext(context, {codeGeneration:{strings:false,wasm:false}});
  for (const script of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)) {
    if (/\bsrc\s*=/i.test(script[1])) continue;
    vm.runInContext(script[2], context, {timeout:2000});
  }
  if (vendor==='dell') { assert.equal(typeof boot, 'function'); boot(); }
  const dispatch=(type, event) => { for (const handler of listeners.get(type) || []) handler(event); };
  const send=(data, overrides={}) => dispatch('message', { origin:context.location.origin, source:parent, data:{sessionId:'9007199254740993',challenge:'first-generation',...data}, ...overrides });
  return { calls, messages, mediaActions, context, status, send, dispatch, createFile:(name='boot.iso',size=65536)=>new FakeFile(name,size),
    ready() { if (vendor==='ilo') native.onready(); else login(0); },
    acceptMedia() { if (vendor==='ilo') dispatch('oob-vm-status',{detail:'Inserted'}); else { deviceInfo(); deviceStatus(7,true,0,0); } },
    ejectMedia() { if (vendor==='ilo') dispatch('oob-vm-status',{detail:'Ejected'}); else deviceStatus(7,false,0,0); },
    media:()=>media, rendererCreations:()=>rendererCreations,
    deviceInfo(list) { deviceList=list; deviceInfo(); },
    deviceStatus(id,mapped,remote,local) { deviceStatus(id,mapped,remote,local); },
    consoleLogin(status) { login(status); },
    mediaLogin(status) { mediaLogin(status); },
    apiError(operation='activateRPVMedia',code=1) { apiError(operation,code); },
    mediaDisconnect() { mediaDisconnect(); },
    advance, timerIds:()=>[...timers.keys()], runLateTimer(id) { assert.equal(typeof timerHistory.get(id),'function');timerHistory.get(id)(); },
    resize() { context.innerWidth=1400; context.innerHeight=900; rectangle={left:200,top:75,width:1000,height:750,right:1200,bottom:825}; },
    rectangle:()=>rectangle,
    rendererNotifications() {
      for (const name of ['onhealthchange','onpowerchange','onlicensechange','onvmchange','onsizechange','onstatuschange']) {
        assert.equal(typeof native[name], 'function', `原生 renderer 直接通知 ${name} 必须可调用`);
        native[name](name==='onsizechange'?1280:1,720);
      }
    },
  };
}

function assertRelease(vendor, frame, message) {
  const release=frame.calls.findLast(call => vendor==='ilo' ? call.type==='mouse'&&call.buttons===0 : call.type==='pointerup'&&call.buttons===0);
  assert.ok(release, message);
  if (vendor==='ilo') { assert.ok(release.x>=0&&release.x<=3000); assert.ok(release.y>=0&&release.y<=3000); }
  else { const rect=frame.rectangle(); assert.ok(release.x>=rect.left&&release.x<=rect.right); assert.ok(release.y>=rect.top&&release.y<=rect.bottom); }
}

function probeEscapeAndInputRevocation(vendor, html) {
  const frame=createFrame(vendor,html);
  frame.send({type:'oob-console-handshake'});frame.ready();
  const key=(code,down)=>frame.send({type:'oob-console-input',event:{type:'key',code,down}});
  const pointer=buttons=>frame.send({type:'oob-console-input',event:{type:'pointer',x:.5,y:.5,buttons}});
  const enable=()=>frame.send({type:'oob-console-input-state',enabled:true});
  enable();key('ShiftLeft',true);pointer(1);frame.calls.length=0;frame.messages.length=0;
  // 屏幕键盘的 Esc 是远端输入；不得撤销键鼠，也不得释放仍按住的修饰键和鼠标。
  key('Escape',true);key('Escape',false);key('KeyA',true);key('KeyA',false);pointer(1);
  assert.deepEqual(frame.calls.filter(call=>call.type==='key').map(({hid,down})=>({hid,down})),[
    {hid:41,down:true},{hid:41,down:false},{hid:4,down:true},{hid:4,down:false},
  ],'Esc 后仍须允许连续按键，且不得擅自释放修饰键');
  assert.equal(frame.calls.some(call=>call.type==='keyboard-release'||call.type==='pointerup'||call.type==='mouse'&&call.buttons===0),false,'Esc 不得释放远端键鼠状态');
  assert.equal(frame.messages.some(message=>message.type==='oob-console-input-disabled'),false,'Esc 不得关闭键鼠连接');

  const revokeChecks=[['explicit-disable',()=>frame.send({type:'oob-console-input-state',enabled:false})],['blur',()=>frame.dispatch('blur',{})]];
  if(vendor==='ilo')revokeChecks.push(['hidden',()=>{frame.context.document.hidden=true;frame.dispatch('document:visibilitychange',{});}]);
  for(const [reason,revoke] of revokeChecks){
    enable();key('ShiftLeft',true);pointer(1);frame.calls.length=0;frame.messages.length=0;
    revoke();
    assertRelease(vendor,frame,`${reason} 仍须释放远端鼠标`);
    assert.ok(frame.calls.some(call=>vendor==='ilo'?call.type==='keyboard-release':call.type==='key'&&call.hid===225&&!call.down),`${reason} 仍须释放远端修饰键`);
    if(reason!=='explicit-disable')assert.ok(frame.messages.some(message=>message.type==='oob-console-input-disabled'),`${reason} 必须通知父页面撤销输入`);
    frame.calls.length=0;key('Escape',true);pointer(1);
    assert.equal(frame.calls.length,0,`${reason} 后不得继续发送键鼠`);
  }
}

function probeDellMedia(html) {
  const connected=()=>{const frame=createFrame('dell',html);frame.send({type:'oob-console-handshake'});frame.ready();return frame;};
  const action=(frame,name,extra={})=>frame.send({type:'oob-console-local-media',action:name,...extra});
  const latest=frame=>frame.messages.filter(message=>message.type==='oob-console-local-media-status').at(-1);
  const free=[{deviceId:7,deviceType:2,isMapped:false}];
  const confirmation=connected();
  action(confirmation,'insert',{file:confirmation.createFile()}); confirmation.deviceInfo(free);
  confirmation.deviceStatus(7,true,-1,0);
  assert.equal(latest(confirmation)?.status,'Connecting','本地File校验的remote UNKNOWN不能冒充BMC已插入');
  assert.equal(confirmation.messages.some(message=>message.status==='Inserted'),false);
  confirmation.deviceStatus(7,true,0,0); assert.equal(latest(confirmation)?.status,'Inserted');

  const initializing=connected();action(initializing,'insert',{file:initializing.createFile('initializing.iso')});initializing.deviceInfo([]);
  assert.equal(latest(initializing)?.status,'Connecting','AutoAttach 首次空目录应在固定5秒内等待原File，不能立即失败');
  initializing.advance(4000);initializing.deviceInfo([]);initializing.advance(999);
  initializing.deviceInfo(free);initializing.deviceInfo(free);
  assert.equal(initializing.mediaActions.filter(item=>item.action==='map').length,1,'5秒内出现空闲CD只映射一次');
  assert.equal(initializing.timerIds().length,0,'发出map前必须取消空目录期限定时器');
  initializing.advance(10000);assert.equal(initializing.messages.some(message=>message.status==='Failed'),false,'已发出的映射不能被旧等待期限误判失败');

  const expired=connected();action(expired,'insert',{file:expired.createFile('expired.iso')});expired.deviceInfo([]);
  expired.advance(4999);expired.deviceInfo([]);assert.equal(latest(expired)?.status,'Connecting');
  expired.advance(1);assert.equal(latest(expired)?.status,'Failed','重复空目录不得延长首次空目录起的固定5秒期限');
  assert.equal(expired.timerIds().length,0);expired.deviceInfo(free);
  assert.equal(expired.mediaActions.some(item=>item.action==='map'),false,'到期后迟到目录不得复活旧File');
  expired.deviceInfo([]);const fresh=expired.createFile('fresh.iso');action(expired,'insert',{file:fresh});
  expired.advance(4999);assert.equal(latest(expired)?.status,'Connecting','新File应获得独立的完整5秒期限');
  expired.deviceInfo(free);assert.equal(expired.media().file,fresh);

  const slow=connected();action(slow,'insert',{file:slow.createFile('slow-auth.iso')});
  slow.advance(15000);assert.equal(latest(slow)?.status,'Connecting','认证和首次目录返回前的网络耗时不得压缩空目录宽限');
  assert.equal(slow.timerIds().length,0,'仅首次空目录可启动5秒期限');slow.deviceInfo([]);slow.advance(4999);
  assert.equal(latest(slow)?.status,'Connecting');slow.deviceInfo(free);assert.equal(slow.mediaActions.filter(item=>item.action==='map').length,1);

  const queued=connected();action(queued,'insert',{file:queued.createFile('queued-old.iso')});queued.deviceInfo([]);
  const oldTimer=queued.timerIds()[0];assert.ok(oldTimer,'首次空目录必须启动固定期限');
  queued.advance(1000);action(queued,'eject');assert.equal(queued.timerIds().length,0,'取消必须清除等待定时器');
  const replacementFile=queued.createFile('queued-new.iso');action(queued,'insert',{file:replacementFile});
  queued.advance(3999);queued.runLateTimer(oldTimer);
  assert.equal(latest(queued)?.status,'Connecting','已清除但已排队的旧timer不得取消新的File意图');
  assert.equal(queued.timerIds().length,1,'旧timer不得清除新File的timer');
  queued.advance(1000);queued.deviceInfo(free);
  assert.equal(queued.media().file,replacementFile,'迟到旧timer之后仍只能映射新File');
  queued.advance(1);assert.equal(queued.messages.some(message=>message.status==='Failed'),false);

  const throttled=connected();action(throttled,'insert',{file:throttled.createFile()});throttled.deviceInfo([]);
  throttled.advance(5001,false);throttled.deviceInfo(free);
  assert.equal(latest(throttled)?.status,'Failed','浏览器timer被节流时，迟到目录也必须复核固定期限');
  assert.equal(throttled.mediaActions.some(item=>item.action==='map'),false);

  for(const failure of ['login','disconnect','api','console']) {
    const failed=connected();action(failed,'insert',{file:failed.createFile()});failed.deviceInfo([]);
    const timer=failed.timerIds()[0];
    if(failure==='login')failed.mediaLogin(1);else if(failure==='disconnect')failed.mediaDisconnect();else if(failure==='api')failed.apiError();else failed.consoleLogin(1);
    assert.equal(latest(failed)?.status,'Failed');assert.equal(failed.timerIds().length,0,'认证、原生调用失败或断线必须清除File等待timer');
    failed.runLateTimer(timer);failed.deviceInfo(free);assert.equal(failed.mediaActions.some(item=>item.action==='map'),false);
  }

  const unloading=connected();action(unloading,'insert',{file:unloading.createFile()});unloading.deviceInfo([]);
  const unloadTimer=unloading.timerIds()[0];unloading.dispatch('beforeunload',{});
  assert.equal(unloading.timerIds().length,0,'页面关闭必须清除File与等待timer');
  unloading.runLateTimer(unloadTimer);unloading.deviceInfo(free);assert.equal(unloading.mediaActions.some(item=>item.action==='map'),false);

  for(const devices of [[{deviceId:7,deviceType:2,isMapped:true}],[{deviceId:3,deviceType:1,isMapped:false}]]) {
    const blocked=connected();action(blocked,'insert',{file:blocked.createFile()});blocked.deviceInfo([]);blocked.deviceInfo(devices);
    assert.equal(latest(blocked)?.status,'Failed','没有空闲CD必须明确失败，不能无限pending');
    assert.equal(blocked.timerIds().length,0,'非空但没有空闲CD的明确失败必须清除等待timer');
    assert.equal(blocked.mediaActions.some(item=>item.action==='map'||item.action==='unmap'),false,'不得覆盖或弹出其他介质');
    blocked.deviceInfo(free);assert.equal(blocked.mediaActions.some(item=>item.action==='map'),false,'已失败意图不能由迟到目录自动重试');
  }
  const canceled=connected();action(canceled,'insert',{file:canceled.createFile('canceled.iso')});action(canceled,'eject');
  canceled.deviceInfo(free);
  assert.equal(canceled.mediaActions.some(item=>item.action==='map'||item.action==='unmap'),false,'未分配槽位的pending取消不得映射或发送卸载');
  assert.equal(canceled.mediaActions.some(item=>item.action==='activate'&&item.value===false),false,'activate(false)不是取消，不得借此启动新媒体连接');
  assert.equal(latest(canceled)?.status,'Ejected','取消尚未开始的本机映射必须回执');
  const nextFile=canceled.createFile('next.iso');action(canceled,'insert',{file:nextFile});
  assert.equal(canceled.mediaActions.filter(item=>item.action==='map').length,1,'新的显式选择才可映射');
  assert.equal(canceled.media().file,nextFile);

  const replaced=connected();action(replaced,'insert',{file:replaced.createFile('old.iso')});action(replaced,'eject');
  const replacement=replaced.createFile('replacement.iso');action(replaced,'insert',{file:replacement});
  assert.equal(replaced.mediaActions.some(item=>item.action==='map'),false,'介质目录未就绪时新选择也只能等待');
  replaced.deviceInfo(free);assert.equal(replaced.mediaActions.filter(item=>item.action==='map').length,1);
  assert.equal(replaced.media().file,replacement,'迟到目录只可使用新的显式File，不得复活旧文件');

  const ejecting=connected();action(ejecting,'insert',{file:ejecting.createFile()});ejecting.deviceInfo(free);
  action(ejecting,'eject');action(ejecting,'eject');
  assert.equal(ejecting.mediaActions.filter(item=>item.action==='unmap').length,0,'File异步校验前的取消不能向尚未映射的slot发unmap');
  ejecting.deviceStatus(7,true,-1,0);
  assert.equal(ejecting.mediaActions.filter(item=>item.action==='unmap').length,0,'本地UNKNOWN回调仍不允许向未确认slot发unmap');
  ejecting.deviceStatus(7,true,0,0);
  assert.equal(ejecting.mediaActions.filter(item=>item.action==='unmap').length,1,'已记录卸载意图须在BMC确认后仅发送一次unmap');
  ejecting.deviceStatus(7,true,0,0);
  assert.equal(ejecting.mediaActions.filter(item=>item.action==='unmap').length,1,'重复BMC确认不得重复卸载');
  assert.equal(latest(ejecting)?.status,'Connecting','卸载后的迟到插入确认不得复活Inserted');
  ejecting.deviceStatus(7,false,-1,0);
  assert.equal(latest(ejecting)?.status,'Connecting','本地卸载校验不能冒充BMC已卸载');
  ejecting.deviceStatus(7,false,0,0);assert.equal(latest(ejecting)?.status,'Ejected');
  ejecting.deviceInfo(free);assert.equal(ejecting.mediaActions.filter(item=>item.action==='map').length,1,'迟到目录不能重新插入已卸载文件');

  const disconnected=connected();action(disconnected,'insert',{file:disconnected.createFile()});disconnected.deviceInfo(free);
  disconnected.deviceStatus(7,true,-1,0);disconnected.mediaDisconnect();
  assert.equal(latest(disconnected)?.status,'Failed','实际介质连接中断只能报告失败，不能冒充已卸载');
  assert.equal(disconnected.messages.some(message=>message.status==='Inserted'||message.status==='Ejected'),false);
  disconnected.deviceInfo(free);assert.equal(disconnected.mediaActions.filter(item=>item.action==='map').length,1,'断线后的迟到目录不能自动重映射');

  const invalid=connected();action(invalid,'insert',{file:invalid.createFile('too-small.iso',1024)});invalid.deviceInfo(free);
  invalid.deviceStatus(7,false,-1,26);assert.equal(latest(invalid)?.status,'Failed','原厂LOCAL_SIZE_EMPTY必须失败');
  action(invalid,'eject');assert.equal(invalid.mediaActions.some(item=>item.action==='unmap'),false,'本地验证失败未映射，不能向候选slot发送unmap');
  action(invalid,'insert',{file:invalid.createFile('valid.iso')});assert.equal(invalid.mediaActions.filter(item=>item.action==='map').length,2,'本地验证失败后应允许新的显式File选择');
}

function probe(vendor, html) {
  probeEscapeAndInputRevocation(vendor,html);
  if(vendor==='dell'){
    const oldCallback=html.replace('(operation,errorCode)=>','()=>');
    assert.notEqual(oldCallback,html,'Dell 错误回调必须保留原厂要求的两个形参');
    assert.throws(()=>createFrame(vendor,oldCallback),/Dell 原厂构造器拒绝零形参错误回调/,'必须先重现旧回调失败');
    probeDellMedia(html);
  }
  const frame=createFrame(vendor, html);
  frame.send({type:'oob-console-handshake'});
  assert.equal(frame.messages.filter(item=>item.type==='oob-console-ready').length,0,'握手不能冒充视频就绪');
  frame.ready();
  if (vendor==='ilo') frame.rendererNotifications();
  assert.ok(frame.messages.some(item=>item.type==='oob-console-ready'&&item.challenge==='first-generation'));
  frame.calls.length=0;
  frame.send({type:'oob-console-input-state',enabled:true},{origin:'https://other.invalid'});
  frame.send({type:'oob-console-input',event:{type:'key',code:'KeyA',down:true}});
  assert.equal(frame.calls.length,0,'其他来源不得打开输入');
  frame.send({type:'oob-console-input-state',enabled:true});
  frame.calls.length=0;
  for (const [code,hid] of Object.entries(codes)) {
    const before=frame.calls.length;
    frame.send({type:'oob-console-input',event:{type:'key',code,down:true}});
    frame.send({type:'oob-console-input',event:{type:'key',code,down:false}});
    assert.equal(frame.calls.length,before+2,`${vendor} 未发送 ${code} 按下/释放`);
    assert.equal(frame.calls[before].hid,hid,`${vendor} ${code} HID 错误`);
    assert.equal(frame.calls[before].down,true); assert.equal(frame.calls[before+1].down,false);
  }
  frame.calls.length=0;
  frame.send({type:'oob-console-input',event:{type:'pointer',x:.5,y:.5,buttons:1,action:'down'}});
  frame.send({type:'oob-console-input',event:{type:'pointer',x:.01,y:.5,buttons:0,action:'up'}});
  assertRelease(vendor,frame,'拖入留黑区松键必须释放远端按钮');
  frame.calls.length=0;
  frame.send({type:'oob-console-input',event:{type:'pointer',x:.01,y:.5,buttons:1,action:'down'}});
  assert.equal(frame.calls.length,0,'留黑区不能开始新的按压');
  frame.send({type:'oob-console-input',event:{type:'pointer',x:.5,y:.5,buttons:1,action:'down'}});
  frame.calls.length=0;
  frame.resize();
  frame.send({type:'oob-console-input',event:{type:'release-all'}});
  assertRelease(vendor,frame,'改变全屏尺寸后仍须在当前画布坐标内释放');
  if (vendor==='dell') {
    for (const [buttons,button,which] of [[1,0,1],[2,2,3],[4,1,2]]) {
      frame.calls.length=0;
      frame.send({type:'oob-console-input',event:{type:'pointer',x:.5,y:.5,buttons,action:'down'}});
      assert.ok(frame.calls.some(item=>item.type==='pointerdown'&&item.button===button&&item.which===which),'左右中键必须提供原厂依赖的 which');
      frame.send({type:'oob-console-input',event:{type:'release-all'}});
    }
  }
  frame.send({type:'oob-console-input-state',enabled:false}); frame.calls.length=0;
  frame.send({type:'oob-console-input',event:{type:'key',code:'NumpadEnter',down:true}});
  frame.send({type:'oob-console-input',event:{type:'pointer',x:.5,y:.5,buttons:1,action:'down'}});
  assert.equal(frame.calls.length,0,'隐藏标签撤销输入后不得发键鼠');
  frame.send({type:'oob-console-handshake',challenge:'next-generation'}); frame.calls.length=0;
  frame.send({type:'oob-console-input-state',enabled:true});
  frame.send({type:'oob-console-input',challenge:'next-generation',event:{type:'key',code:'ContextMenu',down:true}});
  assert.equal(frame.calls.length,0,'旧 generation 不得重新打开输入');
  frame.messages.length=0;
  const file=frame.createFile();
  frame.send({type:'oob-console-local-media',challenge:'next-generation',action:'insert',file});
  assert.ok(frame.messages.some(item=>item.type==='oob-console-local-media-status'&&item.status==='Connecting'));
  assert.equal(frame.messages.some(item=>item.status==='Inserted'),false,'选择本机文件不能冒充设备已插入');
  frame.acceptMedia(); assert.equal(frame.media().file,file,'本机文件须保持 File 引用按需读取，不转为整文件正文');
  assert.ok(frame.messages.some(item=>item.type==='oob-console-local-media-status'&&item.status==='Inserted'));
  frame.send({type:'oob-console-local-media',challenge:'next-generation',action:'eject'});
  assert.ok(frame.media().ejected,'只有显式弹出才调用原生卸载');
  frame.ejectMedia(); assert.ok(frame.messages.some(item=>item.status==='Ejected'));
  if (vendor==='ilo') {
    const unsupported=createFrame(vendor,html,false);
    unsupported.send({type:'oob-console-handshake'});
    assert.equal(unsupported.rendererCreations(),0,'不支持原生 Worker 时不得启动 renderer');
    assert.ok(unsupported.messages.some(item=>item.type==='oob-console-error'&&item.message.includes('Worker')),'必须向父页面说明 Worker 阻断');
    assert.throws(()=>new unsupported.context.PseudoWorker(),/Worker/,'禁止原厂 eval 回退');
  }
  return {vendor,testedKeys:Object.keys(codes).length,checks:['source','challenge','keyboard','escape-keeps-input','input-revocation','drag-release','resize-release','inactive-input','local-iso',...(vendor==='ilo'?['required-worker']:['mouse-buttons'])]};
}

let html='';
process.stdin.setEncoding('utf8');
for await (const chunk of process.stdin) html+=chunk;
const vendor=process.argv[2];
assert.ok(vendor==='ilo'||vendor==='dell','必须指定 ilo 或 dell');
console.log(JSON.stringify(probe(vendor,html)));
