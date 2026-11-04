import { createStyles } from 'antd-style';

export default createStyles(({ token }) => ({
  header: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: 8,
    marginTop: 12,
    marginBottom: 4,
    fontSize: 11,
    color: token.colorTextSecondary,
  },
  chart: { width: '100%', minWidth: 0, maxWidth: '100%' },
  empty: { paddingBlock: 8, color: token.colorTextSecondary, fontSize: 12 },
  // 图例不进入固定高度画布，完整文本逐行自然换行，隐藏曲线后仍能聚焦恢复。
  legend: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr)',
    gap: 6,
    listStyle: 'none',
    margin: '8px 0 0',
    padding: 0,
    minWidth: 0,
  },
  item: { minWidth: 0 },
  button: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: 8,
    width: '100%',
    minWidth: 0,
    background: 'transparent',
    border: 0,
    padding: 0,
    textAlign: 'left',
    color: token.colorTextSecondary,
    fontSize: 11,
    lineHeight: 1.5,
    cursor: 'pointer',
    '&:focus-visible': {
      outline: `2px solid ${token.colorPrimary}`,
      outlineOffset: 2,
      borderRadius: 2,
    },
  },
  marker: { flex: '0 0 20px', height: 0, borderTopWidth: 2, marginTop: 7 },
  label: { minWidth: 0, whiteSpace: 'normal', overflowWrap: 'anywhere' },
  hidden: { opacity: 0.45 },
}));
