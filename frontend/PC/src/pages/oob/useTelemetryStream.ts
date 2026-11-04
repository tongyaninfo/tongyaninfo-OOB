import { useEffect, useRef, useState } from 'react';
import type { ServerTelemetryDto } from '@/services/oob/models';
import {
  type LiveConnectionState,
  subscribeTelemetry,
} from '@/services/oob/realtime';
import type { ConnectionGap } from './Overview/recent';

/** 订阅的生命周期属于当前页面，重连回调仅改状态，不请求快照或历史。 */
export function useTelemetryStream(
  onTelemetry: (snapshot: ServerTelemetryDto) => void,
) {
  const receive = useRef(onTelemetry);
  receive.current = onTelemetry;
  const [state, setState] = useState<LiveConnectionState>('connecting');
  const [gaps, setGaps] = useState<ConnectionGap[]>([]);
  useEffect(() => {
    let gapStart: number | undefined;
    return subscribeTelemetry(
      (snapshot) => receive.current(snapshot),
      (next) => {
        const now = Date.now();
        setState(next);
        if (next !== 'connected' && gapStart === undefined) {
          gapStart = now;
          setGaps((previous) => [
            ...previous.filter(
              (gap) => (gap.to ?? now) >= now - 15 * 60 * 1000,
            ),
            { from: now },
          ]);
        } else if (next === 'connected' && gapStart !== undefined) {
          setGaps((previous) =>
            previous.map((gap) => (gap.to == null ? { ...gap, to: now } : gap)),
          );
          gapStart = undefined;
        }
      },
    );
  }, []);
  return { state, gaps };
}
