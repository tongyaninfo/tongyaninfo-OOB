/**
 * @see https://umijs.org/docs/max/access#access
 * */
import type { CurrentUserDto } from '@/services/oob/models';

export default function access(
  initialState: { currentUser?: Partial<CurrentUserDto> } | undefined,
) {
  const { currentUser } = initialState ?? {};
  return {
    canAdmin: currentUser?.isAdministrator === true,
    canManageUsers: currentUser?.canManageUsers === true,
    canUseConsole: currentUser?.canUseConsole === true,
    canManageServers: currentUser?.canManageServers === true,
    canOperatePower: currentUser?.canOperatePower === true,
  };
}
