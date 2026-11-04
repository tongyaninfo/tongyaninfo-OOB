import { PageContainer } from '@ant-design/pro-components';
import XMarkdown from '@ant-design/x-markdown';
import '@ant-design/x-markdown/es/XMarkdown/index.css';
import enUS from '@root/docs/user-guide.en-US.md';
import zhCN from '@root/docs/user-guide.zh-CN.md';
import zhTW from '@root/docs/user-guide.zh-TW.md';
import { useIntl, useModel } from '@umijs/max';
import { Card } from 'antd';
import React from 'react';

import './Welcome.css';
import './Welcome-dark.css';

const guides = {
  'zh-CN': {
    title: '通研带外系统管理使用文档',
    subtitle: '服务器接入、运行监控、虚拟控制台与日常管理操作指南',
    content: zhCN,
  },
  'zh-TW': {
    title: '通研帶外系統管理使用文件',
    subtitle: '伺服器接入、執行監控、虛擬控制台與日常管理操作指南',
    content: zhTW,
  },
  'en-US': {
    title: 'Tongyan Out-of-Band Management User Guide',
    subtitle:
      'Connect servers, monitor operation, and use consoles and administration tools',
    content: enUS,
  },
};

interface HeadingProps extends React.HTMLAttributes<HTMLHeadingElement> {
  tag?: 'h1' | 'h2' | 'h3' | 'h4';
  domNode?: unknown;
  classname?: string;
  class?: string;
}

const Heading: React.FC<HeadingProps> = ({
  tag: Tag = 'h1',
  children,
  className,
  classname,
  class: htmlClass,
}) => {
  // 兼容 Markdown 渲染器传入的类名，不把内部节点属性透传到 DOM。
  const allClasses = [className, classname, htmlClass]
    .filter(Boolean)
    .join(' ');
  // 正文目录与标题共用锚点规则，主题切换不会改变定位。
  const textContent = React.Children.toArray(children)
    .map((child) => (typeof child === 'string' ? child : ''))
    .join('');
  const id = textContent
    .replace(/[^\w\s一-鿿-]/g, '')
    .trim()
    .replace(/\s+/g, '-')
    .toLowerCase();
  return (
    <Tag id={id} className={`heading-anchor ${allClasses}`.trim()}>
      <a href={`#${id}`} className="anchor-link">
        #
      </a>
      {children}
    </Tag>
  );
};

const mdComponents = {
  h1: (props: HeadingProps) => <Heading tag="h1" {...props} />,
  h2: (props: HeadingProps) => <Heading tag="h2" {...props} />,
  h3: (props: HeadingProps) => <Heading tag="h3" {...props} />,
  h4: (props: HeadingProps) => <Heading tag="h4" {...props} />,
};

const Welcome: React.FC = () => {
  const { locale } = useIntl();
  const normalizedLocale = locale.toLowerCase();
  const guide =
    guides[
      normalizedLocale === 'zh-tw'
        ? 'zh-TW'
        : normalizedLocale.startsWith('zh')
          ? 'zh-CN'
          : 'en-US'
    ];
  const { initialState } = useModel('@@initialState');
  // 使用全局已解析的主题，让自动模式随系统变化时文档同步切换。
  const isDark = initialState?.settings?.navTheme === 'realDark';

  return (
    <PageContainer title={guide.title} content={guide.subtitle}>
      <div data-theme={isDark ? 'dark' : 'light'} className="user-guide">
        <article className="welcome-markdown" aria-label={guide.title}>
          <Card>
            <XMarkdown components={mdComponents}>{guide.content}</XMarkdown>
          </Card>
        </article>
      </div>
    </PageContainer>
  );
};

export default Welcome;
