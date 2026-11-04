import { apiRequest } from './http';
import type {
  ClientExceptionRequest,
  ConnectionTestResult,
  DashboardDto,
  DeviceInventory,
  ExceptionLogDto,
  ExceptionPageData,
  ExportServerConfigurationRequest,
  HistorySeriesDto,
  ImportServerConfigurationRequest,
  MailReportConfigurationDto,
  MailReportConfigurationRequest,
  MailReportPreviewDto,
  MailReportPreviewRequest,
  PowerCapabilitiesDto,
  PowerOperationDto,
  PowerRequest,
  PowerReviewRequest,
  RecentTelemetryDto,
  SaveServerRequest,
  ServerConfigurationFile,
  ServerConfigurationImportResult,
  ServerDetailDto,
  ServerDto,
  ServerPageData,
  SetUidRequest,
  TelemetryDto,
  UidStatusDto,
} from './models';

/** 所有读操作只取后端状态；采样与电源动作只由明确的按钮事件调用。 */
export const getDashboard = (signal?: AbortSignal) =>
  apiRequest<DashboardDto>('/api/oob/dashboard', { signal });

export function getServers(
  params: { current?: number; pageSize?: number; keyword?: string },
  signal?: AbortSignal,
) {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params))
    if (value !== undefined && value !== '') query.set(key, String(value));
  return apiRequest<ServerPageData>(`/api/oob/servers?${query}`, {
    signal,
  });
}

export const getServer = (id: string, signal?: AbortSignal) =>
  apiRequest<ServerDetailDto>(`/api/oob/servers/${encodeURIComponent(id)}`, {
    signal,
  });

export const saveServer = (values: SaveServerRequest, id?: string) =>
  apiRequest<ServerDto>(
    id ? `/api/oob/servers/${encodeURIComponent(id)}` : '/api/oob/servers',
    {
      method: id ? 'PUT' : 'POST',
      body: JSON.stringify(values),
    },
  );

/** 导出全集密文；文件密码只随本次明确请求发送，不保存在浏览器。 */
export const exportServerConfiguration = (
  values: ExportServerConfigurationRequest,
  signal?: AbortSignal,
) =>
  apiRequest<ServerConfigurationFile>('/api/oob/servers/configuration/export', {
    method: 'POST',
    body: JSON.stringify(values),
    signal,
  });

/** 服务端按地址新增、覆盖或跳过；请求层不自动重发导入。 */
export const importServerConfiguration = (
  values: ImportServerConfigurationRequest,
  signal?: AbortSignal,
) =>
  apiRequest<ServerConfigurationImportResult>(
    '/api/oob/servers/configuration/import',
    { method: 'POST', body: JSON.stringify(values), signal },
  );

export const testConnection = (values: SaveServerRequest) =>
  apiRequest<ConnectionTestResult>('/api/oob/servers/test-connection', {
    method: 'POST',
    body: JSON.stringify(values),
  });

export const refreshInventory = (id: string) =>
  apiRequest<DeviceInventory>(
    `/api/oob/servers/${encodeURIComponent(id)}/refresh-inventory`,
    { method: 'POST' },
  );

export const getTelemetry = (id: string, signal?: AbortSignal) =>
  apiRequest<TelemetryDto>(
    `/api/oob/servers/${encodeURIComponent(id)}/telemetry`,
    { signal },
  );

/** UID读取仅查询设备状态；写入只由明确点击动作触发，统一请求层不自动重发。 */
export const getUid = (id: string, signal?: AbortSignal) =>
  apiRequest<UidStatusDto>(`/api/oob/servers/${encodeURIComponent(id)}/uid`, {
    signal,
  });

export const setUid = (id: string, body: SetUidRequest) =>
  apiRequest<UidStatusDto>(`/api/oob/servers/${encodeURIComponent(id)}/uid`, {
    method: 'PUT',
    body: JSON.stringify(body),
  });

export const sampleTelemetry = (id: string) =>
  apiRequest<TelemetryDto>(
    `/api/oob/servers/${encodeURIComponent(id)}/sample`,
    { method: 'POST' },
  );

export function getHistory(
  id: string,
  params: {
    from: string;
    to: string;
    metricCode?: string;
    bucketSeconds?: number;
  },
  signal?: AbortSignal,
) {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params))
    if (value !== undefined && value !== '') query.set(key, String(value));
  return apiRequest<HistorySeriesDto[]>(
    `/api/oob/servers/${encodeURIComponent(id)}/history?${query}`,
    { signal },
  );
}

/** 只读探测当前支持的电源动作、全局开关和电源状态，不创建电源意图。 */
export const getPowerCapabilities = (id: string, signal?: AbortSignal) =>
  apiRequest<PowerCapabilitiesDto>(
    `/api/oob/servers/${encodeURIComponent(id)}/power-capabilities`,
    { signal },
  );

/** 每次明确意图生成一个稳定 requestId；未知结果保留原 ID，不自动重发。 */
export const executePowerAction = (id: string, body: PowerRequest) =>
  apiRequest<PowerOperationDto>(
    `/api/oob/servers/${encodeURIComponent(id)}/power`,
    { method: 'POST', body: JSON.stringify(body) },
  );

/** 人工核对只记录操作者确认的结果和依据，不调用或重发任何设备电源动作。 */
export const reviewPowerOperation = (
  id: string,
  operationId: string,
  body: PowerReviewRequest,
) =>
  apiRequest<PowerOperationDto>(
    `/api/oob/servers/${encodeURIComponent(id)}/power-operations/${encodeURIComponent(operationId)}/review`,
    { method: 'POST', body: JSON.stringify(body) },
  );

export const getPowerOperations = (id: string, signal?: AbortSignal) =>
  apiRequest<PowerOperationDto[]>(
    `/api/oob/servers/${encodeURIComponent(id)}/power-operations`,
    { signal },
  );

export const getPreferences = () =>
  apiRequest<Record<string, unknown>>('/api/preferences');
export const savePreferences = (values: Record<string, unknown>) =>
  apiRequest<Record<string, unknown>>('/api/preferences', {
    method: 'PUT',
    body: JSON.stringify(values),
  });

/** 总览读取实际采样点与已记录的失败区间，不补造固定时间桶。 */
export function getRecent(
  id: string,
  params: { from: string; to: string },
  signal?: AbortSignal,
) {
  return apiRequest<RecentTelemetryDto>(
    `/api/oob/servers/${encodeURIComponent(id)}/recent?${new URLSearchParams(params)}`,
    { signal },
  );
}
export function getExceptions(
  params: {
    current?: number;
    pageSize?: number;
    from?: string;
    to?: string;
    kind?: string;
    level?: string;
    source?: string;
    serverId?: string;
    keyword?: string;
  },
  signal?: AbortSignal,
) {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params))
    if (value !== undefined && value !== '') query.set(key, String(value));
  return apiRequest<ExceptionPageData>(`/api/oob/exceptions?${query}`, {
    signal,
  });
}
export const getException = (id: string, signal?: AbortSignal) =>
  apiRequest<ExceptionLogDto>(`/api/oob/exceptions/${encodeURIComponent(id)}`, {
    signal,
  });
export const reportClientException = (
  body: ClientExceptionRequest,
  signal?: AbortSignal,
) =>
  apiRequest<boolean>('/api/oob/exceptions/client', {
    method: 'POST',
    body: JSON.stringify(body),
    reportUnexpected: false,
    signal,
  });

/** 读取与预览没有发件副作用；计划保存不自动重试，未知结果先重新读取核对。 */
export const getMailReportConfiguration = (signal?: AbortSignal) =>
  apiRequest<MailReportConfigurationDto>(
    '/api/oob/mail/reports/configuration',
    {
      signal,
    },
  );
export const saveMailReportConfiguration = (
  values: MailReportConfigurationRequest,
) =>
  apiRequest<MailReportConfigurationDto>(
    '/api/oob/mail/reports/configuration',
    {
      method: 'PUT',
      body: JSON.stringify(values),
    },
  );
export const previewMailReport = (
  values: MailReportPreviewRequest,
  signal?: AbortSignal,
) =>
  apiRequest<MailReportPreviewDto>('/api/oob/mail/reports/preview', {
    method: 'POST',
    body: JSON.stringify(values),
    signal,
  });
