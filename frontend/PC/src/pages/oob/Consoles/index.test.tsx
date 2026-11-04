import { render } from '@testing-library/react';
import React from 'react';
import { expect, it } from 'vitest';
import Consoles from './index';

it('路由入口不再持有会话组件，路由卸载不会重复创建或销毁宿主中的 iframe', () => {
  const view = render(<Consoles />);
  expect(view.container).toBeEmptyDOMElement();
});
