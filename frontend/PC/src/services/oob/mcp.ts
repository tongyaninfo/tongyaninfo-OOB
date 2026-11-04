import { apiRequest } from './http';
import type {
  McpCallLogDto,
  McpCallLogPageData,
  McpClientDto,
  McpClientPageData,
  McpClientQuery,
  McpClientRequest,
  McpConfigurationDto,
  McpConfigurationRequest,
  McpLogQuery,
  McpOptionsDto,
  McpSecretDto,
  McpToolDto,
  McpVersionRequest,
} from './models';

const root = '/api/mcp';
const queryString = (query: object) => {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query))
    if (value !== undefined && value !== null && value !== '')
      params.set(key, String(value));
  return params;
};
export const getMcpConfiguration = (signal?: AbortSignal) =>
  apiRequest<McpConfigurationDto>(`${root}/configuration`, { signal });
export const saveMcpConfiguration = (values: McpConfigurationRequest) =>
  apiRequest<McpConfigurationDto>(`${root}/configuration`, {
    method: 'PUT',
    body: JSON.stringify(values),
  });
export const setMcpEnabled = (enabled: boolean, values: McpVersionRequest) =>
  apiRequest<McpConfigurationDto>(
    `${root}/configuration/${enabled ? 'enable' : 'disable'}`,
    { method: 'POST', body: JSON.stringify(values) },
  );
export const getMcpTools = (signal?: AbortSignal) =>
  apiRequest<McpToolDto[]>(`${root}/tools`, { signal });
export const getMcpOptions = (signal?: AbortSignal) =>
  apiRequest<McpOptionsDto>(`${root}/options`, { signal });
export const getMcpClients = (query: McpClientQuery, signal?: AbortSignal) =>
  apiRequest<McpClientPageData>(`${root}/clients?${queryString(query)}`, {
    signal,
  });
export const getMcpClient = (id: string, signal?: AbortSignal) =>
  apiRequest<McpClientDto>(`${root}/clients/${encodeURIComponent(id)}`, {
    signal,
  });
export const saveMcpClient = (values: McpClientRequest, id?: string) =>
  apiRequest<McpClientDto>(
    id ? `${root}/clients/${encodeURIComponent(id)}` : `${root}/clients`,
    { method: id ? 'PUT' : 'POST', body: JSON.stringify(values) },
  );
/** 启停、撤销与轮换使用各自显式意图，不把保存配置当作授权。 */
export const changeMcpClient = (
  id: string,
  action: 'enable' | 'disable' | 'revoke',
  values: McpVersionRequest,
) =>
  apiRequest<McpClientDto>(
    `${root}/clients/${encodeURIComponent(id)}/${action}`,
    { method: 'POST', body: JSON.stringify(values) },
  );
export const rotateMcpSecret = (id: string, values: McpVersionRequest) =>
  apiRequest<McpSecretDto>(
    `${root}/clients/${encodeURIComponent(id)}/rotate-secret`,
    { method: 'POST', body: JSON.stringify(values), reportUnexpected: false },
  );
export const getMcpLogs = (query: McpLogQuery, signal?: AbortSignal) =>
  apiRequest<McpCallLogPageData>(`${root}/logs?${queryString(query)}`, {
    signal,
  });
export const getMcpLog = (id: string, signal?: AbortSignal) =>
  apiRequest<McpCallLogDto>(`${root}/logs/${encodeURIComponent(id)}`, {
    signal,
  });
