namespace OobManagement.Infrastructure.Service.impl;

/// <summary>自有最小画布外壳；仅调用已取证 renderer/HID/FileReader，不嵌入原厂管理站点。</summary>
internal static class ConsoleIlo4Frame
{
    public static OobManagement.Application.Models.ConsoleResource CreateResource()
    {
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18));
        var html = Html.Replace("<script", $"<script nonce=\"{nonce}\"", StringComparison.Ordinal);
        return new(System.Text.Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", ScriptNonce: nonce);
    }

    public const string WebSocketShim = """
        (() => {
          const Original = self.WebSocket;
          self.WebSocket = class extends Original {
            constructor(address, protocols) {
              const requested = new URL(address, self.location.href);
              if (!['/wss/ircport','/wss/vmport'].includes(requested.pathname)) throw new Error('Unsupported console stream');
              const current = new URL(self.location.href);
              const prefix = current.pathname.split('/frame/')[0];
              const gateway = new URL(prefix + '/stream' + requested.pathname, current.origin);
              gateway.protocol = current.protocol === 'https:' ? 'wss:' : 'ws:';
              if (protocols === undefined) super(gateway.href); else super(gateway.href, protocols);
              if (typeof document !== 'undefined' && requested.pathname === '/wss/vmport') {
                this.addEventListener('message', event => {
                  if (event.data instanceof ArrayBuffer && event.data.byteLength === 4) {
                    const status = new Uint8Array(event.data);
                    const accepted=status[0] === 32 && status[1] === 0;
                    window.dispatchEvent(new CustomEvent('oob-vm-status', {detail:accepted ? 'Inserted' : 'Failed'}));
                    if(!accepted)this.close();
                  }
                });
                this.addEventListener('close', () => window.dispatchEvent(new CustomEvent('oob-vm-status', {detail:'Ejected'})));
                this.addEventListener('error', () => window.dispatchEvent(new CustomEvent('oob-vm-status', {detail:'Failed'})));
              }
            }
          };
        })();
        """;

    public static string Html => """
        <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>iLO4 统一虚拟控制台</title><style>html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#101318;color:#dce4ef;font:14px system-ui}body{display:flex;align-items:center;justify-content:center}canvas{max-width:100%;max-height:100%;object-fit:contain;outline:none}#status{position:fixed;left:10px;bottom:8px;background:#0009;padding:4px 8px;pointer-events:none}</style>
        </head><body><canvas id="oob-screen" width="1024" height="768" tabindex="0"></canvas><div id="status">正在建立原生视频通道…</div>
        <script>
        """ + WebSocketShim + """
        const sessionId=decodeURIComponent(location.pathname.match(/console-sessions\/([^/]+)\/frame\//)[1]);
        const parentOrigin=location.origin;
        let challenge=null;
        function publish(type,extra={}){if(challenge)parent.postMessage({type,sessionId,challenge,...extra},parentOrigin);}
        function status(message){document.getElementById('status').textContent=String(message);publish('oob-console-status',{message:String(message)});}
        const iLOGlobal={ie:false};
        const iLO={getCookie:()=>null,setCookie:()=>{},isFunction:value=>typeof value==='function',getBaseUrl:()=>location.origin+'/',translateString:(key,fallback)=>fallback||key,
          confirm:(_,callback)=>{if(callback)callback(false);},alert:value=>{const message=String(value?.message||value?.messageKey||'原生控制台拒绝操作').replace(/<[^>]*>/g,'');status(message);publish('oob-console-error',{message});}};
        const myIcons={critical:'error'};
        function showIrcToast(value){status(value?.text||value?.textKey||'控制台状态已更新');}
        </script><script src="../js/jquery.js"></script><script>$.log=()=>{};</script>
        <script src="../js/pseudo-worker.js"></script><script src="../js/ircKeyboardMouse.js"></script><script src="../js/renderer.js"></script><script src="../js/VMconnection.js"></script>
        <script>
        let renderer=null,inputEnabled=false,ready=false,media=null,expectedEject=false,failure=null;
        const canvas=document.getElementById('oob-screen');
        const workerFailure='浏览器无法使用原生 Worker，控制台已阻断；请启用 Worker 或使用支持的现代浏览器。';
        // 禁止原厂 PseudoWorker 的 eval 回退，不能通过放宽 CSP 换取连接成功。
        window.PseudoWorker=function(){throw new Error(workerFailure);};
        function release(){if(!renderer)return;renderer.keyboard.kmap.fill(false);renderer.worker.kbd_send([0,0,0,0,0,0,0,0]);renderer.keyboard.mouse_btn=0;renderer.keyboard.mouse_send(0,0,renderer.keyboard.mouse_ax,renderer.keyboard.mouse_ay,0,0);}
        function sendKey(code,down){if(ready&&inputEnabled&&typeof code==='string'&&code.length<40)renderer.keyboard.send_vkey(code,!!down);}
        function fail(error){const detail=error instanceof Error?error.message:String(error);failure=/Worker|SecurityError/.test(detail)?workerFailure:detail;ready=false;inputEnabled=false;status(failure);publish('oob-console-error',{message:failure});}
        try {
          if(typeof Worker!=='function')throw new Error(workerFailure);
          renderer=new Renderer({version:4,canvasId:'oob-screen',session:'00000000000000000000000000000000',host:location.host,openCommand:0});
          if(!(renderer.decoder instanceof Worker))throw new Error(workerFailure);
          // 原生自动绑定会越过标签输入开关，改为下方统一受控监听。
          renderer.keyboard.bind_all=()=>{};renderer.keyboard.unbind_all();
          renderer.onready=()=>{ready=true;status('视频已连接，键盘鼠标默认关闭');publish('oob-console-ready',{bridgeProtocol:'OobConsoleV1'});};
          renderer.onclose=()=>{ready=false;inputEnabled=false;status('视频连接已关闭');publish('oob-console-error',{message:'视频连接已关闭'});};
          renderer.onstatuschange=value=>status(String(value).replace(/<[^>]*>/g,''));
          // 原生 decoder 会直接调用这些通知；只接收信息，不能从通知推进电源或介质动作。
          renderer.onpowerchange=()=>{};renderer.onhealthchange=()=>{};renderer.onlicensechange=()=>{};renderer.onvmchange=()=>{};renderer.onvmactivity=()=>{};
          // Renderer.set_size 已先更新 canvas 属性和绘图缓冲，本层不重复缩放，避免坐标二次转换。
          renderer.onsizechange=()=>{};
          renderer.confirm=value=>{renderer.worker.call_func(value.callback,false);status('控制台已被占用，未抢占他人会话');};
        } catch(error){fail(error);}
        canvas.addEventListener('keydown',event=>{if(inputEnabled){sendKey(event.code,true);event.preventDefault();}});
        canvas.addEventListener('keyup',event=>{if(inputEnabled){sendKey(event.code,false);event.preventDefault();}});
        canvas.addEventListener('pointerdown',event=>{canvas.focus();if(inputEnabled){renderer.keyboard.on_mouse_button({...event,type:'mousedown',button:event.button});event.preventDefault();}});
        canvas.addEventListener('pointerup',event=>{if(inputEnabled){renderer.keyboard.on_mouse_button({type:'mouseup',button:event.button});event.preventDefault();}});
        canvas.addEventListener('pointermove',event=>{if(ready&&inputEnabled)renderer.keyboard.on_mouse_move(event);});
        canvas.addEventListener('wheel',event=>{if(ready&&inputEnabled){renderer.keyboard.on_mouse_wheel(event);event.preventDefault();}},{passive:false});
        canvas.addEventListener('contextmenu',event=>event.preventDefault());
        window.addEventListener('blur',()=>{release();inputEnabled=false;publish('oob-console-input-disabled');});
        document.addEventListener('visibilitychange',()=>{if(document.hidden){release();inputEnabled=false;publish('oob-console-input-disabled');}});
        window.addEventListener('oob-vm-status',event=>{
          let state=event.detail;
          if(state==='Ejected'&&!expectedEject)state='Failed';
          if(state==='Ejected'||state==='Failed'){if(media)media.file=null;media=null;vm_conn[0]=undefined;}
          publish('oob-console-local-media-status',{status:state,message:state==='Failed'?'本机介质通道被拒绝或已中断':undefined});
        });
        window.addEventListener('message',event=>{
          if(event.origin!==parentOrigin||event.source!==parent||event.data?.sessionId!==sessionId)return;
          const message=event.data;
          if(message.type==='oob-console-handshake'&&typeof message.challenge==='string'&&message.challenge.length<=100){release();inputEnabled=false;challenge=message.challenge;if(failure)publish('oob-console-error',{message:failure});else if(ready)publish('oob-console-ready',{bridgeProtocol:'OobConsoleV1'});return;}
          if(!challenge||message.challenge!==challenge)return;
          if(message.type==='oob-console-input-state'){if(!message.enabled)release();inputEnabled=ready&&message.enabled===true;return;}
          if(message.type==='oob-console-input'){
            const input=message.event;
            if(input?.type==='release-all'){release();return;}
            if(!ready||!inputEnabled)return;
            if(input?.type==='key')sendKey(input.code,input.down);
            else if(input?.type==='pointer'&&Number.isFinite(input.x)&&Number.isFinite(input.y)){
              const bounds=canvas.getBoundingClientRect(),px=input.x*innerWidth-bounds.left,py=input.y*innerHeight-bounds.top,held=renderer.keyboard.mouse_btn;
              let buttons=(Number(input.buttons)||0)&7;
              // 留黑区不开始新按压；已按住的拖拽仍钳制到画布，越界松键必须送到远端。
              if(px<0||py<0||px>bounds.width||py>bounds.height){if(!held)return;buttons&=held;}
              if(bounds.width<=0||bounds.height<=0){if(held&&!buttons)release();return;}
              const ax=Math.max(0,Math.min(3000,Math.round(px/bounds.width*3000))),ay=Math.max(0,Math.min(3000,Math.round(py/bounds.height*3000)));
              renderer.keyboard.mouse_ax=ax;renderer.keyboard.mouse_ay=ay;renderer.keyboard.mouse_btn=buttons;renderer.keyboard.mouse_send(0,0,ax,ay,buttons,0);
            } else if(input?.type==='wheel'&&Number.isFinite(input.deltaY))renderer.keyboard.mouse_send(0,0,renderer.keyboard.mouse_ax,renderer.keyboard.mouse_ay,renderer.keyboard.mouse_btn,Math.max(-127,Math.min(127,Math.round(input.deltaY))));
          }
          if(message.type==='oob-console-local-media'){
            if(message.action==='insert'){
              if(!ready||media||!(message.file instanceof File)||!message.file.name.toLowerCase().endsWith('.iso')||message.file.size<=0){publish('oob-console-local-media-status',{status:'Failed',message:'请连接视频并选择有效ISO，已有映射须先弹出'});return;}
              try{expectedEject=false;media=new VMconnection(vmConnType.CDROM,0);vm_conn[0]=media;media.setFile(message.file);media.setMedia_sz(message.file.size);publish('oob-console-local-media-status',{status:'Connecting'});media.createWebSocket();}catch(error){fail(error);publish('oob-console-local-media-status',{status:'Failed'});}
            }else if(message.action==='eject'&&media){expectedEject=true;media.closeVMconnection();}
          }
        });
        window.addEventListener('beforeunload',()=>{release();if(media?.getWebsocket())media.getWebsocket().close();renderer?.worker.close();});
        </script></body></html>
        """;
}
