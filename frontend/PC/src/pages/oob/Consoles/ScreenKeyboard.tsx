import { Button, Space, Typography } from 'antd';
import React, { useEffect, useRef, useState } from 'react';
import { useOobIntl } from '../intl';
import {
  type ConsoleInput,
  type ConsoleKey,
  keyboardRows,
  modifierCodes,
  numpadRows,
} from './keyboard';
import useStyles from './style';

/** 屏幕键盘与物理键盘共用输入协议；修饰键为一次组合锁定，发送后可靠释放。 */
export default function ScreenKeyboard({
  enabled,
  onInput,
  releaseVersion = 0,
}: {
  enabled: boolean;
  onInput: (event: ConsoleInput) => void;
  releaseVersion?: number;
}) {
  const t = useOobIntl();
  const { styles } = useStyles();
  const [modifiers, setModifiers] = useState<ConsoleKey[]>([]);
  const currentInput = useRef(onInput);
  currentInput.current = onInput;
  useEffect(
    () => () => {
      currentInput.current({ type: 'release-all' });
    },
    [],
  );
  useEffect(() => {
    setModifiers([]);
  }, [enabled, releaseVersion]);
  const release = () => {
    for (const item of modifiers)
      onInput({ type: 'key', code: item.code, key: item.key, down: false });
    onInput({ type: 'release-all' });
    setModifiers([]);
  };
  const press = (item: ConsoleKey) => {
    if (!enabled) return;
    if (modifierCodes.has(item.code)) {
      const selected = modifiers.some((value) => value.code === item.code);
      onInput({ type: 'key', code: item.code, key: item.key, down: !selected });
      setModifiers(
        selected
          ? modifiers.filter((value) => value.code !== item.code)
          : [...modifiers, item],
      );
      return;
    }
    onInput({ type: 'key', code: item.code, key: item.key, down: true });
    onInput({ type: 'key', code: item.code, key: item.key, down: false });
    for (const modifier of modifiers)
      onInput({
        type: 'key',
        code: modifier.code,
        key: modifier.key,
        down: false,
      });
    setModifiers([]);
  };
  const renderRow = (row: ConsoleKey[], index: number) => (
    <div className={styles.keyRow} key={row[0].code || index}>
      {row.map((item) => (
        <Button
          key={item.code}
          disabled={!enabled}
          size="small"
          aria-label={item.code}
          aria-pressed={
            modifierCodes.has(item.code)
              ? modifiers.some((value) => value.code === item.code)
              : undefined
          }
          type={
            modifiers.some((value) => value.code === item.code)
              ? 'primary'
              : 'default'
          }
          style={{
            flex: item.wide || 1,
            minWidth: item.wide ? 32 * item.wide : 31,
          }}
          onClick={() => press(item)}
        >
          {item.label}
        </Button>
      ))}
    </div>
  );
  return (
    <div className={styles.keyboard} data-console-keyboard>
      <Space wrap className={styles.keyboardHeading}>
        <Typography.Text>{t('oob.console.keyboard')}</Typography.Text>
        <Button size="small" disabled={!enabled} onClick={release}>
          {t('oob.console.release')}
        </Button>
        <Typography.Text type="secondary">
          {t('oob.console.keyboardHint')}
        </Typography.Text>
      </Space>
      <div className={styles.keyboardLayout}>
        <div className={styles.mainKeys}>{keyboardRows.map(renderRow)}</div>
        <div className={styles.numpad}>{numpadRows.map(renderRow)}</div>
      </div>
    </div>
  );
}
