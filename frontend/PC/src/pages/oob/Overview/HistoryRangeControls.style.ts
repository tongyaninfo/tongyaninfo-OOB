import { createStyles } from 'antd-style';

export default createStyles(({ token }) => ({
  pickerPopup: {
    // 窄屏按上下排列日期与时间，避免默认并排面板超出视口而无法选择左侧日期。
    '@media (max-width: 575px)': {
      '.ant-picker-panel-container': {
        maxWidth: 'calc(100vw - 16px)',
        maxHeight: 'calc(100dvh - 24px)',
        overflow: 'auto',
      },
      '.ant-picker-datetime-panel, .ant-picker-panels': {
        flexDirection: 'column',
      },
      '.ant-picker-datetime-panel .ant-picker-time-panel': {
        borderInlineStart: 0,
        borderBlockStart: `1px solid ${token.colorBorderSecondary}`,
      },
      '.ant-picker-time-panel .ant-picker-content': { height: 160 },
    },
  },
}));
