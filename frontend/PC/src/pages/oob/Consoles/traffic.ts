export type TrafficPoint = {
  at: number;
  received: number | null;
  sent: number | null;
  receiveRate: number | null;
  sendRate: number | null;
};
/** 只用同一会话的累计字节差计算速率；计数回退、不可用与断线均留下缺口。 */
export function appendTraffic(
  previous: TrafficPoint[],
  at: number,
  received: number | null,
  sent: number | null,
): TrafficPoint[] {
  const last = previous.at(-1);
  const seconds = last ? (at - last.at) / 1000 : 0;
  const rate = (value: number | null, earlier: number | null | undefined) =>
    value !== null &&
    earlier !== null &&
    earlier !== undefined &&
    seconds > 0 &&
    seconds <= 15 &&
    value >= earlier
      ? (value - earlier) / seconds
      : null;
  return [
    ...previous,
    {
      at,
      received,
      sent,
      receiveRate: rate(received, last?.received),
      sendRate: rate(sent, last?.sent),
    },
  ]
    .filter((point) => point.at >= at - 60000)
    .slice(-61);
}
export function formatBytes(value: number | null | undefined, rate = false) {
  if (value === null || value === undefined || !Number.isFinite(value))
    return '—';
  let scaled = Math.max(0, value);
  let unit = 0;
  while (scaled >= 1024 && unit < 4) {
    scaled /= 1024;
    unit++;
  }
  return `${scaled.toFixed(unit ? 1 : 0)} ${['B', 'KiB', 'MiB', 'GiB', 'TiB'][unit]}${rate ? '/s' : ''}`;
}
