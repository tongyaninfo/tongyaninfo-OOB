import type {
  MetricReading,
  ServerTelemetryDto,
} from '../src/services/oob/models';

/** 合同完整的虚拟快照，夹具只使用占位目标，不包含真实设备认证信息。 */
export function telemetryFixture(
  time: string,
  metrics: MetricReading[],
  status = 'Online',
): ServerTelemetryDto {
  return {
    server: {
      id: '9007199254740993',
      name: 'Server',
      brand: 'HP',
      controllerType: 'iLO4',
      address: 'https://example.invalid/',
      username: 'placeholder',
      samplingIntervalSeconds: 30,
      enabled: true,
      certificateFingerprint: null,
      ignoreCertificateErrors: true,
      identity: {
        manufacturer: null,
        model: null,
        serialNumber: null,
        hostName: null,
        controllerHostName: null,
        controllerType: null,
        firmwareVersion: null,
        powerState: null,
      },
      connectionStatus: status,
      lastCollectedAt: time,
      lastInventoryAt: null,
      lastError: null,
      createdAt: time,
      updatedAt: time,
    },
    telemetry: { collectedAt: time, stale: false, metrics, warnings: [] },
    publishedAt: time,
  };
}
