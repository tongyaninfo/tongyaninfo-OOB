import { apiRequest } from './http';
import type {
  ResetPasswordRequest,
  SaveUserRequest,
  UserDto,
  UserPageData,
} from './models';

export type UserQuery = {
  keyword?: string;
  status?: 'all' | 'enabled' | 'disabled' | 'deleted';
  current: number;
  pageSize: number;
  sortField: 'username' | 'displayName' | 'createdAt' | 'updatedAt';
  sortOrder: 'ascend' | 'descend';
};
/** 分页与排序交由服务端，长 ID 和并发版本全程保留十进制字符串。 */
export function getUsers(query: UserQuery, signal?: AbortSignal) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query))
    if (value !== undefined && value !== '') params.set(key, String(value));
  return apiRequest<UserPageData>(`/api/users?${params}`, { signal });
}
export const saveUser = (values: SaveUserRequest, id?: string) =>
  apiRequest<UserDto>(
    id ? `/api/users/${encodeURIComponent(id)}` : '/api/users',
    { method: id ? 'PUT' : 'POST', body: JSON.stringify(values) },
  );
export const deleteUser = (user: UserDto) =>
  apiRequest<UserDto>(`/api/users/${encodeURIComponent(user.id)}/delete`, {
    method: 'POST',
    body: JSON.stringify({ version: user.version }),
  });
export const resetUserPassword = (id: string, values: ResetPasswordRequest) =>
  apiRequest<UserDto>(`/api/users/${encodeURIComponent(id)}/reset-password`, {
    method: 'POST',
    body: JSON.stringify(values),
  });
