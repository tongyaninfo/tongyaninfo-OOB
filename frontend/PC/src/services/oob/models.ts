import type { components } from './generated';

/** 所有业务类型只引用后端 OpenAPI 生成合同，不维护第二份 DTO。 */
export type PowerAction = components['schemas']['PowerAction'];
export type PowerCapabilitiesDto =
  components['schemas']['PowerCapabilitiesDto'];
export type DeviceIdentity = components['schemas']['DeviceIdentity'];
export type DeviceCapabilities = components['schemas']['DeviceCapabilities'];
export type ServerDto = components['schemas']['ServerDto'];
export type SaveServerRequest = components['schemas']['SaveServerRequest'];
export type ExportServerConfigurationRequest =
  components['schemas']['ExportServerConfigurationRequest'];
export type ImportServerConfigurationRequest =
  components['schemas']['ImportServerConfigurationRequest'];
export type ServerConfigurationFile =
  components['schemas']['ServerConfigurationFile'];
export type ServerConfigurationImportResult =
  components['schemas']['ServerConfigurationImportResult'];
export type ConnectionTestResult =
  components['schemas']['ConnectionTestResult'];
export type HardwareComponent = components['schemas']['HardwareComponent'];
export type DeviceInventory = components['schemas']['DeviceInventory'];
export type MetricReading = components['schemas']['MetricReading'];
export type TelemetryDto = components['schemas']['TelemetryDto'];
export type UidStatusDto = components['schemas']['UidStatusDto'];
export type SetUidRequest = components['schemas']['SetUidRequest'];
export type PowerOperationDto = components['schemas']['PowerOperationDto'];
export type ServerDetailDto = components['schemas']['ServerDetailDto'];
export type DashboardDto = components['schemas']['DashboardDto'];
export type ServerTelemetryDto = components['schemas']['ServerTelemetryDto'];
export type HistoryPointDto = components['schemas']['HistoryPointDto'];
export type HistorySeriesDto = components['schemas']['HistorySeriesDto'];
export type CurrentUserDto = components['schemas']['CurrentUserDto'];
export type UserDto = components['schemas']['UserDto'];
export type UserPageData = components['schemas']['UserDtoPageData'];
export type SaveUserRequest = components['schemas']['SaveUserRequest'];
export type ChangePasswordRequest =
  components['schemas']['ChangePasswordRequest'];
export type ResetPasswordRequest =
  components['schemas']['ResetUserPasswordRequest'];
export type CsrfDto = components['schemas']['CsrfDto'];
export type PowerRequest = components['schemas']['PowerRequest'];
export type ServerPageData = components['schemas']['ServerDtoPageData'];
export type PowerReviewRequest = components['schemas']['PowerReviewRequest'];

export type RecentTelemetryDto = components['schemas']['RecentTelemetryDto'];
export type CollectionGapDto = components['schemas']['CollectionGapDto'];
export type ExceptionLogDto = components['schemas']['ExceptionLogDto'];
export type ExceptionPageData =
  components['schemas']['ExceptionLogDtoPageData'];
export type ClientExceptionRequest =
  components['schemas']['ClientExceptionRequest'];
export type ConsoleCapabilitiesDto =
  components['schemas']['ConsoleCapabilitiesDto'];
export type ConsoleSessionDto = components['schemas']['ConsoleSessionDto'];
export type ConsoleSessionRequest =
  components['schemas']['ConsoleSessionRequest'];
export type ConsoleInputStateRequest =
  components['schemas']['ConsoleInputStateRequest'];
export type VirtualMediaImageDto =
  components['schemas']['VirtualMediaImageDto'];
export type VirtualMediaImagePageData =
  components['schemas']['VirtualMediaImageDtoPageData'];
export type VirtualMediaOperationDto =
  components['schemas']['VirtualMediaOperationDto'];
export type VirtualMediaStatusDto =
  components['schemas']['VirtualMediaStatusDto'];
export type VirtualMediaTrafficDto =
  components['schemas']['VirtualMediaTrafficDto'];
export type MountVirtualMediaRequest =
  components['schemas']['MountVirtualMediaRequest'];
export type EjectVirtualMediaRequest =
  components['schemas']['EjectVirtualMediaRequest'];
export type ReviewVirtualMediaRequest =
  components['schemas']['ReviewVirtualMediaRequest'];

export type MailConfigurationDto =
  components['schemas']['MailConfigurationDto'];
export type MailConfigurationRequest =
  components['schemas']['MailConfigurationRequest'];
export type MailProbeDto = components['schemas']['MailProbeDto'];
export type MailSendRequest = components['schemas']['MailSendRequest'];
export type MailMessageDto = components['schemas']['MailMessageDto'];
export type MailMessagePageData =
  components['schemas']['MailMessageDtoPageData'];
export type MailReportConfigurationDto =
  components['schemas']['MailReportConfigurationDto'];
export type MailReportConfigurationRequest =
  components['schemas']['MailReportConfigurationRequest'];
export type MailReportScheduleDto =
  components['schemas']['MailReportScheduleDto'];
export type MailReportScheduleRequest =
  components['schemas']['MailReportScheduleRequest'];
export type MailReportPreviewRequest =
  components['schemas']['MailReportPreviewRequest'];
export type MailReportPreviewDto =
  components['schemas']['MailReportPreviewDto'];

export type McpConfigurationDto = components['schemas']['McpConfigurationDto'];
export type McpConfigurationRequest =
  components['schemas']['McpConfigurationRequest'];
export type McpVersionRequest = components['schemas']['McpVersionRequest'];
export type McpClientDto = components['schemas']['McpClientDto'];
export type McpClientRequest = components['schemas']['McpClientRequest'];
export type McpClientQuery = components['schemas']['McpClientQuery'];
export type McpClientPageData = components['schemas']['McpClientDtoPageData'];
export type McpLogQuery = components['schemas']['McpLogQuery'];
export type McpCallLogDto = components['schemas']['McpCallLogDto'];
export type McpCallLogPageData = components['schemas']['McpCallLogDtoPageData'];
export type McpToolDto = components['schemas']['McpToolDto'];
export type McpSecretDto = components['schemas']['McpSecretDto'];
export type McpOptionsDto = components['schemas']['McpOptionsDto'];
