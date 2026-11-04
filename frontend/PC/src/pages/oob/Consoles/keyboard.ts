/** 键盘布局采用浏览器标准 code，桥接器负责转换成厂商协议；不以字符拼接模拟扫描码。 */
export type ConsoleKey = {
  code: string;
  key: string;
  label: string;
  wide?: number;
};
const key = (
  code: string,
  value: string,
  label = value,
  wide?: number,
): ConsoleKey => ({ code, key: value, label, wide });
const letters = (value: string) =>
  [...value].map((letter) => key(`Key${letter}`, letter.toLowerCase(), letter));
export const keyboardRows: ConsoleKey[][] = [
  [
    key('Escape', 'Escape', 'Esc'),
    ...Array.from({ length: 12 }, (_, index) =>
      key(`F${index + 1}`, `F${index + 1}`),
    ),
    key('PrintScreen', 'PrintScreen', 'PrtSc'),
    key('ScrollLock', 'ScrollLock', 'ScrLk'),
    key('Pause', 'Pause', 'Pause'),
  ],
  [
    key('Backquote', '`', '~ `'),
    ...[...'1234567890'].map((value) => key(`Digit${value}`, value)),
    key('Minus', '-', '_ -'),
    key('Equal', '=', '+ ='),
    key('Backspace', 'Backspace', 'Backspace', 2),
    key('Insert', 'Insert', 'Ins'),
    key('Home', 'Home', 'Home'),
    key('PageUp', 'PageUp', 'PgUp'),
  ],
  [
    key('Tab', 'Tab', 'Tab', 1.5),
    ...letters('QWERTYUIOP'),
    key('BracketLeft', '[', '{ ['),
    key('BracketRight', ']', '} ]'),
    key('Backslash', '\\', '| \\', 1.5),
    key('Delete', 'Delete', 'Del'),
    key('End', 'End', 'End'),
    key('PageDown', 'PageDown', 'PgDn'),
  ],
  [
    key('CapsLock', 'CapsLock', 'Caps Lock', 1.8),
    ...letters('ASDFGHJKL'),
    key('Semicolon', ';', ': ;'),
    key('Quote', "'", '" \''),
    key('Enter', 'Enter', 'Enter', 2.2),
  ],
  [
    key('ShiftLeft', 'Shift', 'Shift', 2.2),
    ...letters('ZXCVBNM'),
    key('Comma', ',', '< ,'),
    key('Period', '.', '> .'),
    key('Slash', '/', '? /'),
    key('ShiftRight', 'Shift', 'Shift', 2.8),
    key('ArrowUp', 'ArrowUp', '↑'),
  ],
  [
    key('ControlLeft', 'Control', 'Ctrl', 1.5),
    key('MetaLeft', 'Meta', 'Win', 1.2),
    key('AltLeft', 'Alt', 'Alt', 1.2),
    key('Space', ' ', 'Space', 6),
    key('AltRight', 'Alt', 'Alt', 1.2),
    key('MetaRight', 'Meta', 'Win', 1.2),
    key('ContextMenu', 'ContextMenu', 'Menu', 1.2),
    key('ControlRight', 'Control', 'Ctrl', 1.5),
    key('ArrowLeft', 'ArrowLeft', '←'),
    key('ArrowDown', 'ArrowDown', '↓'),
    key('ArrowRight', 'ArrowRight', '→'),
  ],
];
export const numpadRows = [
  [
    key('NumLock', 'NumLock', 'Num'),
    key('NumpadDivide', '/', '/'),
    key('NumpadMultiply', '*', '*'),
    key('NumpadSubtract', '-', '-'),
  ],
  [
    key('Numpad7', '7'),
    key('Numpad8', '8'),
    key('Numpad9', '9'),
    key('NumpadAdd', '+', '+'),
  ],
  [key('Numpad4', '4'), key('Numpad5', '5'), key('Numpad6', '6')],
  [
    key('Numpad1', '1'),
    key('Numpad2', '2'),
    key('Numpad3', '3'),
    key('NumpadEnter', 'Enter', 'Enter'),
  ],
  [key('Numpad0', '0', '0', 2), key('NumpadDecimal', '.', '.')],
];
export const modifierCodes = new Set([
  'ControlLeft',
  'ControlRight',
  'ShiftLeft',
  'ShiftRight',
  'AltLeft',
  'AltRight',
  'MetaLeft',
  'MetaRight',
]);
export type ConsoleInput =
  | { type: 'key'; code: string; key: string; down: boolean }
  | {
      type: 'pointer';
      x: number;
      y: number;
      buttons: number;
      action: 'down' | 'up' | 'move';
    }
  | { type: 'wheel'; deltaX: number; deltaY: number }
  | { type: 'release-all' };

/** 输入失焦时释放已按下的物理键，防止修饰键留在远端；不会回放断线期间的输入。 */
export function createKeyTracker(send: (event: ConsoleInput) => void) {
  const pressed = new Map<string, string>();
  return {
    down(code: string, value: string) {
      pressed.set(code, value);
      send({ type: 'key', code, key: value, down: true });
    },
    up(code: string, value: string) {
      if (!pressed.has(code)) return;
      pressed.delete(code);
      send({ type: 'key', code, key: value, down: false });
    },
    release() {
      for (const [code, value] of pressed)
        send({ type: 'key', code, key: value, down: false });
      pressed.clear();
      send({ type: 'release-all' });
    },
  };
}
