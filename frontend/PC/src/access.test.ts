import { describe, expect, it } from 'vitest';
import access from './access';

describe('access', () => {
  it('should return canAdmin true when user has admin access', () => {
    const initialState = {
      currentUser: {
        userid: '1',
        name: 'Admin User',
        avatar: 'https://example.com/avatar.png',
        access: 'admin',
        isAdministrator: true,
      },
    };

    const result = access(initialState);

    expect(result.canAdmin).toBe(true);
  });

  it('should return canAdmin false when user has non-admin access', () => {
    const initialState = {
      currentUser: {
        userid: '2',
        name: 'Regular User',
        avatar: 'https://example.com/avatar.png',
        access: 'user',
      },
    };

    const result = access(initialState);

    expect(result.canAdmin).toBe(false);
  });

  it('should return canAdmin false when user access is undefined', () => {
    const initialState = {
      currentUser: {
        userid: '3',
        name: 'Guest User',
        avatar: 'https://example.com/avatar.png',
      },
    };

    const result = access(initialState);

    expect(result.canAdmin).toBe(false);
  });

  it('should return canAdmin false when currentUser is undefined', () => {
    const initialState = {
      currentUser: undefined,
    };

    const result = access(initialState);

    expect(result.canAdmin).toBeFalsy();
  });

  it('should return canAdmin false when initialState is undefined', () => {
    const result = access(undefined);

    expect(result.canAdmin).toBeFalsy();
  });
  it.each([
    [false, false],
    [true, false],
    [false, true],
    [true, true],
  ])('两项勾选权限独立生效：用户管理 %s，控制台 %s', (canManageUsers, canUseConsole) => {
    expect(
      access({
        currentUser: { canManageUsers, canUseConsole, isAdministrator: false },
      }),
    ).toEqual({
      canAdmin: false,
      canManageUsers,
      canUseConsole,
      canManageServers: false,
      canOperatePower: false,
    });
  });
  it('系统管理员不能绕过未勾选权限，也不从旧access字符串推导设备能力', () => {
    expect(
      access({
        currentUser: {
          isAdministrator: true,
          canManageUsers: false,
          canUseConsole: false,
        },
      }),
    ).toEqual({
      canAdmin: true,
      canManageUsers: false,
      canUseConsole: false,
      canManageServers: false,
      canOperatePower: false,
    });
    expect(access({ currentUser: { access: 'admin' } }).canAdmin).toBe(false);
  });
  it.each([
    false,
    true,
  ])('服务器和电源权限独立于系统管理员标记 %s', (isAdministrator) => {
    for (const canManageServers of [false, true])
      for (const canOperatePower of [false, true]) {
        expect(
          access({
            currentUser: { isAdministrator, canManageServers, canOperatePower },
          }),
        ).toEqual({
          canAdmin: isAdministrator,
          canManageUsers: false,
          canUseConsole: false,
          canManageServers,
          canOperatePower,
        });
      }
  });
});
