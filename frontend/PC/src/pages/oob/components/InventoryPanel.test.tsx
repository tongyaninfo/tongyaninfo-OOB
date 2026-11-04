import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { Descriptions } from 'antd';
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { DeviceInventory } from '@/services/oob/models';
import { telemetryFixture } from '../../../../tests/oobFixtures';

vi.mock('@umijs/max', () => ({ useIntl: () => ({ locale: 'zh-CN' }) }));
vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));

import InventoryPanel from './InventoryPanel';

function viewport(width: number) {
  vi.spyOn(window, 'matchMedia').mockImplementation((media) => ({
    matches: /min-width:\s*(\d+)px/.test(media)
      ? width >= Number(media.match(/min-width:\s*(\d+)px/)?.[1])
      : /max-width:\s*(\d+)px/.test(media)
        ? width <= Number(media.match(/max-width:\s*(\d+)px/)?.[1])
        : false,
    media,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(),
  }));
}
const snapshot = telemetryFixture('2026-10-25T00:00:00Z', []);
const inventory: DeviceInventory = {
  identity: snapshot.server.identity,
  collectedAt: '2026-10-25T00:00:00Z',
  warnings: [],
  components: [
    {
      key: 'processor:1',
      name: 'fixture processor',
      kind: 'Processor',
      model: 'fixture model',
      manufacturer: 'fixture manufacturer',
      serialNumber: 'fixture serial',
      status: 'OK',
      capacityBytes: null,
      children: [],
      attributes: {
        Manufacturer: 'fixture manufacturer',
        SerialNumber: 'fixture serial',
        Protocol: 'fixture protocol',
        'Future.Field': 'future value',
      },
    },
  ],
};
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});
describe('硬件详情真实Descriptions响应式布局', () => {
  it('旧固定span2在xs单列布局确实会产生列数警告', async () => {
    viewport(390);
    const error = vi
      .spyOn(console, 'error')
      .mockImplementation(() => undefined);
    render(
      <Descriptions
        column={{ xs: 1, sm: 2 }}
        items={[
          { key: 'old', label: 'old attribute', children: 'value', span: 2 },
        ]}
      />,
    );
    await waitFor(() =>
      expect(
        error.mock.calls.some((args) =>
          args.join(' ').includes('Sum of column `span`'),
        ),
      ).toBe(true),
    );
  });
  it.each([
    [390, 10],
    [800, 7],
    [1280, 7],
  ])('%ipx下实际库存渲染为%i行，无列数警告且保留全部属性', async (width, rows) => {
    viewport(width);
    const error = vi
      .spyOn(console, 'error')
      .mockImplementation(() => undefined);
    const view = render(<InventoryPanel inventory={inventory} />);
    await waitFor(() =>
      expect(
        view.container.querySelectorAll('.ant-descriptions-row'),
      ).toHaveLength(rows),
    );
    expect(screen.getByTitle('Future.Field')).toHaveTextContent('Future.Field');
    expect(screen.getByText('future value')).toBeInTheDocument();
    expect(
      error.mock.calls.some((args) =>
        args.join(' ').includes('Sum of column `span`'),
      ),
    ).toBe(false);
  });
});
