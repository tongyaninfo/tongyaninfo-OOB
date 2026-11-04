import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { randomBytes, randomUUID } from 'node:crypto';
import { createInterface } from 'node:readline';
import { parseReady, type HostReady } from './protocol';

export interface HostOptions {
  executable: string;
  arguments?: string[];
  dataDirectory: string;
  startupTimeoutMs?: number;
  shutdownTimeoutMs?: number;
  onUnexpectedExit?: () => void;
}

/** 秘密只经父子进程管道发送，不进入命令行、URL、文件或诊断输出。 */
export class HostProcess {
  readonly launchSecret = randomBytes(32).toString('base64url');
  readonly instanceId = randomUUID();
  private child?: ChildProcessWithoutNullStreams;
  private stopping = false;
  private exit?: Promise<void>;
  private ready?: HostReady;

  constructor(private readonly options: HostOptions) {}

  async start(): Promise<HostReady> {
    if (this.child) throw new Error('本地服务已经启动');
    const child = spawn(this.options.executable,
      [...(this.options.arguments ?? []), '--desktop-data', this.options.dataDirectory],
      { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    this.child = child;
    // spawn 失败可能只有 error/close 而没有 exit，等待 close 才能覆盖该路径。
    this.exit = new Promise(resolve => child.once('close', () => resolve()));
    // 持续消费 stderr，但不打印可能包含连接、票据或请求正文的第三方日志。
    child.stderr.on('data', () => undefined);
    child.stdin.on('error', () => undefined);
    try {
      this.ready = await new Promise<HostReady>((resolve, reject) => {
        const lines = createInterface({ input: child.stdout, crlfDelay: Infinity });
        const timeout = setTimeout(() => finish(new Error('本地服务启动超时')), this.options.startupTimeoutMs ?? 60000);
        let settled = false;
        const finish = (error?: Error, ready?: HostReady) => {
          if (settled) return;
          settled = true;
          clearTimeout(timeout);
          // 不关闭 readline，继续排空日志，防止子进程因 stdout 管道积压阻塞。
          if (error) reject(error); else resolve(ready!);
        };
        child.once('error', () => finish(new Error('无法启动本地服务，请检查安装文件')));
        child.once('exit', () => {
          if (!settled) finish(new Error('本地服务在就绪前结束'));
          else if (!this.stopping) this.options.onUnexpectedExit?.();
        });
        lines.on('line', line => {
          if (settled || line.length > 16384 || !line.startsWith('{')) return;
          try {
            const value: unknown = JSON.parse(line);
            const ready = parseReady(value, this.instanceId);
            if (ready) finish(undefined, ready);
          } catch { /* 普通运行日志不是控制协议，不能影响启动状态。 */ }
        });
        child.stdin.write(`${JSON.stringify({ command: 'start', launchSecret: this.launchSecret, instanceId: this.instanceId })}\n`);
      });
      return this.ready;
    } catch (error) {
      await this.stop();
      throw error;
    }
  }

  async stop(): Promise<void> {
    const child = this.child;
    if (!child || this.stopping) return this.exit;
    this.stopping = true;
    if (child.exitCode !== null || child.signalCode !== null) return;
    if (child.stdin.writable) child.stdin.write(`${JSON.stringify({ command: 'shutdown' })}\n`);
    let timer: NodeJS.Timeout | undefined;
    const graceful = await Promise.race([
      this.exit!.then(() => true),
      new Promise<false>(resolve => { timer = setTimeout(() => resolve(false), this.options.shutdownTimeoutMs ?? 15000); }),
    ]);
    if (timer) clearTimeout(timer);
    // 只终止本对象直接启动且尚未退出的子进程；不按名称或端口停止其它进程。
    if (!graceful && child.pid && child.exitCode === null && child.signalCode === null) {
      child.kill('SIGTERM');
      let forceTimer: NodeJS.Timeout | undefined;
      await Promise.race([
        this.exit,
        new Promise<void>(resolve => {
          forceTimer = setTimeout(() => {
            if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
            resolve();
          }, 5000);
        }),
      ]);
      if (forceTimer) clearTimeout(forceTimer);
    }
  }
}
