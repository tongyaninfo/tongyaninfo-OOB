namespace OobManagement.Infrastructure.Service.impl;

/// <summary>受控原生画布；只实现约定的输入/媒体桥接，不加载厂商整站、电源或设置页面。</summary>
internal static class ConsoleDellFrame
{
    public static OobManagement.Application.Models.ConsoleResource CreateResource()
    {
        // 每次响应独立 nonce，与网关严格 CSP 一致；仅允许本次已审查外壳脚本执行。
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18));
        var html = Html.Replace("<script", $"<script nonce=\"{nonce}\"", StringComparison.Ordinal);
        return new(System.Text.Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", ScriptNonce: nonce);
    }

    public const string MediaWorkerShim = """
        (()=>{const Native=globalThis.WebSocket;const marker=globalThis.location.pathname.indexOf('/frame/');
        const root=globalThis.location.pathname.slice(0,marker);const address=(globalThis.location.protocol==='https:'?'wss:':'ws:')+'//'+globalThis.location.host+root+'/stream/media';
        class GatewaySocket extends Native{constructor(url,protocols){super(address,protocols)}}globalThis.WebSocket=GatewaySocket;})();
        """;

    public const string Html = """
        <!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <style>html,body,#viewer{margin:0;width:100%;height:100%;overflow:hidden;background:#000}#viewer{position:relative}canvas{outline:0}#notice{position:absolute;left:12px;top:10px;color:#ddd;font:14px sans-serif;pointer-events:none}</style>
        <div id="viewer"><canvas id="kvmCanvas" tabindex="-1"></canvas><div id="notice">正在连接 iDRAC8 原生控制台…</div></div>
        <script>
        (()=>{const Native=window.WebSocket;const marker=location.pathname.indexOf('/frame/');const root=location.pathname.slice(0,marker);
        const address=(location.protocol==='https:'?'wss:':'ws:')+'//'+location.host+root+'/stream/kvm';
        class GatewaySocket extends Native{constructor(url,protocols){super(address,protocols)}}window.WebSocket=GatewaySocket;})();
        </script><script src="../js/ViewerJS/viewer/rpviewer.js"></script><script>
        (()=>{'use strict';const sessionId=location.pathname.split('/console-sessions/')[1].split('/')[0];
        const canvas=document.getElementById('kvmCanvas');const notice=document.getElementById('notice');let viewer,ready=false,enabled=false,challenge=null,mediaActive=false,mediaDevicesKnown=false,pendingFile=null,pendingTimer=null,pendingDeadline=null,pendingGeneration=0,mappedId=null,mappedConfirmed=false,ejecting=false,unmapSent=false,lastButtons=0,lastX=.5,lastY=.5;const pressed=new Set();
        const tell=(type,extra={})=>{if(challenge)parent.postMessage({type,sessionId,challenge,...extra},location.origin)};
        const reportReady=()=>{if(ready)tell('oob-console-ready',{bridgeProtocol:'OobConsoleV1'})};
        const mediaStatus=(status,message)=>tell('oob-console-local-media-status',{status,message});
        const hid=code=>{if(/^Key[A-Z]$/.test(code))return code.charCodeAt(3)-61;if(/^Digit[1-9]$/.test(code))return 29+Number(code[5]);if(code==='Digit0')return 39;if(/^Numpad[1-9]$/.test(code))return 88+Number(code[6]);if(/^F([1-9]|1[0-2])$/.test(code))return 57+Number(code.slice(1));return ({Enter:40,Escape:41,Backspace:42,Tab:43,Space:44,Minus:45,Equal:46,BracketLeft:47,BracketRight:48,Backslash:49,Semicolon:51,Quote:52,Backquote:53,Comma:54,Period:55,Slash:56,CapsLock:57,PrintScreen:70,ScrollLock:71,Pause:72,Insert:73,Home:74,PageUp:75,Delete:76,End:77,PageDown:78,ArrowRight:79,ArrowLeft:80,ArrowDown:81,ArrowUp:82,NumLock:83,NumpadDivide:84,NumpadMultiply:85,NumpadSubtract:86,NumpadAdd:87,NumpadEnter:88,Numpad0:98,NumpadDecimal:99,IntlBackslash:100,ContextMenu:101,NumpadEqual:103,ControlLeft:224,ShiftLeft:225,AltLeft:226,MetaLeft:227,OSLeft:227,ControlRight:228,ShiftRight:229,AltRight:230,MetaRight:231,OSRight:231})[code]};
        function key(code,down){const value=hid(code);if(!value||!viewer)return;const handler=viewer.getKeyboardHandler();if(!handler)return;handler.sendUSBKey(value,down);if(down)pressed.add(code);else pressed.delete(code)}
        function pointer(e,force=false){if(!viewer||(!enabled&&!force))return;const rect=canvas.getBoundingClientRect();if(rect.width<=0||rect.height<=0)return;
        let buttons=Number.isInteger(e.buttons)?e.buttons&7:0;const rawX=e.x*innerWidth,rawY=e.y*innerHeight;
        const inside=Number.isFinite(rawX)&&Number.isFinite(rawY)&&rawX>=rect.left&&rawX<=rect.right&&rawY>=rect.top&&rawY<=rect.bottom;
        // 留黑区不开始新按压；已有拖动的松开必须送达，缩放后用当前画布矩形还原最后合法位置。
        if(!force&&!inside&&!lastButtons)return;if(!inside)buttons&=lastButtons;
        const releasing=(lastButtons&~buttons)!==0;
        let x=force||!inside&&releasing?rect.left+lastX*rect.width:rawX;
        let y=force||!inside&&releasing?rect.top+lastY*rect.height:rawY;
        if(!Number.isFinite(x)||!Number.isFinite(y)){x=rect.left+lastX*rect.width;y=rect.top+lastY*rect.height}
        x=Math.min(rect.right,Math.max(rect.left,x));y=Math.min(rect.bottom,Math.max(rect.top,y));
        const changed=lastButtons^buttons;for(const [mask,button] of [[1,0],[2,2],[4,1]])if(changed&mask){
        const event=new PointerEvent((buttons&mask)?'pointerdown':'pointerup',{bubbles:true,cancelable:true,pointerType:'mouse',clientX:x,clientY:y,button,buttons});Object.defineProperty(event,'which',{value:button+1});canvas.dispatchEvent(event)}
        canvas.dispatchEvent(new PointerEvent('pointermove',{bubbles:true,cancelable:true,pointerType:'mouse',clientX:x,clientY:y,buttons}));lastButtons=buttons;lastX=(x-rect.left)/rect.width;lastY=(y-rect.top)/rect.height;}
        function release(){for(const code of [...pressed])key(code,false);if(lastButtons)pointer({buttons:0},true)}
        function clearPending(){if(pendingTimer!==null)clearTimeout(pendingTimer);pendingTimer=null;pendingDeadline=null;pendingFile=null;pendingGeneration++;}
        function expirePending(generation){if(!pendingFile||generation!==pendingGeneration)return;clearPending();mediaStatus('Failed','等待设备初始化虚拟光驱目录超时，未发送映射；请确认设备状态后重新选择镜像');}
        function insert(file){if(!(file instanceof File)||!/\.iso$/i.test(file.name)||file.size===0){mediaStatus('Failed','当前 iDRAC8 原生桥接只接受非空只读 ISO 镜像');return}if(mappedId!==null||pendingFile||ejecting){mediaStatus('Failed','请先弹出当前本地镜像');return}pendingFile=file;pendingGeneration++;mediaStatus('Connecting','正在建立原生虚拟光驱连接');if(!mediaActive){mediaActive=true;mediaDevicesKnown=false;viewer.setRPVMPort(443);viewer.activateRPVMedia(true)}else mapPending();}
        function mapPending(){
          if(!pendingFile||!viewer||!mediaDevicesKnown)return;
          // 浏览器后台定时器可能被节流；目录回调也核对单调期限，过期意图绝不恢复。
          if(pendingDeadline!==null&&performance.now()>=pendingDeadline){expirePending(pendingGeneration);return;}
          const list=viewer.getVMDeviceList();
          if(Array.isArray(list)&&list.length===0){
            // 原厂AutoAttach可能先返回空目录；首次空目录宽限5秒，后续回调不续期，也不挤占认证耗时。
            if(pendingDeadline===null){pendingDeadline=performance.now()+5000;const generation=pendingGeneration;pendingTimer=setTimeout(()=>expirePending(generation),5000);}
            mediaStatus('Connecting','正在等待设备初始化虚拟光驱目录（最多5秒）');return;
          }
          const disk=Array.isArray(list)?list.find(d=>d.deviceType===RPViewer.RP_MEDIA_REMOTE_TYPE.REMOTE_TYPE_CD_DVD&&!d.isMapped):null;
          // 非空目录没有空闲CD时立即终止；不得覆盖他人挂载，后续目录更新不能重试本次File。
          if(!disk){clearPending();mediaStatus('Failed','设备没有空闲虚拟光驱，未覆盖或弹出现有介质');return;}
          const file=pendingFile;clearPending();mappedId=disk.deviceId;mappedConfirmed=false;unmapSent=false;viewer.mapRPDevice(mappedId,file);
        }
        function sendConfirmedEject(){if(mappedId!==null&&mappedConfirmed&&ejecting&&!unmapSent){unmapSent=true;viewer.unMapRPDevice(mappedId);}}
        function eject(){
          // 尚未选择设备槽位时只取消本机等待，不向设备发unmap；迟到目录回调也找不到旧File。
          if(pendingFile){clearPending();mediaStatus('Ejected','已取消尚未开始的本机映射，未向设备发送挂载或卸载');return;}
          if(mappedId===null||ejecting)return;
          ejecting=true;mediaStatus('Connecting','正在等待设备确认卸载');
          // mapRPDevice先做异步File校验，候选id不代表远端已映射；确认前只保留本次卸载意图。
          sendConfirmedEject();
        }
        window.addEventListener('message',event=>{const m=event.data;if(event.source!==parent||event.origin!==location.origin||!m||m.sessionId!==sessionId)return;
        if(m.type==='oob-console-handshake'&&typeof m.challenge==='string'){enabled=false;release();challenge=m.challenge;reportReady();return}if(!challenge||m.challenge!==challenge)return;
        if(m.type==='oob-console-input-state'){enabled=m.enabled===true;if(!enabled)release();return}if(m.type==='oob-console-local-media'&&ready){if(m.action==='insert')insert(m.file);else if(m.action==='eject')eject();return}
        if(m.type!=='oob-console-input'||!ready)return;const e=m.event;if(!e)return;if(e.type==='release-all'){release();return}if(!enabled)return;
        if(e.type==='key')key(e.code,e.down===true);else if(e.type==='pointer')pointer(e);else if(e.type==='wheel'){const rect=canvas.getBoundingClientRect(),wheel=new MouseEvent('mousewheel',{bubbles:true,cancelable:true,clientX:rect.left+rect.width/2,clientY:rect.top+rect.height/2});Object.defineProperty(wheel,'wheelDelta',{value:-Math.sign(e.deltaY)*120});canvas.dispatchEvent(wheel)}});
        window.addEventListener('blur',()=>{release();enabled=false;tell('oob-console-input-disabled')});window.addEventListener('beforeunload',()=>{clearPending();release();try{viewer?.closeRPViewerSession()}catch{}});
        let attempts=0;const boot=setInterval(()=>{if(++attempts>200){clearInterval(boot);notice.textContent='原生控制台资源加载失败';return}if(typeof RPViewer!=='function'||typeof Util==='undefined'||typeof PacketHandler==='undefined'||typeof Logger==='undefined'||typeof MediaWorkerHandler==='undefined')return;clearInterval(boot);
        // 原厂将 function.length===0 判为空回调，必须保留操作名、错误码两个形参。
        viewer=new RPViewer('kvmCanvas',(operation,errorCode)=>{notice.textContent='原生控制台调用失败（'+errorCode+'）';if(pendingFile){clearPending();mediaStatus('Failed','原生虚拟介质调用失败（'+errorCode+'）')}});viewer.setRPDebugMode(false);viewer.setRPCredential('oob-console-user','oob-console-pass');viewer.setRPServerConfiguration(location.hostname,443);viewer.disableRPCertPopup();viewer.setRPSecureConnectionAlways(true);viewer.setRPSupportReconnect(false);viewer.setRPMaintainAspectRatio(true);viewer.setRPAllowSharingRequests(false);viewer.setRPMouseInputSupport(true);viewer.setRPKeyboardInputSupport(true);viewer.setRPFocus(true);viewer.setRPEmbeddedViewerSize(innerWidth,innerHeight);
        viewer.registerRPLoginResponseCallback(status=>{if(status===RPViewer.RP_LOGIN_RESULT.LOGIN_SUCCESS){ready=true;notice.textContent='';reportReady()}else{ready=false;if(pendingFile){clearPending();mediaStatus('Failed','控制台连接失败，已取消尚未开始的本机映射')}notice.textContent='控制台登录失败或已被占用'}});
        viewer.registerRPVMLoginResponseCallback(status=>{if(status!==RPViewer.RP_LOGIN_RESULT.LOGIN_SUCCESS){const waiting=pendingFile||mappedId!==null;clearPending();mediaActive=false;mediaDevicesKnown=false;mappedConfirmed=false;ejecting=false;unmapSent=false;if(waiting)mediaStatus('Failed','虚拟介质认证失败或设备已被占用')}});
        viewer.registerRPVMDeviceInfoUpdateCallback(()=>{if(!mediaActive)return;mediaDevicesKnown=true;mapPending();});
        viewer.registerRPVMDeviceStatusUpdateCallback((id,mapped,remote,local)=>{
          if(id!==mappedId)return;
          const remoteUnknown=remote===RPViewer.RP_MEDIA_REMOTE_STATUS.REMOTE_STATUS_UNKNOWN,localUnknown=local===RPViewer.RP_MEDIA_LOCAL_STATUS.LOCAL_STATUS_UNKNOWN;
          const remoteOk=remote===RPViewer.RP_MEDIA_REMOTE_STATUS.REMOTE_OK,localOk=local===RPViewer.RP_MEDIA_LOCAL_STATUS.LOCAL_OK;
          if((!remoteOk&&!remoteUnknown)||(!localOk&&!localUnknown)){
            clearPending();
            // 原生本地File校验失败且尚无远端状态时未产生设备映射，可接受新的显式选择。
            if(remoteUnknown&&!mapped&&!localUnknown){mappedId=null;mappedConfirmed=false;ejecting=false;unmapSent=false;}
            mediaStatus('Failed','设备拒绝虚拟介质映射');return;
          }
          // File校验先回mapped=true/remote=UNKNOWN，必须等待实际BMC状态，不能提前宣告插入/卸载。
          if(remoteUnknown||localUnknown){mediaStatus('Connecting',ejecting?'正在等待设备确认卸载':'本机文件已校验，正在等待设备确认映射');return;}
          if(mapped){mappedConfirmed=true;mediaStatus(ejecting?'Connecting':'Inserted',ejecting?'正在等待设备确认卸载':'本地 ISO 已由设备确认映射');sendConfirmedEject();}
          else{mappedId=null;mappedConfirmed=false;ejecting=false;unmapSent=false;mediaStatus('Ejected','本地 ISO 已由设备确认弹出');}
        });
        viewer.registerRPVMSessionDisconnectCallback(()=>{const waiting=pendingFile||mappedId!==null;clearPending();mappedId=null;mediaActive=false;mediaDevicesKnown=false;mappedConfirmed=false;ejecting=false;unmapSent=false;if(waiting)mediaStatus('Failed','本机虚拟介质通道已断开，设备状态未确认')});
        viewer.connectRPViewer();window.addEventListener('resize',()=>viewer.setRPEmbeddedViewerSize(innerWidth,innerHeight));},50);
        })();</script></html>
        """;
}
