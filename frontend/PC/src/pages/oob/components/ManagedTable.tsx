import { SettingOutlined } from '@ant-design/icons';
import type { TableProps } from 'antd';
import { Checkbox, Dropdown, Radio, Space, Table, Tooltip } from 'antd';
import React, { useState } from 'react';
import {
  getTablePreference,
  updateTablePreference,
} from '@/services/oob/preferences';
import { useOobIntl } from '../intl';

interface Props<T extends object> extends TableProps<T> {
  tableId: string;
}

/** 新业务表格复用 Ant Design，按稳定表编号保存当前账号的尺寸与列显隐。 */
export default function ManagedTable<T extends object>({
  tableId,
  columns = [],
  pagination,
  ...props
}: Props<T>) {
  const t = useOobIntl();
  const settings = getTablePreference(tableId);
  const [size, setSize] = useState<'small' | 'middle' | 'large'>(
    (settings?.size as 'small' | 'middle' | 'large') || props.size || 'middle',
  );
  const [state, setState] = useState<Record<string, { show?: boolean }>>(
    (settings?.columnsState as Record<string, { show?: boolean }>) || {},
  );
  const initialPagination = typeof pagination === 'object' ? pagination : {};
  const [current, setCurrent] = useState(initialPagination.current || 1);
  const [pageSize, setPageSize] = useState(
    initialPagination.pageSize || initialPagination.defaultPageSize || 10,
  );
  const [saveError, setSaveError] = useState('');
  const columnKey = (column: (typeof columns)[number], index: number) =>
    String(
      column.key ??
        ('dataIndex' in column
          ? Array.isArray(column.dataIndex)
            ? column.dataIndex.join('.')
            : column.dataIndex
          : undefined) ??
        index,
    );
  const persist = (nextSize: typeof size, nextState: typeof state) =>
    updateTablePreference(
      tableId,
      { size: nextSize, columnsState: nextState },
      () => setSaveError(t('oob.settings.failed')),
    );
  const rows = props.dataSource?.length || 0;
  const total = initialPagination.total ?? rows;
  const effectiveSize = initialPagination.pageSize ?? pageSize;
  const page = Math.min(
    initialPagination.current ?? current,
    Math.max(1, Math.ceil(total / effectiveSize)),
  );
  return (
    <section
      aria-label={t('oob.table.settings')}
      // biome-ignore lint/a11y/noNoninteractiveTabindex: 命名表格区域允许聚焦并提供左右键翻页，子控件保留自身键义。
      tabIndex={0}
      onKeyDown={(event) => {
        if (
          pagination === false ||
          props.loading ||
          event.repeat ||
          event.nativeEvent.isComposing ||
          event.ctrlKey ||
          event.altKey ||
          event.metaKey ||
          event.shiftKey
        )
          return;
        if (
          (event.target as HTMLElement).closest(
            'input,textarea,select,button,a,[contenteditable=true],[role=combobox]',
          )
        )
          return;
        const next =
          event.key === 'ArrowRight'
            ? page + 1
            : event.key === 'ArrowLeft'
              ? page - 1
              : page;
        if (
          next !== page &&
          next >= 1 &&
          next <= Math.ceil(total / effectiveSize)
        ) {
          event.preventDefault();
          setCurrent(next);
          initialPagination.onChange?.(next, effectiveSize);
        }
      }}
    >
      <div
        style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: 8 }}
      >
        <Dropdown
          trigger={['click']}
          popupRender={() => (
            <div
              style={{
                background: 'var(--ant-color-bg-elevated)',
                borderRadius: 8,
                padding: 16,
                boxShadow: 'var(--ant-box-shadow-secondary)',
                maxWidth: 320,
              }}
            >
              <Space orientation="vertical">
                <Radio.Group
                  size="small"
                  value={size}
                  onChange={(event) => {
                    const next = event.target.value;
                    setSize(next);
                    persist(next, state);
                  }}
                  options={[
                    { label: t('oob.table.compact'), value: 'small' },
                    { label: t('oob.table.medium'), value: 'middle' },
                    { label: t('oob.table.large'), value: 'large' },
                  ]}
                />
                {columns.map((column, index) => {
                  const key = columnKey(column, index);
                  return (
                    <Checkbox
                      key={key}
                      checked={state[key]?.show !== false}
                      onChange={(event) => {
                        const next = {
                          ...state,
                          [key]: { show: event.target.checked },
                        };
                        setState(next);
                        persist(size, next);
                      }}
                    >
                      {typeof column.title === 'function'
                        ? key
                        : column.title || key}
                    </Checkbox>
                  );
                })}
              </Space>
            </div>
          )}
        >
          <Tooltip title={t('oob.table.settings')}>
            <button
              type="button"
              aria-label={t('oob.table.settings')}
              style={{
                border: 0,
                padding: 0,
                background: 'transparent',
                color: 'inherit',
                cursor: 'pointer',
                fontSize: 16,
              }}
            >
              <SettingOutlined />
            </button>
          </Tooltip>
        </Dropdown>
      </div>
      {saveError && <span role="alert">{saveError}</span>}
      <Table<T>
        {...props}
        size={size}
        columns={columns.filter(
          (column, index) => state[columnKey(column, index)]?.show !== false,
        )}
        pagination={
          pagination === false
            ? false
            : {
                ...initialPagination,
                current: page,
                pageSize: effectiveSize,
                onChange: (next, nextSize) => {
                  setCurrent(next);
                  setPageSize(nextSize);
                  initialPagination.onChange?.(next, nextSize);
                },
              }
        }
      />
    </section>
  );
}
