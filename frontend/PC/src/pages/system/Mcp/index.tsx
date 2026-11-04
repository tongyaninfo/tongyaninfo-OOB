import { PageContainer } from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import { useAccess, useModel } from '@umijs/max';
import {
  Alert,
  App,
  Button,
  Descriptions,
  Result,
  Tabs,
  Typography,
} from 'antd';
import React from 'react';
import { useOobIntl } from '@/pages/oob/intl';
import { getMcpConfiguration, getMcpTools } from '@/services/oob/mcp';
import Clients from './Clients';
import Configuration from './Configuration';
import Tools from './Tools';

function Guide({ account }: { account: string }) {
  const t = useOobIntl();
  const { message } = App.useApp();
  const query = useQuery({
    queryKey: ['mcp', account, 'configuration'],
    queryFn: ({ signal }) => getMcpConfiguration(signal),
    retry: false,
    refetchOnWindowFocus: false,
  });
  const value = query.data?.success ? query.data.data : undefined;
  const template = JSON.stringify(
    {
      transport: 'streamable-http',
      url: value?.endpoint || 'https://<system-domain>/mcp',
      oauth: {
        clientId: '<registered-client-id>',
        redirectUri: '<registered-redirect-uri>',
        pkce: true,
      },
    },
    null,
    2,
  );
  return (
    <>
      <Typography.Paragraph>{t('mcp.guideHelp')}</Typography.Paragraph>
      <Typography.Paragraph>{t('mcp.guideAuth')}</Typography.Paragraph>
      <Typography.Paragraph type="secondary">
        {t('mcp.guideCompatibility')}
      </Typography.Paragraph>
      {query.data?.success === false && (
        <Alert type="error" title={query.data.errorMessage} />
      )}
      <Descriptions
        items={[
          {
            key: 'endpoint',
            label: t('mcp.endpoint'),
            children: value?.endpoint || '—',
          },
          {
            key: 'protocols',
            label: t('mcp.protocols'),
            children: value?.protocolVersions.join(', ') || '—',
          },
        ]}
      />
      <Typography.Title level={5}>{t('mcp.template')}</Typography.Title>
      <pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
        {template}
      </pre>
      <Button
        onClick={async () => {
          try {
            await navigator.clipboard.writeText(template);
            message.success(t('mcp.copied'));
          } catch {
            message.error(t('mcp.copyFailed'));
          }
        }}
      >
        {t('mcp.copy')}
      </Button>
    </>
  );
}

function Workspace({ account }: { account: string }) {
  const t = useOobIntl();
  const tools = useQuery({
    queryKey: ['mcp', account, 'tools'],
    queryFn: ({ signal }) => getMcpTools(signal),
    retry: false,
    refetchOnWindowFocus: false,
  });
  const data = tools.data?.success ? tools.data.data : [];
  return (
    <PageContainer title={t('mcp.title')}>
      {tools.data?.success === false && (
        <Alert
          showIcon
          type="error"
          title={tools.data.errorMessage}
          action={
            <Button onClick={() => void tools.refetch()}>
              {t('mcp.reload')}
            </Button>
          }
        />
      )}
      <Tabs
        destroyOnHidden
        items={[
          {
            key: 'configuration',
            label: t('mcp.configuration'),
            children: <Configuration account={account} tools={data} />,
          },
          {
            key: 'clients',
            label: t('mcp.clients'),
            children: <Clients account={account} tools={data} />,
          },
          {
            key: 'tools',
            label: t('mcp.tools'),
            children: <Tools tools={data} loading={tools.isFetching} />,
          },
          {
            key: 'guide',
            label: t('mcp.guide'),
            children: <Guide account={account} />,
          },
        ]}
      />
    </PageContainer>
  );
}

export default function Mcp() {
  const access = useAccess();
  const { initialState } = useModel('@@initialState');
  const account = initialState?.currentUser?.userid || '';
  // 权限保护位于有请求的子树之外，普通用户直达路由也不发管理查询。
  return access.canAdmin ? (
    <Workspace key={account} account={account} />
  ) : (
    <Result status="403" />
  );
}
