import { useCallback, useEffect, useRef, useState } from 'react';
import { captureRuntimeException } from '@/services/oob/browserExceptions';

type FullscreenIssue = 'unsupported' | 'occupied' | 'failed' | null;

/** 浏览器的能力或权限拒绝是预期结果；其余真实异常沿用统一脱敏上报。 */
function reportUnexpected(error: unknown) {
  const name =
    error && typeof error === 'object' && 'name' in error ? error.name : '';
  if (
    ![
      'NotAllowedError',
      'NotSupportedError',
      'TypeError',
      'AbortError',
    ].includes(String(name))
  )
    void captureRuntimeException(error);
}

/** 只控制当前看板的原生全屏，不重挂页面或改变查询、采样、实时订阅。 */
export default function useBoardFullscreen({
  captureEscape = true,
}: {
  captureEscape?: boolean;
} = {}) {
  const ref = useRef<HTMLDivElement>(null);
  const mounted = useRef(true);
  const inFlight = useRef(false);
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [pending, setPending] = useState(false);
  const [issue, setIssue] = useState<FullscreenIssue>(null);

  useEffect(() => {
    mounted.current = true;
    const element = ref.current;
    // Esc、浏览器主动退出和其他全屏变化统一按真实归属同步按钮。
    const sync = () =>
      setIsFullscreen(
        document.fullscreenElement === element && element !== null,
      );
    const handleEscape = (event: KeyboardEvent) => {
      if (
        event.key === 'Escape' &&
        !event.repeat &&
        !event.isComposing &&
        element &&
        document.fullscreenElement === element &&
        typeof document.exitFullscreen === 'function'
      ) {
        // 部分嵌入式浏览器只把Esc交给页面，由本看板补充退出；不阻止原生按键行为。
        void document.exitFullscreen().catch((error: unknown) => {
          reportUnexpected(error);
          if (mounted.current) setIssue('failed');
        });
      }
    };
    document.addEventListener('fullscreenchange', sync);
    if (captureEscape) document.addEventListener('keydown', handleEscape, true);
    sync();
    return () => {
      mounted.current = false;
      document.removeEventListener('fullscreenchange', sync);
      document.removeEventListener('keydown', handleEscape, true);
      if (
        element &&
        document.fullscreenElement === element &&
        typeof document.exitFullscreen === 'function'
      )
        void document.exitFullscreen().catch(reportUnexpected);
    };
  }, [captureEscape]);

  const popupContainer = useCallback(() => ref.current || document.body, []);
  const toggle = useCallback(async () => {
    if (inFlight.current) return;
    const element = ref.current;
    const current = document.fullscreenElement;
    if (current && current !== element) {
      setIssue('occupied');
      return;
    }
    const exiting = element !== null && current === element;
    if (
      !element ||
      (exiting
        ? typeof document.exitFullscreen !== 'function'
        : typeof element.requestFullscreen !== 'function' ||
          document.fullscreenEnabled === false)
    ) {
      setIssue('unsupported');
      return;
    }
    inFlight.current = true;
    setPending(true);
    setIssue(null);
    try {
      // 在点击事件的同步调用链里请求，不能先等待其他任务而丢失浏览器用户激活权限。
      if (exiting) await document.exitFullscreen();
      else await element.requestFullscreen();
      if (mounted.current)
        setIsFullscreen(document.fullscreenElement === element);
      else if (
        document.fullscreenElement === element &&
        typeof document.exitFullscreen === 'function'
      )
        // 请求晚于页面卸载才完成时，仍只释放本次看板的全屏归属。
        await document.exitFullscreen().catch(reportUnexpected);
    } catch (error) {
      reportUnexpected(error);
      if (mounted.current) {
        setIssue('failed');
      }
    } finally {
      inFlight.current = false;
      if (mounted.current) setPending(false);
    }
  }, []);
  return { ref, isFullscreen, pending, issue, toggle, popupContainer };
}
