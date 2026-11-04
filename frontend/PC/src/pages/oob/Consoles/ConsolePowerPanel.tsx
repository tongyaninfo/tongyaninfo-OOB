import { useAccess } from '@umijs/max';
import React from 'react';
import PowerPanel, { type PowerPanelProps } from '../components/PowerPanel';

export type ConsolePowerPanelProps = Omit<PowerPanelProps, 'initialOperations'>;

/** 控制台权限不授予电源权限；只复用目录中的目标信息，不读取完整服务器详情。 */
export default function ConsolePowerPanel(props: ConsolePowerPanelProps) {
  const access = useAccess();
  if (!access.canOperatePower) return null;
  return <PowerPanel {...props} />;
}
