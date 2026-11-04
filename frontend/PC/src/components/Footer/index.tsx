import { createStyles } from 'antd-style';
import React from 'react';

const useStyles = createStyles(({ token }) => ({
  footer: {
    padding: '20px 24px',
    textAlign: 'center',
    color: token.colorTextDescription,
    fontSize: 12,
    lineHeight: 1.6,
    letterSpacing: 2,
  },
}));

/** 登录与业务布局共用品牌署名，避免展示模板仓库或框架版本信息。 */
const Footer: React.FC = () => {
  const { styles } = useStyles();
  return <footer className={styles.footer}>通研技术</footer>;
};

export default Footer;
