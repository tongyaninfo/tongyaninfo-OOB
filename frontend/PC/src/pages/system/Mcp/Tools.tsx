import { Descriptions, Modal, Tag, Typography } from 'antd';
import React, { useState } from 'react';
import ManagedTable from '@/pages/oob/components/ManagedTable';
import { useOobIntl } from '@/pages/oob/intl';
import type { McpToolDto } from '@/services/oob/models';
import { detailRow } from './common';

/** Schema只作为纯文本显示，工具描述不作为HTML或可执行指令。 */
export default function Tools({
  tools,
  loading,
}: {
  tools: McpToolDto[];
  loading: boolean;
}) {
  const t = useOobIntl();
  const [selected, setSelected] = useState<McpToolDto>();
  return (
    <>
      <Typography.Paragraph type="secondary">
        {t('mcp.toolHelp')}
      </Typography.Paragraph>
      <ManagedTable<McpToolDto>
        tableId="system.mcp.tools.v1"
        rowKey="name"
        dataSource={tools}
        loading={loading}
        pagination={false}
        onRow={detailRow(setSelected)}
        scroll={{ x: 850 }}
        columns={[
          { key: 'name', dataIndex: 'name', title: t('mcp.toolName') },
          {
            key: 'description',
            dataIndex: 'description',
            title: t('mcp.toolDescription'),
          },
          {
            key: 'permission',
            dataIndex: 'requiredPermission',
            title: t('mcp.permission'),
            render: (value) => value || '—',
          },
          {
            key: 'readonly',
            dataIndex: 'readOnly',
            title: t('mcp.readonly'),
            render: (value) => <Tag>{String(value)}</Tag>,
          },
          {
            key: 'enabled',
            dataIndex: 'enabled',
            title: t('mcp.status'),
            render: (value) => (
              <Tag color={value ? 'success' : 'default'}>
                {t(value ? 'mcp.enabled' : 'mcp.disabled')}
              </Tag>
            ),
          },
        ]}
      />
      <Modal
        open={Boolean(selected)}
        title={selected?.name}
        onCancel={() => setSelected(undefined)}
        footer={null}
        width={900}
      >
        <Descriptions
          column={1}
          items={[
            {
              key: 'input',
              label: t('mcp.inputSchema'),
              children: (
                <pre
                  style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}
                >
                  {JSON.stringify(selected?.inputSchema, null, 2)}
                </pre>
              ),
            },
            {
              key: 'output',
              label: t('mcp.outputSchema'),
              children: (
                <pre
                  style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}
                >
                  {JSON.stringify(selected?.outputSchema, null, 2)}
                </pre>
              ),
            },
          ]}
        />
      </Modal>
    </>
  );
}
