import { apiRequest } from './http';
import type {
  MailConfigurationDto,
  MailConfigurationRequest,
  MailMessageDto,
  MailMessagePageData,
  MailProbeDto,
  MailSendRequest,
} from './models';

export type MailQuery = {
  keyword?: string;
  status?: string;
  kind?: string;
  current: number;
  pageSize: number;
  sortField:
    | 'toAddress'
    | 'subject'
    | 'kind'
    | 'status'
    | 'createdAt'
    | 'completedAt';
  sortOrder: 'ascend' | 'descend';
};

/** 邮件写请求不自动重试，未知结果只查询原请求；正文与密码不进入客户端持久存储。 */
export const getMailConfiguration = (signal?: AbortSignal) =>
  apiRequest<MailConfigurationDto>('/api/oob/mail/configuration', { signal });
export const saveMailConfiguration = (values: MailConfigurationRequest) =>
  apiRequest<MailConfigurationDto>('/api/oob/mail/configuration', {
    method: 'PUT',
    body: JSON.stringify(values),
  });
export const probeMailConnection = () =>
  apiRequest<MailProbeDto>('/api/oob/mail/probe', { method: 'POST' });
export const sendMail = (values: MailSendRequest, test = false) =>
  apiRequest<MailMessageDto>(`/api/oob/mail/${test ? 'test' : 'send'}`, {
    method: 'POST',
    body: JSON.stringify(values),
  });
export const getMailRequest = (requestId: string, signal?: AbortSignal) =>
  apiRequest<MailMessageDto>(
    `/api/oob/mail/requests/${encodeURIComponent(requestId)}`,
    { signal },
  );
export const getMailMessage = (id: string, signal?: AbortSignal) =>
  apiRequest<MailMessageDto>(
    `/api/oob/mail/messages/${encodeURIComponent(id)}`,
    {
      signal,
    },
  );
export function getMailMessages(query: MailQuery, signal?: AbortSignal) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query))
    if (value !== undefined && value !== '') params.set(key, String(value));
  return apiRequest<MailMessagePageData>(`/api/oob/mail/messages?${params}`, {
    signal,
  });
}
