import { theme, Typography } from 'antd';
import React from 'react';

/** 带外地址独立打开；复制按钮放在链接外，避免复制时同时跳转。 */
export default function ServerAddress({
  address,
  copyable = false,
}: {
  address: string;
  copyable?: boolean;
}) {
  const { token } = theme.useToken();
  return (
    <Typography.Text copyable={copyable ? { text: address } : false}>
      <a
        href={address}
        target="_blank"
        rel="noopener noreferrer"
        // 内联蓝色覆盖浏览器的已访问颜色，切换主题后也保持蓝色。
        style={{ color: token.blue6 }}
        onClick={(event) => event.stopPropagation()}
      >
        {address}
      </a>
    </Typography.Text>
  );
}
