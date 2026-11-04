/** 后端路由与 JSON Schema 同源生成，禁止手改；生成器 json-schema-to-typescript 16.0.0。 */

export type PowerAction =
  | 'On'
  | 'GracefulShutdown'
  | 'ForceOff'
  | 'GracefulRestart'
  | 'ForceRestart'
  | 'PowerCycle'
  | 'PushPowerButton';
export type UidControlState = 'Off' | 'Lit' | 'Blinking';
export type UidState = 'Off' | 'Lit' | 'Blinking' | 'Unknown';

/**
 * 后端DTO与实际JSON序列化的离线合同；draft-07兼容子集，类型约束不替代服务运行时校验
 */
export interface components {
  schemas: {
    BooleanApiResult: BooleanApiResult;
    ChangePasswordRequest: ChangePasswordRequest;
    ClientExceptionRequest: ClientExceptionRequest;
    CollectionGapDto: CollectionGapDto;
    ConnectionTestResult: ConnectionTestResult;
    ConnectionTestResultApiResult: ConnectionTestResultApiResult;
    ConsoleCapabilitiesDto: ConsoleCapabilitiesDto;
    ConsoleCapabilitiesDtoApiResult: ConsoleCapabilitiesDtoApiResult;
    ConsoleInputStateRequest: ConsoleInputStateRequest;
    ConsoleSessionDto: ConsoleSessionDto;
    ConsoleSessionDtoApiResult: ConsoleSessionDtoApiResult;
    ConsoleSessionRequest: ConsoleSessionRequest;
    CsrfDto: CsrfDto;
    CsrfDtoApiResult: CsrfDtoApiResult;
    CurrentUserDto: CurrentUserDto;
    CurrentUserDtoApiResult: CurrentUserDtoApiResult;
    DashboardDto: DashboardDto;
    DashboardDtoApiResult: DashboardDtoApiResult;
    DeviceCapabilities: DeviceCapabilities;
    DeviceIdentity: DeviceIdentity;
    DeviceInventory: DeviceInventory;
    DeviceInventoryApiResult: DeviceInventoryApiResult;
    EjectVirtualMediaRequest: EjectVirtualMediaRequest;
    ExceptionLogDto: ExceptionLogDto;
    ExceptionLogDtoApiResult: ExceptionLogDtoApiResult;
    ExceptionLogDtoPageData: ExceptionLogDtoPageData;
    ExceptionLogDtoPageDataApiResult: ExceptionLogDtoPageDataApiResult;
    ExceptionLogQuery: ExceptionLogQuery;
    ExportServerConfigurationRequest: ExportServerConfigurationRequest;
    HardwareComponent: HardwareComponent;
    HistoryPointDto: HistoryPointDto;
    HistorySeriesDto: HistorySeriesDto;
    HistorySeriesDtoIReadOnlyListApiResult: HistorySeriesDtoIReadOnlyListApiResult;
    ImportServerConfigurationRequest: ImportServerConfigurationRequest;
    JsonElementApiResult: JsonElementApiResult;
    LoginRequest: LoginRequest;
    MailConfigurationDto: MailConfigurationDto;
    MailConfigurationDtoApiResult: MailConfigurationDtoApiResult;
    MailConfigurationRequest: MailConfigurationRequest;
    MailMessageDto: MailMessageDto;
    MailMessageDtoApiResult: MailMessageDtoApiResult;
    MailMessageDtoPageData: MailMessageDtoPageData;
    MailMessageDtoPageDataApiResult: MailMessageDtoPageDataApiResult;
    MailMessageQuery: MailMessageQuery;
    MailProbeDto: MailProbeDto;
    MailProbeDtoApiResult: MailProbeDtoApiResult;
    MailReportConfigurationDto: MailReportConfigurationDto;
    MailReportConfigurationDtoApiResult: MailReportConfigurationDtoApiResult;
    MailReportConfigurationRequest: MailReportConfigurationRequest;
    MailReportPreviewDto: MailReportPreviewDto;
    MailReportPreviewDtoApiResult: MailReportPreviewDtoApiResult;
    MailReportPreviewRequest: MailReportPreviewRequest;
    MailReportScheduleDto: MailReportScheduleDto;
    MailReportScheduleRequest: MailReportScheduleRequest;
    MailSendRequest: MailSendRequest;
    McpCallLogDto: McpCallLogDto;
    McpCallLogDtoApiResult: McpCallLogDtoApiResult;
    McpCallLogDtoPageData: McpCallLogDtoPageData;
    McpCallLogDtoPageDataApiResult: McpCallLogDtoPageDataApiResult;
    McpClientDto: McpClientDto;
    McpClientDtoApiResult: McpClientDtoApiResult;
    McpClientDtoPageData: McpClientDtoPageData;
    McpClientDtoPageDataApiResult: McpClientDtoPageDataApiResult;
    McpClientQuery: McpClientQuery;
    McpClientRequest: McpClientRequest;
    McpConfigAuditDto: McpConfigAuditDto;
    McpConfigAuditDtoPageData: McpConfigAuditDtoPageData;
    McpConfigAuditDtoPageDataApiResult: McpConfigAuditDtoPageDataApiResult;
    McpConfigurationDto: McpConfigurationDto;
    McpConfigurationDtoApiResult: McpConfigurationDtoApiResult;
    McpConfigurationRequest: McpConfigurationRequest;
    McpLogQuery: McpLogQuery;
    McpOptionsDto: McpOptionsDto;
    McpOptionsDtoApiResult: McpOptionsDtoApiResult;
    McpSecretDto: McpSecretDto;
    McpSecretDtoApiResult: McpSecretDtoApiResult;
    McpServerOptionDto: McpServerOptionDto;
    McpToolDto: McpToolDto;
    McpToolDtoIReadOnlyListApiResult: McpToolDtoIReadOnlyListApiResult;
    McpUserOptionDto: McpUserOptionDto;
    McpVersionRequest: McpVersionRequest;
    MetricReading: MetricReading;
    MountVirtualMediaRequest: MountVirtualMediaRequest;
    PowerAction: PowerAction;
    PowerCapabilitiesDto: PowerCapabilitiesDto;
    PowerCapabilitiesDtoApiResult: PowerCapabilitiesDtoApiResult;
    PowerOperationDto: PowerOperationDto;
    PowerOperationDtoApiResult: PowerOperationDtoApiResult;
    PowerOperationDtoIReadOnlyListApiResult: PowerOperationDtoIReadOnlyListApiResult;
    PowerRequest: PowerRequest;
    PowerReviewRequest: PowerReviewRequest;
    RecentTelemetryDto: RecentTelemetryDto;
    RecentTelemetryDtoApiResult: RecentTelemetryDtoApiResult;
    ResetUserPasswordRequest: ResetUserPasswordRequest;
    ReviewVirtualMediaRequest: ReviewVirtualMediaRequest;
    SaveServerRequest: SaveServerRequest;
    SaveUserRequest: SaveUserRequest;
    ServerConfigurationFile: ServerConfigurationFile;
    ServerConfigurationFileApiResult: ServerConfigurationFileApiResult;
    ServerConfigurationImportResult: ServerConfigurationImportResult;
    ServerConfigurationImportResultApiResult: ServerConfigurationImportResultApiResult;
    ServerDetailDto: ServerDetailDto;
    ServerDetailDtoApiResult: ServerDetailDtoApiResult;
    ServerDto: ServerDto;
    ServerDtoApiResult: ServerDtoApiResult;
    ServerDtoPageData: ServerDtoPageData;
    ServerDtoPageDataApiResult: ServerDtoPageDataApiResult;
    ServerTelemetryDto: ServerTelemetryDto;
    SetUidRequest: SetUidRequest;
    TelemetryDto: TelemetryDto;
    TelemetryDtoApiResult: TelemetryDtoApiResult;
    UidControlState: UidControlState;
    UidState: UidState;
    UidStatusDto: UidStatusDto;
    UidStatusDtoApiResult: UidStatusDtoApiResult;
    UserDto: UserDto;
    UserDtoApiResult: UserDtoApiResult;
    UserDtoPageData: UserDtoPageData;
    UserDtoPageDataApiResult: UserDtoPageDataApiResult;
    UserVersionRequest: UserVersionRequest;
    VirtualMediaImageDto: VirtualMediaImageDto;
    VirtualMediaImageDtoApiResult: VirtualMediaImageDtoApiResult;
    VirtualMediaImageDtoPageData: VirtualMediaImageDtoPageData;
    VirtualMediaImageDtoPageDataApiResult: VirtualMediaImageDtoPageDataApiResult;
    VirtualMediaOperationDto: VirtualMediaOperationDto;
    VirtualMediaOperationDtoApiResult: VirtualMediaOperationDtoApiResult;
    VirtualMediaSlotDto: VirtualMediaSlotDto;
    VirtualMediaStatusDto: VirtualMediaStatusDto;
    VirtualMediaStatusDtoApiResult: VirtualMediaStatusDtoApiResult;
    VirtualMediaTrafficDto: VirtualMediaTrafficDto;
    VirtualMediaTrafficDtoApiResult: VirtualMediaTrafficDtoApiResult;
  };
}
export interface BooleanApiResult {
  success: boolean;
  data: boolean;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
  confirmPassword: string;
}
export interface ClientExceptionRequest {
  eventType: string;
  exceptionType?: string | null;
  stackTrace?: string | null;
  path?: string | null;
}
export interface CollectionGapDto {
  from: string;
  to: string;
}
export interface ConnectionTestResult {
  success: boolean;
  identity: DeviceIdentity;
  capabilities: DeviceCapabilities;
  errorCode: string | null;
  errorMessage: string | null;
  warnings: string[];
}
export interface DeviceIdentity {
  manufacturer: string | null;
  model: string | null;
  serialNumber: string | null;
  hostName: string | null;
  controllerHostName: string | null;
  controllerType: string | null;
  firmwareVersion: string | null;
  powerState: string | null;
}
export interface DeviceCapabilities {
  protocol: string;
  supportsInventory: boolean;
  supportedMetrics: string[];
  powerActions: PowerAction[];
  warnings: string[];
}
export interface ConnectionTestResultApiResult {
  success: boolean;
  data: ConnectionTestResult | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface ConsoleCapabilitiesDto {
  serverId: string;
  controllerType: string;
  status: string;
  mode: string | null;
  keyboardSupported: boolean;
  mouseSupported: boolean;
  reasonCode: string | null;
  message: string | null;
  localMediaSupported: boolean;
}
export interface ConsoleCapabilitiesDtoApiResult {
  success: boolean;
  data: ConsoleCapabilitiesDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface ConsoleInputStateRequest {
  enabled: boolean;
}
export interface ConsoleSessionDto {
  id: string;
  requestId: string;
  serverId: string;
  serverName: string;
  controllerType: string;
  status: string;
  createdAt: string;
  expiresAt: string;
  frameUrl: string | null;
  errorCode: string | null;
  message: string | null;
  inputEnabled: boolean;
  /**
   * 十进制long文本；客户端不得转number
   */
  bytesReceived: string;
  /**
   * 十进制long文本；客户端不得转number
   */
  bytesSent: string;
  bridgeProtocol: string | null;
  trafficAvailable: boolean;
  mediaBytesReceived: string;
  mediaBytesSent: string;
}
export interface ConsoleSessionDtoApiResult {
  success: boolean;
  data: ConsoleSessionDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface ConsoleSessionRequest {
  requestId: string;
}
export interface CsrfDto {
  token: string;
}
export interface CsrfDtoApiResult {
  success: boolean;
  data: CsrfDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface CurrentUserDto {
  userid: string;
  name: string;
  access: string;
  avatar: string;
  username: string;
  isAdministrator: boolean;
  canManageUsers: boolean;
  canUseConsole: boolean;
  canManageServers: boolean;
  canOperatePower: boolean;
}
export interface CurrentUserDtoApiResult {
  success: boolean;
  data: CurrentUserDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface DashboardDto {
  totalServers: number;
  onlineServers: number;
  offlineServers: number;
  pendingServers: number;
  totalPowerWatts: number | null;
  generatedAt: string;
  servers: ServerDto[];
  telemetrySnapshots: ServerTelemetryDto[];
}
export interface ServerDto {
  id: string;
  name: string;
  brand: string;
  controllerType: string;
  address: string;
  username: string;
  samplingIntervalSeconds: number;
  enabled: boolean;
  certificateFingerprint: string | null;
  identity: DeviceIdentity;
  connectionStatus: string;
  lastCollectedAt: string | null;
  lastInventoryAt: string | null;
  lastError: string | null;
  createdAt: string;
  updatedAt: string;
  ignoreCertificateErrors: boolean;
}
export interface ServerTelemetryDto {
  server: ServerDto;
  telemetry: TelemetryDto;
  publishedAt: string;
}
export interface TelemetryDto {
  collectedAt: string | null;
  stale: boolean;
  metrics: MetricReading[];
  warnings: string[];
}
export interface MetricReading {
  sensorKey: string;
  name: string;
  metricCode: string;
  value: number | null;
  unit: string;
  availability: string;
  source: string;
  health: string | null;
  observedAt: string;
  deviceTime: string | null;
}
export interface DashboardDtoApiResult {
  success: boolean;
  data: DashboardDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface DeviceInventory {
  identity: DeviceIdentity;
  collectedAt: string;
  components: HardwareComponent[];
  warnings: string[];
}
export interface HardwareComponent {
  key: string;
  kind: string;
  name: string;
  model: string | null;
  manufacturer: string | null;
  serialNumber: string | null;
  status: string | null;
  /**
   * 十进制long文本；客户端不得转number
   */
  capacityBytes: string | null;
  attributes: {
    [k: string]: string | null;
  };
  children: HardwareComponent[];
}
export interface DeviceInventoryApiResult {
  success: boolean;
  data: DeviceInventory | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface EjectVirtualMediaRequest {
  slotId: string;
  confirmation: string;
  requestId: string;
}
export interface ExceptionLogDto {
  id: string;
  occurredAt: string;
  kind: string;
  level: string;
  source: string;
  operation: string;
  errorCode: string;
  message: string;
  exceptionType: string | null;
  stackTrace: string | null;
  traceId: string | null;
  serverId: string | null;
  userId: string | null;
  requestPath: string | null;
  requestMethod: string | null;
  httpStatusCode: number | null;
}
export interface ExceptionLogDtoApiResult {
  success: boolean;
  data: ExceptionLogDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface ExceptionLogDtoPageData {
  items: ExceptionLogDto[];
  total: number;
  current: number;
  pageSize: number;
}
export interface ExceptionLogDtoPageDataApiResult {
  success: boolean;
  data: ExceptionLogDtoPageData | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface ExceptionLogQuery {
  current: number;
  pageSize: number;
  from?: string | null;
  to?: string | null;
  kind?: string | null;
  level?: string | null;
  source?: string | null;
  /**
   * 十进制long文本；客户端不得转number
   */
  serverId?: string | null;
  keyword?: string | null;
}
export interface ExportServerConfigurationRequest {
  password: string;
}
export interface HistoryPointDto {
  time: string;
  average: number | null;
  minimum: number | null;
  maximum: number | null;
  count: number;
}
export interface HistorySeriesDto {
  sensorKey: string;
  name: string;
  metricCode: string;
  unit: string;
  points: HistoryPointDto[];
}
export interface HistorySeriesDtoIReadOnlyListApiResult {
  success: boolean;
  data: HistorySeriesDto[] | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface ImportServerConfigurationRequest {
  password: string;
  file: ServerConfigurationFile;
}
export interface ServerConfigurationFile {
  format: string;
  version: number;
  salt: string;
  nonce: string;
  ciphertext: string;
  tag: string;
}
export interface JsonElementApiResult {
  success: boolean;
  data: unknown;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface LoginRequest {
  username: string;
  password: string;
}
export interface MailConfigurationDto {
  configured: boolean;
  host: string;
  port: number;
  securityMode: string;
  fromAddress: string;
  username: string;
  passwordConfigured: boolean;
  /**
   * 十进制long文本；客户端不得转number
   */
  version: string;
  updatedAt: string | null;
}
export interface MailConfigurationDtoApiResult {
  success: boolean;
  data: MailConfigurationDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface MailConfigurationRequest {
  host: string;
  fromAddress: string;
  password?: string | null;
  username?: string | null;
  port: number;
  securityMode: string;
  /**
   * 十进制long文本；客户端不得转number
   */
  version: string;
}
export interface MailMessageDto {
  id: string;
  requestId: string;
  kind: string;
  fromAddress: string;
  toAddress: string;
  subject: string;
  textBody: string | null;
  status: string;
  errorCode: string | null;
  message: string | null;
  createdByUserId: string | null;
  createdAt: string;
  completedAt: string | null;
  htmlBody: string | null;
}
export interface MailMessageDtoApiResult {
  success: boolean;
  data: MailMessageDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface MailMessageDtoPageData {
  items: MailMessageDto[];
  total: number;
  current: number;
  pageSize: number;
}
export interface MailMessageDtoPageDataApiResult {
  success: boolean;
  data: MailMessageDtoPageData | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface MailMessageQuery {
  keyword?: string | null;
  status?: string | null;
  kind?: string | null;
  current: number;
  pageSize: number;
  sortField?: string | null;
  sortOrder?: string | null;
}
export interface MailProbeDto {
  connected: boolean;
  errorCode: string | null;
  message: string;
}
export interface MailProbeDtoApiResult {
  success: boolean;
  data: MailProbeDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface MailReportConfigurationDto {
  configured: boolean;
  /**
   * 十进制long文本；客户端不得转number
   */
  version: string;
  schedules: MailReportScheduleDto[];
}
export interface MailReportScheduleDto {
  period: string;
  enabled: boolean;
  toAddress: string;
  sendTime: string;
  dayOfWeek: number;
  dayOfMonth: number;
  effectiveFrom: string | null;
  nextSendAt: string | null;
}
export interface MailReportConfigurationDtoApiResult {
  success: boolean;
  data: MailReportConfigurationDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface MailReportConfigurationRequest {
  /**
   * 十进制long文本；客户端不得转number
   */
  version: string;
  schedules: MailReportScheduleRequest[];
}
export interface MailReportScheduleRequest {
  period: string;
  enabled: boolean;
  toAddress: string;
  sendTime: string;
  dayOfWeek: number;
  dayOfMonth: number;
}
export interface MailReportPreviewDto {
  subject: string;
  textBody: string;
  htmlBody: string;
  from: string;
  to: string;
}
export interface MailReportPreviewDtoApiResult {
  success: boolean;
  data: MailReportPreviewDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface MailReportPreviewRequest {
  from: string;
  to: string;
}
export interface MailSendRequest {
  requestId: string;
  toAddress: string;
  subject: string;
  textBody: string;
}
export interface McpCallLogDto {
  id: string;
  callId: string;
  occurredAt: string;
  completedAt: string | null;
  clientId: string | null;
  clientName: string | null;
  userId: string | null;
  method: string;
  tool: string | null;
  serverId: string | null;
  sourceIp: string;
  protocolVersion: string | null;
  result: string;
  errorCode: string | null;
  httpStatusCode: number | null;
  /**
   * 十进制long文本；客户端不得转number
   */
  durationMilliseconds: string | null;
  traceId: string | null;
  requestId: string | null;
  parametersSummary: string | null;
  outputSummary: string | null;
  policySummary: string | null;
  exceptionId: string | null;
}
export interface McpCallLogDtoApiResult {
  success: boolean;
  data: McpCallLogDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface McpCallLogDtoPageData {
  items: McpCallLogDto[];
  total: number;
  current: number;
  pageSize: number;
}
export interface McpCallLogDtoPageDataApiResult {
  success: boolean;
  data: McpCallLogDtoPageData | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface McpClientDto {
  id: string;
  name: string;
  oAuthClientId: string;
  clientType: string;
  userId: string;
  enabled: boolean;
  expiresAt: string | null;
  serverScope: string;
  version: string;
  tools: string[];
  serverIds: string[];
  networks: string[];
  origins: string[];
  redirectUris: string[];
  createdAt: string;
  updatedAt: string;
  lastCalledAt: string | null;
}
export interface McpClientDtoApiResult {
  success: boolean;
  data: McpClientDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface McpClientDtoPageData {
  items: McpClientDto[];
  total: number;
  current: number;
  pageSize: number;
}
export interface McpClientDtoPageDataApiResult {
  success: boolean;
  data: McpClientDtoPageData | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface McpClientQuery {
  current: number;
  pageSize: number;
  keyword?: string | null;
  sortField: string;
  sortOrder: string;
}
export interface McpClientRequest {
  expectedVersion: string;
  name: string;
  clientType: string;
  userId: string;
  expiresAt?: string | null;
  serverScope: string;
  tools: string[];
  serverIds: string[];
  networks: string[];
  origins: string[];
  redirectUris: string[];
}
export interface McpConfigAuditDto {
  id: string;
  occurredAt: string;
  userId: string;
  action: string;
  objectId: string;
  previousVersion: string;
  currentVersion: string;
  summary: string;
}
export interface McpConfigAuditDtoPageData {
  items: McpConfigAuditDto[];
  total: number;
  current: number;
  pageSize: number;
}
export interface McpConfigAuditDtoPageDataApiResult {
  success: boolean;
  data: McpConfigAuditDtoPageData | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface McpConfigurationDto {
  enabled: boolean;
  version: string;
  supported: boolean;
  ready: boolean;
  endpoint: string;
  protocolVersions: string[];
  readinessErrors: string[];
  requestsPerMinute: number;
  clientConcurrency: number;
  globalConcurrency: number;
  queryTimeoutSeconds: number;
  maxRequestBytes: number;
  maxResponseBytes: number;
  enabledTools: string[];
}
export interface McpConfigurationDtoApiResult {
  success: boolean;
  data: McpConfigurationDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface McpConfigurationRequest {
  expectedVersion: string;
  requestsPerMinute: number;
  clientConcurrency: number;
  globalConcurrency: number;
  queryTimeoutSeconds: number;
  maxRequestBytes: number;
  maxResponseBytes: number;
  enabledTools: string[];
}
export interface McpLogQuery {
  current: number;
  pageSize: number;
  from?: string | null;
  to?: string | null;
  clientId?: string | null;
  userId?: string | null;
  method?: string | null;
  tool?: string | null;
  serverId?: string | null;
  sourceIp?: string | null;
  result?: string | null;
  errorCode?: string | null;
  traceId?: string | null;
  callId?: string | null;
  sortField: string;
  sortOrder: string;
}
export interface McpOptionsDto {
  users: McpUserOptionDto[];
  servers: McpServerOptionDto[];
}
export interface McpUserOptionDto {
  id: string;
  displayName: string;
  username: string;
  enabled: boolean;
}
export interface McpServerOptionDto {
  id: string;
  name: string;
  brand: string;
  enabled: boolean;
}
export interface McpOptionsDtoApiResult {
  success: boolean;
  data: McpOptionsDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface McpSecretDto {
  clientId: string;
  secret: string;
}
export interface McpSecretDtoApiResult {
  success: boolean;
  data: McpSecretDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface McpToolDto {
  name: string;
  description: string;
  requiredPermission: string | null;
  enabled: boolean;
  readOnly: boolean;
  inputSchema: unknown;
  outputSchema: unknown;
}
export interface McpToolDtoIReadOnlyListApiResult {
  success: boolean;
  data: McpToolDto[] | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface McpVersionRequest {
  expectedVersion: string;
}
export interface MountVirtualMediaRequest {
  imageId?: string | null;
  slotId: string;
  confirmation: string;
  requestId: string;
  imageUrl?: string | null;
}
export interface PowerCapabilitiesDto {
  enabled: boolean;
  capabilities: DeviceCapabilities;
  powerState: string | null;
}
export interface PowerCapabilitiesDtoApiResult {
  success: boolean;
  data: PowerCapabilitiesDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface PowerOperationDto {
  id: string;
  requestId: string;
  action: PowerAction;
  status: string;
  message: string | null;
  createdAt: string;
  completedAt: string | null;
}
export interface PowerOperationDtoApiResult {
  success: boolean;
  data: PowerOperationDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface PowerOperationDtoIReadOnlyListApiResult {
  success: boolean;
  data: PowerOperationDto[] | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface PowerRequest {
  action: PowerAction;
  confirmation: string;
  requestId: string;
}
export interface PowerReviewRequest {
  confirmation: string;
  outcome: string;
  note: string;
}
export interface RecentTelemetryDto {
  series: HistorySeriesDto[];
  gaps: CollectionGapDto[];
}
export interface RecentTelemetryDtoApiResult {
  success: boolean;
  data: RecentTelemetryDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface ResetUserPasswordRequest {
  /**
   * 十进制long文本；客户端不得转number
   */
  version: string;
  newPassword: string;
  confirmPassword: string;
}
export interface ReviewVirtualMediaRequest {
  confirmation: string;
  note: string;
}
export interface SaveServerRequest {
  name: string;
  brand: string;
  controllerType: string;
  address: string;
  username: string;
  password?: string | null;
  samplingIntervalSeconds: number;
  enabled: boolean;
  certificateFingerprint?: string | null;
  ignoreCertificateErrors: boolean;
  existingServerId?: string | null;
}
export interface SaveUserRequest {
  username: string;
  displayName: string;
  enabled: boolean;
  canManageUsers: boolean;
  canUseConsole: boolean;
  canManageServers: boolean;
  canOperatePower: boolean;
  password?: string | null;
  /**
   * 十进制long文本；客户端不得转number
   */
  version?: string | null;
}
export interface ServerConfigurationFileApiResult {
  success: boolean;
  data: ServerConfigurationFile | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface ServerConfigurationImportResult {
  added: number;
  updated: number;
  skipped: number;
}
export interface ServerConfigurationImportResultApiResult {
  success: boolean;
  data: ServerConfigurationImportResult | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface ServerDetailDto {
  server: ServerDto;
  inventory: DeviceInventory | null;
  telemetry: TelemetryDto;
  capabilities: DeviceCapabilities;
  powerOperations: PowerOperationDto[];
}
export interface ServerDetailDtoApiResult {
  success: boolean;
  data: ServerDetailDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface ServerDtoApiResult {
  success: boolean;
  data: ServerDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface ServerDtoPageData {
  items: ServerDto[];
  total: number;
  current: number;
  pageSize: number;
}
export interface ServerDtoPageDataApiResult {
  success: boolean;
  data: ServerDtoPageData | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface SetUidRequest {
  state: UidControlState;
  confirmation: string;
}
export interface TelemetryDtoApiResult {
  success: boolean;
  data: TelemetryDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface UidStatusDto {
  state: UidState;
  canControl: boolean;
  supportedStates: UidControlState[];
  observedAt: string;
  message: string | null;
}
export interface UidStatusDtoApiResult {
  success: boolean;
  data: UidStatusDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface UserDto {
  id: string;
  username: string;
  displayName: string;
  enabled: boolean;
  deletedAt: string | null;
  isAdministrator: boolean;
  canManageUsers: boolean;
  canUseConsole: boolean;
  /**
   * 十进制long文本；客户端不得转number
   */
  version: string;
  createdAt: string;
  updatedAt: string;
  canManageServers: boolean;
  canOperatePower: boolean;
}
export interface UserDtoApiResult {
  success: boolean;
  data: UserDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface UserDtoPageData {
  items: UserDto[];
  total: number;
  current: number;
  pageSize: number;
}
export interface UserDtoPageDataApiResult {
  success: boolean;
  data: UserDtoPageData | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface UserVersionRequest {
  /**
   * 十进制long文本；客户端不得转number
   */
  version: string;
}
export interface VirtualMediaImageDto {
  id: string;
  fileName: string;
  mediaType: string;
  /**
   * 十进制long文本；客户端不得转number
   */
  sizeBytes: string;
  sha256: string;
  createdAt: string;
}
export interface VirtualMediaImageDtoApiResult {
  success: boolean;
  data: VirtualMediaImageDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface VirtualMediaImageDtoPageData {
  items: VirtualMediaImageDto[];
  total: number;
  current: number;
  pageSize: number;
}
export interface VirtualMediaImageDtoPageDataApiResult {
  success: boolean;
  data: VirtualMediaImageDtoPageData | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface VirtualMediaOperationDto {
  id: string;
  serverId: string;
  slotId: string;
  imageId: string | null;
  action: string;
  status: string;
  requestId: string;
  message: string | null;
  createdAt: string;
  updatedAt: string;
  canReviewNotExecuted: boolean;
}
export interface VirtualMediaOperationDtoApiResult {
  success: boolean;
  data: VirtualMediaOperationDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface VirtualMediaSlotDto {
  id: string;
  name: string;
  mediaType: string;
  inserted: boolean;
  writeProtected: boolean;
  imageName: string | null;
  mountedImageId: string | null;
  canMount: boolean;
  canEject: boolean;
}
export interface VirtualMediaStatusDto {
  serverId: string;
  supported: boolean;
  canMount: boolean;
  mediaBaseUrlConfigured: boolean;
  slots: VirtualMediaSlotDto[];
  operations: VirtualMediaOperationDto[];
  warnings: string[];
}
export interface VirtualMediaStatusDtoApiResult {
  success: boolean;
  data: VirtualMediaStatusDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
export interface VirtualMediaTrafficDto {
  /**
   * 十进制long文本；客户端不得转number
   */
  bytesRead: string;
  bytesPerSecond: number;
  observedAt: string;
}
export interface VirtualMediaTrafficDtoApiResult {
  success: boolean;
  data: VirtualMediaTrafficDto | null;
  errorCode: string | null;
  errorMessage: string | null;
}
