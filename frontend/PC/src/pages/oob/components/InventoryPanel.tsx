import { useIntl } from '@umijs/max';
import { Card, Col, Descriptions, Empty, Row, Tree, Typography } from 'antd';
import type { DataNode } from 'antd/es/tree';
import React, { useState } from 'react';
import type { DeviceInventory, HardwareComponent } from '@/services/oob/models';
import { formatCapacity, formatTime } from '../format';
import { useOobIntl } from '../intl';
import {
  inventoryAttributeLabel,
  inventoryKindLabel,
  inventoryStatusLabel,
  visibleInventoryAttributes,
} from './inventoryLabels';

/** 树结构沿用设备控制器和磁盘的真实归属，不把磁盘打平成无来源列表。 */
function toNodes(components: HardwareComponent[]): DataNode[] {
  return components.map((component) => ({
    key: component.key,
    title: component.name,
    children: component.children?.length
      ? toNodes(component.children)
      : undefined,
  }));
}

function findComponent(
  components: HardwareComponent[],
  key: string,
): HardwareComponent | undefined {
  for (const component of components) {
    if (component.key === key) return component;
    const found = findComponent(component.children || [], key);
    if (found) return found;
  }
  return undefined;
}

export default function InventoryPanel({
  inventory,
}: {
  inventory?: DeviceInventory | null;
}) {
  const t = useOobIntl();
  const { locale } = useIntl();
  const [selected, setSelected] = useState('');
  const components = inventory?.components || [];
  const component = selected
    ? findComponent(components, selected)
    : components[0];
  if (!components.length)
    return <Empty description={t('oob.inventory.empty')} />;
  return (
    <>
      <Typography.Paragraph type="secondary">
        {t('oob.inventory.help')} · {t('oob.updated')}：
        {formatTime(inventory?.collectedAt)}
      </Typography.Paragraph>
      <Row gutter={[16, 16]}>
        <Col xs={24} lg={9}>
          <Card size="small">
            <Tree
              treeData={toNodes(components)}
              defaultExpandAll
              selectedKeys={component ? [component.key] : []}
              onSelect={(keys) => setSelected(String(keys[0] || ''))}
            />
          </Card>
        </Col>
        <Col xs={24} lg={15}>
          <Card
            size="small"
            title={component?.name || t('oob.inventory.select')}
          >
            {component && (
              <Descriptions
                column={{ xs: 1, sm: 2 }}
                bordered
                size="small"
                items={[
                  {
                    key: 'kind',
                    label: t('oob.inventory.kind'),
                    children: inventoryKindLabel(component.kind, locale),
                  },
                  {
                    key: 'model',
                    label: inventoryAttributeLabel('Model', locale),
                    children: component.model || '—',
                  },
                  {
                    key: 'serial',
                    label: t('oob.serial'),
                    children: component.serialNumber || '—',
                  },
                  {
                    key: 'health',
                    label: t('oob.health'),
                    children: component.status
                      ? inventoryStatusLabel(component.status, locale)
                      : '—',
                  },
                  {
                    key: 'capacity',
                    label: t('oob.inventory.capacity'),
                    children: formatCapacity(component.capacityBytes),
                  },
                  {
                    key: 'children',
                    label: t('oob.inventory.children'),
                    children: component.children?.length || 0,
                  },
                  ...visibleInventoryAttributes(component.attributes || {}).map(
                    ([key, value]) => ({
                      key: `attr-${key}`,
                      label: (
                        <span title={key}>
                          {inventoryAttributeLabel(key, locale)}
                        </span>
                      ),
                      children:
                        value == null
                          ? '—'
                          : /(?:Status|State)$/.test(key)
                            ? inventoryStatusLabel(value, locale)
                            : value,
                      // 与Descriptions列数共用相同断点，窄屏不能仍占两列。
                      span: { xs: 1, sm: 2 },
                    }),
                  ),
                ]}
              />
            )}
          </Card>
        </Col>
      </Row>
    </>
  );
}
