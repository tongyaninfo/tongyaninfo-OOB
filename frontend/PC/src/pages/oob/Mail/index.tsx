import { MailOutlined } from '@ant-design/icons';
import { PageContainer } from '@ant-design/pro-components';
import { useQueryClient } from '@tanstack/react-query';
import { useAccess } from '@umijs/max';
import { Button, Result, Tabs } from 'antd';
import React, { useState } from 'react';
import { useOobIntl } from '../intl';
import Configuration from './Configuration';
import Records from './Records';
import Reports from './Reports';
import SendMailModal from './SendMailModal';

/** 复用管理员身份控制入口，后端仍独立验证每个配置、发送和记录查询。 */
export default function Mail() {
  const t = useOobIntl();
  const access = useAccess();
  const client = useQueryClient();
  const [sending, setSending] = useState(false);
  const changed = () => {
    void client.invalidateQueries({ queryKey: ['oob', 'mail', 'messages'] });
  };
  if (!access.canAdmin) return <Result status="403" />;
  return (
    <PageContainer
      title={t('oob.mail.title')}
      extra={
        <Button
          type="primary"
          icon={<MailOutlined />}
          onClick={() => setSending(true)}
        >
          {t('oob.mail.send')}
        </Button>
      }
    >
      <Tabs
        items={[
          {
            key: 'messages',
            label: t('oob.mail.records'),
            children: <Records />,
          },
          {
            key: 'configuration',
            label: t('oob.mail.config'),
            children: <Configuration onChanged={changed} />,
          },
          {
            key: 'reports',
            label: t('oob.mail.reports.title'),
            children: <Reports />,
          },
        ]}
      />
      <SendMailModal
        open={sending}
        onClose={() => setSending(false)}
        onChanged={changed}
      />
    </PageContainer>
  );
}
