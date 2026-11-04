import { createStyles } from 'antd-style';

export default createStyles(({ token }) => ({
  card: {
    overflow: 'hidden',
    borderRadius: 12,
    '.ant-card-head': { minHeight: 64 },
    '.ant-card-head-title': { whiteSpace: 'normal', paddingBlock: 12 },
  },
  heading: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 8 },
  serverName: { fontSize: 18, fontWeight: 600, color: token.colorTextHeading },
  metadata: {
    marginTop: 4,
    fontSize: 12,
    fontWeight: 400,
    color: token.colorTextSecondary,
  },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.3fr) minmax(0, 1fr) minmax(0, 1fr)',
    gap: 12,
    alignItems: 'start',
    '@media (max-width: 1199px)': {
      gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    },
    '@media (max-width: 767px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  temperatures: {
    '@media (min-width: 768px) and (max-width: 1199px)': {
      gridColumn: '1 / -1',
    },
  },
  panel: {
    minWidth: 0,
    padding: 12,
    border: `1px solid ${token.colorBorderSecondary}`,
    borderRadius: token.borderRadiusLG,
    background: token.colorFillAlter,
  },
  panelTitle: {
    display: 'flex',
    gap: 8,
    alignItems: 'center',
    marginBottom: 12,
    fontWeight: 600,
  },
  panelActions: {
    marginLeft: 'auto',
    display: 'inline-flex',
    alignItems: 'center',
    gap: 4,
  },
  icon: { color: token.colorPrimary, fontSize: 16 },
  temperatureReadings: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(130px, 1fr))',
    gap: 8,
  },
  averageReadings: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(100px, 1fr))',
    gap: 8,
    dd: { fontSize: 22 },
    'dd > span': { display: 'block', marginLeft: 0, lineHeight: 1.2 },
  },
  reading: { minWidth: 0, margin: '0 0 8px' },
  readingName: {
    fontSize: 12,
    color: token.colorTextSecondary,
    overflowWrap: 'anywhere',
  },
  readingValue: {
    margin: 0,
    whiteSpace: 'nowrap',
    fontVariantNumeric: 'tabular-nums',
    fontSize: 24,
    lineHeight: 1.4,
    fontWeight: 600,
    color: token.colorTextHeading,
  },
  unit: {
    marginLeft: 3,
    fontWeight: 400,
    fontSize: 12,
    color: token.colorTextSecondary,
  },
  notice: {
    display: 'block',
    fontSize: 12,
    color: token.colorTextSecondary,
    marginTop: 6,
    lineHeight: 1.5,
    overflowWrap: 'anywhere',
  },
}));
