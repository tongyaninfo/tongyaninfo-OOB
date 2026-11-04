import { PlusOutlined, ReloadOutlined } from '@ant-design/icons';
import { PageContainer } from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import { useAccess, useModel } from '@umijs/max';
import {
  Alert,
  App,
  Button,
  Input,
  Select,
  Space,
  type TableProps,
  Tag,
  Tooltip,
} from 'antd';
import React, { useEffect, useRef, useState } from 'react';
import ChangePasswordModal from '@/components/ChangePasswordModal';
import { currentUser } from '@/services/oob/auth';
import type { UserDto } from '@/services/oob/models';
import { sessionGeneration } from '@/services/oob/session';
import {
  deleteUser,
  getUsers,
  saveUser,
  type UserQuery,
} from '@/services/oob/users';
import ManagedTable from '../components/ManagedTable';
import { formatTime } from '../format';
import { useOobIntl } from '../intl';
import { userError } from './errors';
import UserEditor from './UserEditor';

/** 权限只决定入口；服务器在每次请求重新校验用户、版本和最后管理账号。 */
export default function Users() {
  const t = useOobIntl();
  const access = useAccess();
  const { initialState, setInitialState } = useModel('@@initialState');
  const { message, modal } = App.useApp();
  const [params, setParams] = useState<UserQuery>({
    current: 1,
    pageSize: 20,
    status: 'all',
    sortField: 'username',
    sortOrder: 'ascend',
  });
  const [editor, setEditor] = useState<{ user?: UserDto }>();
  const [passwordUser, setPasswordUser] = useState<UserDto>();
  const [writing, setWriting] = useState(false);
  const [needsRefresh, setNeedsRefresh] = useState(false);
  const active = useRef(true);
  useEffect(() => {
    active.current = true;
    return () => {
      active.current = false;
    };
  }, []);
  const writeLock = useRef(false);
  const [error, setError] = useState('');
  const query = useQuery({
    queryKey: ['oob', 'users', params],
    queryFn: ({ signal }) => getUsers(params, signal),
    enabled: access.canManageUsers,
    retry: false,
    refetchOnWindowFocus: false,
  });
  const data = query.data?.success ? query.data.data : undefined;
  const refresh = async () => {
    setError('');
    const result = await query.refetch();
    if (result.data?.success) setNeedsRefresh(false);
  };
  const saved = async () => {
    const expected = sessionGeneration();
    const identity = await currentUser();
    if (!active.current || expected !== sessionGeneration()) return;
    if (identity.success)
      setInitialState((state) => ({ ...state, currentUser: identity.data }));
    if (identity.success) await refresh();
  };
  const canEdit = (user: UserDto) =>
    !user.deletedAt && (!user.isAdministrator || access.canAdmin);
  const confirm = (user: UserDto, remove: boolean) => {
    modal.confirm({
      title: t(
        remove
          ? 'oob.users.delete'
          : user.enabled
            ? 'oob.users.disabled'
            : 'oob.users.enabled',
      ),
      content: t(
        remove
          ? 'oob.users.deleteConfirm'
          : user.enabled
            ? 'oob.users.disableConfirm'
            : 'oob.users.enableConfirm',
        { name: `${user.displayName} (${user.username})` },
      ),
      okText: t('oob.users.save'),
      cancelText: t('oob.users.cancel'),
      okButtonProps: { danger: remove || user.enabled },
      onOk: async () => {
        if (writeLock.current) return;
        const expected = sessionGeneration();
        writeLock.current = true;
        setWriting(true);
        setError('');
        try {
          const result = remove
            ? await deleteUser(user)
            : await saveUser(
                {
                  username: user.username,
                  displayName: user.displayName,
                  enabled: !user.enabled,
                  canManageUsers: user.canManageUsers,
                  canUseConsole: user.canUseConsole,
                  canManageServers: user.canManageServers,
                  canOperatePower: user.canOperatePower,
                  version: user.version,
                },
                user.id,
              );
          if (!active.current || expected !== sessionGeneration()) return;
          if (!result.success) {
            setNeedsRefresh(
              result.errorCode === 'USER_VERSION_CONFLICT' ||
                (result.dispatched &&
                  (result.kind !== 'business' || result.status >= 500)),
            );
            setError(
              result.dispatched &&
                (result.kind !== 'business' || result.status >= 500)
                ? t('oob.users.unknown')
                : userError(result, t),
            );
            return;
          }
          message.success(
            t(remove ? 'oob.users.deletedSaved' : 'oob.users.statusSaved'),
          );
          await saved();
        } finally {
          writeLock.current = false;
          if (active.current) setWriting(false);
        }
      },
    });
  };
  const sort = (field: UserQuery['sortField']) => ({
    sorter: true,
    sortOrder: params.sortField === field ? params.sortOrder : null,
  });
  const columns: TableProps<UserDto>['columns'] = [
    {
      key: 'displayName',
      dataIndex: 'displayName',
      title: t('oob.users.name'),
      ...sort('displayName'),
      render: (value, user) => (
        <Space wrap>
          {value}
          {user.isAdministrator && <Tag>{t('oob.users.admin')}</Tag>}
        </Space>
      ),
    },
    {
      key: 'username',
      dataIndex: 'username',
      title: t('oob.users.username'),
      ...sort('username'),
    },
    {
      key: 'status',
      title: t('oob.users.status'),
      render: (_, user) => (
        <Tag
          color={
            user.deletedAt ? 'default' : user.enabled ? 'success' : 'warning'
          }
        >
          {t(
            user.deletedAt
              ? 'oob.users.deleted'
              : user.enabled
                ? 'oob.users.enabled'
                : 'oob.users.disabled',
          )}
        </Tag>
      ),
    },
    {
      key: 'permissions',
      title: t('oob.users.permissions'),
      render: (_, user) => (
        <Space wrap>
          {user.canManageUsers && <Tag>{t('oob.users.manage')}</Tag>}
          {user.canUseConsole && <Tag>{t('oob.users.console')}</Tag>}
          {user.canManageServers && <Tag>{t('oob.users.servers')}</Tag>}
          {user.canOperatePower && <Tag>{t('oob.users.power')}</Tag>}
          {!user.canManageUsers &&
            !user.canUseConsole &&
            !user.canManageServers &&
            !user.canOperatePower && <span>{t('oob.users.readonly')}</span>}
        </Space>
      ),
    },
    {
      key: 'createdAt',
      dataIndex: 'createdAt',
      title: t('oob.users.created'),
      ...sort('createdAt'),
      render: formatTime,
    },
    {
      key: 'updatedAt',
      dataIndex: 'updatedAt',
      title: t('oob.users.updated'),
      ...sort('updatedAt'),
      render: formatTime,
    },
    {
      key: 'actions',
      title: t('oob.actions'),
      render: (_, user) =>
        user.deletedAt ? (
          '—'
        ) : (
          <Tooltip title={canEdit(user) ? undefined : t('oob.users.protected')}>
            <Space wrap>
              <Button
                type="link"
                disabled={!canEdit(user) || writing || needsRefresh}
                onClick={(event) => {
                  event.stopPropagation();
                  setEditor({ user });
                }}
              >
                {t('oob.users.edit')}
              </Button>
              <Button
                type="link"
                disabled={!canEdit(user) || writing || needsRefresh}
                onClick={(event) => {
                  event.stopPropagation();
                  setPasswordUser(user);
                }}
              >
                {t('oob.users.reset')}
              </Button>
              <Button
                type="link"
                disabled={!canEdit(user) || writing || needsRefresh}
                onClick={(event) => {
                  event.stopPropagation();
                  confirm(user, false);
                }}
              >
                {t(user.enabled ? 'oob.users.disabled' : 'oob.users.enabled')}
              </Button>
              <Button
                type="link"
                danger
                disabled={!canEdit(user) || writing || needsRefresh}
                onClick={(event) => {
                  event.stopPropagation();
                  confirm(user, true);
                }}
              >
                {t('oob.users.delete')}
              </Button>
            </Space>
          </Tooltip>
        ),
    },
  ];
  if (!access.canManageUsers)
    return <Alert type="error" showIcon title="403" />;
  return (
    <PageContainer
      title={t('oob.users.title')}
      subTitle={t('oob.users.subtitle')}
    >
      <Space wrap style={{ marginBottom: 16 }}>
        <Input.Search
          allowClear
          placeholder={t('oob.users.search')}
          aria-label={t('oob.users.search')}
          maxLength={100}
          style={{ width: 260, maxWidth: '100%' }}
          onSearch={(keyword) =>
            setParams((value) => ({ ...value, keyword, current: 1 }))
          }
        />
        <Select
          aria-label={t('oob.users.status')}
          value={params.status}
          style={{ minWidth: 140 }}
          onChange={(status) =>
            setParams((value) => ({ ...value, status, current: 1 }))
          }
          options={(['all', 'enabled', 'disabled', 'deleted'] as const).map(
            (value) => ({ value, label: t(`oob.users.${value}`) }),
          )}
        />
        <Button
          icon={<ReloadOutlined />}
          loading={query.isFetching}
          onClick={() => {
            void refresh();
          }}
        >
          {t('oob.users.reload')}
        </Button>
        <Button
          type="primary"
          icon={<PlusOutlined />}
          onClick={() => setEditor({})}
        >
          {t('oob.users.add')}
        </Button>
      </Space>
      {(error || query.data?.success === false) && (
        <Alert
          showIcon
          type="error"
          title={
            error ||
            (query.data?.success === false ? userError(query.data, t) : '')
          }
          style={{ marginBottom: 16 }}
        />
      )}
      <ManagedTable<UserDto>
        key={initialState?.currentUser?.userid}
        tableId="oob.users.v1"
        rowKey="id"
        columns={columns}
        dataSource={data?.items || []}
        loading={query.isFetching}
        scroll={{ x: 1150 }}
        pagination={{
          current: params.current,
          pageSize: params.pageSize,
          total: data?.total || 0,
          showSizeChanger: true,
          onChange: (current, pageSize) =>
            setParams((value) => ({
              ...value,
              current: pageSize === value.pageSize ? current : 1,
              pageSize,
            })),
        }}
        onChange={(_page, _filters, sorter, extra) => {
          if (extra.action !== 'sort') return;
          const value = Array.isArray(sorter) ? sorter[0] : sorter;
          setParams((old) => ({
            ...old,
            current: 1,
            sortField: (value.columnKey ||
              'username') as UserQuery['sortField'],
            sortOrder: value.order || 'ascend',
          }));
        }}
      />
      <UserEditor
        open={Boolean(editor)}
        user={editor?.user}
        onClose={() => setEditor(undefined)}
        onSaved={saved}
      />
      <ChangePasswordModal
        open={Boolean(passwordUser)}
        user={passwordUser}
        onClose={() => setPasswordUser(undefined)}
        onSaved={saved}
      />
    </PageContainer>
  );
}
