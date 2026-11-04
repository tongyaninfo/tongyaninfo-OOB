const readline = require('node:readline');
const mode = process.argv[2];
const lines = readline.createInterface({ input: process.stdin });
lines.on('line', line => {
  const message = JSON.parse(line);
  if (message.command === 'start') {
    if (mode === 'exit') return process.exit(7);
    if (message.launchSecret.length < 32) return process.exit(8);
    process.stdout.write('普通运行日志\n');
    process.stdout.write(`${JSON.stringify({ type: 'ready', url: mode === 'invalid' ? 'http://example.com:1234' : 'http://127.0.0.1:43123', instanceId: message.instanceId, initialized: false })}\n`);
  }
  if (message.command === 'shutdown') process.exit(0);
});
lines.on('close', () => process.exit(0));
