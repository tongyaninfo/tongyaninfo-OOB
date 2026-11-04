import { getServers } from './api';
import { apiRequest, type BinaryUploadProgress, uploadBinary } from './http';
import type {
  ConsoleCapabilitiesDto,
  ConsoleSessionDto,
  EjectVirtualMediaRequest,
  MountVirtualMediaRequest,
  ReviewVirtualMediaRequest,
  ServerDto,
  VirtualMediaImageDto,
  VirtualMediaImagePageData,
  VirtualMediaOperationDto,
  VirtualMediaStatusDto,
  VirtualMediaTrafficDto,
} from './models';

const serverPath = (id: string) => `/api/oob/servers/${encodeURIComponent(id)}`;
const sessionPath = (id: string) =>
  `/api/oob/console-sessions/${encodeURIComponent(id)}`;
export const getConsoleCapabilities = (id: string, signal?: AbortSignal) =>
  apiRequest<ConsoleCapabilitiesDto>(`${serverPath(id)}/console-capabilities`, {
    signal,
  });
export const createConsoleSession = (id: string, requestId: string) =>
  apiRequest<ConsoleSessionDto>(`${serverPath(id)}/console-sessions`, {
    method: 'POST',
    body: JSON.stringify({ requestId }),
  });
export const findConsoleSession = (requestId: string) =>
  apiRequest<ConsoleSessionDto>(
    `/api/oob/console-sessions/by-request/${encodeURIComponent(requestId)}`,
  );
export const getConsoleTraffic = (id: string, signal?: AbortSignal) =>
  apiRequest<ConsoleSessionDto>(`${sessionPath(id)}/traffic`, { signal });
export const closeConsoleSession = (id: string) =>
  apiRequest<ConsoleSessionDto>(`${sessionPath(id)}/close`, {
    method: 'POST',
    body: '{}',
  });
export const setConsoleInput = (id: string, enabled: boolean) =>
  apiRequest<ConsoleSessionDto>(`${sessionPath(id)}/input-state`, {
    method: 'POST',
    body: JSON.stringify({ enabled }),
  });

/** 选择器一次读完所有分页；重复、缺页或变化的总数不能冒充完整目录。 */
export async function getConsoleServers(
  signal?: AbortSignal,
): Promise<ServerDto[]> {
  const items = new Map<string, ServerDto>();
  let total: number | undefined;
  for (let page = 1; page <= 10000; page++) {
    const result = await getServers({ current: page, pageSize: 100 }, signal);
    if (!result.success) throw new Error(result.errorMessage);
    if (total !== undefined && total !== result.data.total)
      throw new Error('SERVER_DIRECTORY_CHANGED');
    total = result.data.total;
    if (
      result.data.current !== page ||
      (!result.data.items.length && items.size < total)
    )
      throw new Error('SERVER_DIRECTORY_INCOMPLETE');
    for (const server of result.data.items) {
      if (items.has(server.id)) throw new Error('SERVER_DIRECTORY_DUPLICATED');
      items.set(server.id, server);
    }
    if (items.size === total) return [...items.values()];
    if (items.size > total) throw new Error('SERVER_DIRECTORY_INCOMPLETE');
  }
  throw new Error('SERVER_DIRECTORY_INCOMPLETE');
}
export const getMediaImages = (current = 1, signal?: AbortSignal) =>
  apiRequest<VirtualMediaImagePageData>(
    `/api/oob/media-images?current=${current}&pageSize=100`,
    { signal },
  );
export const uploadMediaImage = (
  file: File,
  signal?: AbortSignal,
  onProgress?: (progress: BinaryUploadProgress) => void,
) =>
  uploadBinary<VirtualMediaImageDto>(
    `/api/oob/media-images?fileName=${encodeURIComponent(file.name)}`,
    file,
    onProgress,
    signal,
  );
export const getVirtualMedia = (id: string, signal?: AbortSignal) =>
  apiRequest<VirtualMediaStatusDto>(`${serverPath(id)}/virtual-media`, {
    signal,
  });
export const getVirtualMediaTraffic = (id: string, signal?: AbortSignal) =>
  apiRequest<VirtualMediaTrafficDto>(
    `${serverPath(id)}/virtual-media/traffic`,
    { signal },
  );
export const mountVirtualMedia = (id: string, body: MountVirtualMediaRequest) =>
  apiRequest<VirtualMediaOperationDto>(
    `${serverPath(id)}/virtual-media/mount`,
    { method: 'POST', body: JSON.stringify(body) },
  );
export const ejectVirtualMedia = (id: string, body: EjectVirtualMediaRequest) =>
  apiRequest<VirtualMediaOperationDto>(
    `${serverPath(id)}/virtual-media/eject`,
    { method: 'POST', body: JSON.stringify(body) },
  );
export const findVirtualMediaOperation = (id: string, requestId: string) =>
  apiRequest<VirtualMediaOperationDto>(
    `${serverPath(id)}/virtual-media/operations/${encodeURIComponent(requestId)}`,
  );
export const reviewVirtualMediaNotExecuted = (
  id: string,
  requestId: string,
  body: ReviewVirtualMediaRequest,
) =>
  apiRequest<VirtualMediaOperationDto>(
    `${serverPath(id)}/virtual-media/operations/${encodeURIComponent(requestId)}/review-not-executed`,
    { method: 'POST', body: JSON.stringify(body) },
  );
