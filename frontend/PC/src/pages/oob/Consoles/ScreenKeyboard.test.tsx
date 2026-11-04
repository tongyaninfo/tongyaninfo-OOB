import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import { describe, expect, it, vi } from 'vitest';

vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));

import ScreenKeyboard from './ScreenKeyboard';

describe('屏幕键盘的实际组合输入', () => {
  it('Ctrl锁定后点击字母按顺序发送按下/释放并清空锁定', () => {
    const input = vi.fn();
    render(<ScreenKeyboard enabled onInput={input} />);
    fireEvent.click(screen.getByRole('button', { name: 'ControlLeft' }));
    expect(screen.getByRole('button', { name: 'ControlLeft' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    fireEvent.click(screen.getByRole('button', { name: 'KeyA' }));
    expect(input.mock.calls.map(([event]) => event)).toEqual([
      { type: 'key', code: 'ControlLeft', key: 'Control', down: true },
      { type: 'key', code: 'KeyA', key: 'a', down: true },
      { type: 'key', code: 'KeyA', key: 'a', down: false },
      { type: 'key', code: 'ControlLeft', key: 'Control', down: false },
    ]);
    expect(screen.getByRole('button', { name: 'ControlLeft' })).toHaveAttribute(
      'aria-pressed',
      'false',
    );
  });
  it('无输入能力时所有按键禁用，恢复能力不保留上一次锁定', () => {
    const input = vi.fn();
    const view = render(<ScreenKeyboard enabled onInput={input} />);
    fireEvent.click(screen.getByRole('button', { name: 'ShiftLeft' }));
    view.rerender(<ScreenKeyboard enabled={false} onInput={input} />);
    expect(screen.getByRole('button', { name: 'KeyA' })).toBeDisabled();
    view.rerender(<ScreenKeyboard enabled onInput={input} />);
    expect(screen.getByRole('button', { name: 'ShiftLeft' })).toHaveAttribute(
      'aria-pressed',
      'false',
    );
  });
});
