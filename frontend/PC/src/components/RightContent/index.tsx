import { BookOutlined } from '@ant-design/icons';
import { history, useIntl } from '@umijs/max';
import { Button, Tooltip } from 'antd';
import React from 'react';
import { LangDropdown } from './LangDropdown';
import useHeaderActionStyles from './style';
import { VersionDropdown } from './VersionDropdown';

export const DocLink: React.FC = () => {
  const { styles } = useHeaderActionStyles();
  const intl = useIntl();
  const label = intl.formatMessage({ id: 'component.globalHeader.help' });
  return (
    <Tooltip title={label}>
      <Button
        type="text"
        className={styles.action}
        icon={<BookOutlined />}
        aria-label={label}
        onClick={() => {
          history.push('/welcome');
        }}
      />
    </Tooltip>
  );
};

export { LangDropdown, VersionDropdown };
