import { createStyles } from 'antd-style';

export default createStyles(({ token }) => ({
  toolbar: {
    '.ant-pro-table-list-toolbar-container': {
      minWidth: 0,
      flexWrap: 'wrap',
      gap: token.marginXS,
    },
    '.ant-pro-table-list-toolbar-left, .ant-pro-table-list-toolbar-right': {
      minWidth: 0,
      maxWidth: '100%',
    },
    // 当前 ProTable 将全部动作放入无类名的首个子容器，默认不换行。
    // 只调整本页动作容器，让窄屏上的搜索与操作按钮逐行排列。
    '.ant-pro-table-list-toolbar-right > div:first-child': {
      minWidth: 0,
      maxWidth: '100%',
      flexWrap: 'wrap',
      justifyContent: 'flex-end',
      '> button': { flexShrink: 0 },
    },
  },
  search: { width: 260, maxWidth: '100%', minWidth: 0 },
}));
