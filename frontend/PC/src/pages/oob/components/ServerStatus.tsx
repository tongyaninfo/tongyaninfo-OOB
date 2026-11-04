import { Tag } from 'antd';
import React from 'react';
import type { ServerDto } from '@/services/oob/models';
import { useOobIntl } from '../intl';

/** 连接状态与采集开关分别表达，暂停采集不被伪装成连接失败。 */
export default function ServerStatus({ server }: { server: ServerDto }) {
  const t = useOobIntl();
  const status = server.connectionStatus || '';
  const color = /online|connected|available|success/i.test(status)
    ? 'success'
    : /offline|failed|error|unreachable/i.test(status)
      ? 'error'
      : 'default';
  return (
    <>
      {!server.enabled && <Tag>{t('oob.paused')}</Tag>}
      <Tag color={color}>
        {status === 'Online'
          ? t('oob.online')
          : status === 'Offline'
            ? t('oob.offline')
            : status === 'Pending'
              ? t('oob.pending')
              : status || t('oob.pending')}
      </Tag>
    </>
  );
}
