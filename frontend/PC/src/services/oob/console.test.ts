import { beforeEach, describe, expect, it, vi } from 'vitest';

const getServers = vi.hoisted(() => vi.fn());
vi.mock('./api', () => ({ getServers }));

import { getConsoleServers } from './console';

beforeEach(() => {
  getServers.mockReset();
});
describe('控制台完整服务器目录', () => {
  it('读完全部分页，长ID始终为字符串', async () => {
    getServers.mockImplementation(async ({ current }) => ({
      success: true,
      data: {
        current,
        total: 2,
        items: [
          { id: current === 1 ? '9007199254740993' : '9007199254740995' },
        ],
      },
    }));
    expect((await getConsoleServers()).map((item) => item.id)).toEqual([
      '9007199254740993',
      '9007199254740995',
    ]);
    expect(getServers).toHaveBeenCalledTimes(2);
  });
  it('中途失败和重复页均拒绝返回不完整目录', async () => {
    getServers.mockImplementation(async ({ current }) =>
      current === 1
        ? { success: true, data: { current, total: 2, items: [{ id: '1' }] } }
        : { success: false, errorMessage: 'second page failed' },
    );
    await expect(getConsoleServers()).rejects.toThrow('second page failed');
    getServers.mockImplementation(async ({ current }) => ({
      success: true,
      data: { current, total: 2, items: [{ id: '1' }] },
    }));
    await expect(getConsoleServers()).rejects.toThrow(
      'SERVER_DIRECTORY_DUPLICATED',
    );
  });
});
