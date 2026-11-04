import type { useOobIntl } from '../intl';

type Translate = ReturnType<typeof useOobIntl>;
export const mailStatuses = [
  'Sending',
  'Accepted',
  'Failed',
  'Unknown',
] as const;
export const mailKinds = ['Test', 'Manual', 'System'] as const;
export const mailStatusLabel = (status: string, t: Translate) =>
  mailStatuses.includes(status as (typeof mailStatuses)[number])
    ? t(`oob.mail.status.${status as (typeof mailStatuses)[number]}`)
    : status;
export const mailKindLabel = (kind: string, t: Translate) =>
  mailKinds.includes(kind as (typeof mailKinds)[number])
    ? t(`oob.mail.source.${kind as (typeof mailKinds)[number]}`)
    : kind;
const statusColors: Record<string, string> = {
  Accepted: 'success',
  Failed: 'error',
  Sending: 'processing',
  Unknown: 'warning',
};
export const mailStatusColor = (status: string) => statusColors[status];
