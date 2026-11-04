import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ServerDto } from '@/services/oob/models';

const api = vi.hoisted(() => ({
  images: vi.fn(),
  status: vi.fn(),
  traffic: vi.fn(),
  mount: vi.fn(),
  eject: vi.fn(),
  upload: vi.fn(),
  find: vi.fn(),
  review: vi.fn(),
}));
vi.mock('@/services/oob/console', () => ({
  getMediaImages: api.images,
  getVirtualMedia: api.status,
  getVirtualMediaTraffic: api.traffic,
  mountVirtualMedia: api.mount,
  ejectVirtualMedia: api.eject,
  uploadMediaImage: api.upload,
  findVirtualMediaOperation: api.find,
  reviewVirtualMediaNotExecuted: api.review,
}));
vi.mock('../intl', () => ({ useOobIntl: () => (key: string) => key }));

import VirtualMediaDrawer from './VirtualMediaDrawer';

const server = { id: '9007199254740993', name: '测试服务器' } as ServerDto;
beforeEach(() => {
  for (const fn of Object.values(api)) fn.mockReset();
  api.images.mockResolvedValue({
    success: true,
    data: { current: 1, total: 0, items: [] },
  });
  api.status.mockResolvedValue({
    success: true,
    data: {
      serverId: server.id,
      supported: true,
      canMount: true,
      mediaBaseUrlConfigured: false,
      slots: [
        {
          id: 'CD',
          name: 'Virtual CD',
          mediaType: 'CD',
          inserted: false,
          writeProtected: true,
          imageName: null,
          mountedImageId: null,
          canMount: true,
          canEject: false,
        },
      ],
      operations: [],
      warnings: [],
    },
  });
  api.traffic.mockResolvedValue({
    success: true,
    data: {
      bytesRead: '0',
      bytesPerSecond: 0,
      observedAt: '2026-10-25T08:00:00Z',
    },
  });
});

describe('虚拟介质的本机映射与显式动作', () => {
  it('本机ISO默认首选，确认名称和选文件后只交给原生桥接，不上传或调用URL挂载', async () => {
    const local = vi.fn((_file: File) => true);
    const eject = vi.fn();
    const view = render(
      <App>
        <VirtualMediaDrawer
          server={server}
          open
          onClose={() => {}}
          getContainer={() => document.body}
          localSupported
          localReady
          onLocalFile={local}
          onLocalEject={eject}
        />
      </App>,
    );
    const choose = await screen.findByRole('button', {
      name: 'oob.console.localSelect',
    });
    expect(choose).toBeDisabled();
    expect(api.mount).not.toHaveBeenCalled();
    fireEvent.change(
      screen.getByRole('textbox', { name: 'oob.console.confirmServer' }),
      { target: { value: server.name } },
    );
    expect(choose).not.toBeDisabled();
    const file = new File([new Uint8Array(4096)], 'boot.iso', {
      type: 'application/octet-stream',
    });
    const input = document.querySelector<HTMLInputElement>('input[type=file]');
    if (!input) throw new Error('File picker missing');
    fireEvent.change(input, { target: { files: [file] } });
    await waitFor(() => expect(local).toHaveBeenCalledTimes(1));
    expect(local.mock.calls[0][0]).toHaveProperty('name', 'boot.iso');
    expect(api.upload).not.toHaveBeenCalled();
    expect(api.mount).not.toHaveBeenCalled();
    view.unmount();
    expect(api.eject).not.toHaveBeenCalled();
    expect(eject).not.toHaveBeenCalled();
  });
  it('原生桥接未就绪时禁止选择文件，关闭抽屉不会暗中卸载', async () => {
    const local = vi.fn((_file: File) => true);
    const view = render(
      <App>
        <VirtualMediaDrawer
          server={server}
          open
          onClose={() => {}}
          getContainer={() => document.body}
          localSupported={false}
          localReady={false}
          onLocalFile={local}
          onLocalEject={() => {}}
        />
      </App>,
    );
    expect(
      await screen.findByRole('button', { name: 'oob.console.localSelect' }),
    ).toBeDisabled();
    expect(
      screen.getByText('oob.console.localUnavailable'),
    ).toBeInTheDocument();
    view.unmount();
    expect(api.mount).not.toHaveBeenCalled();
    expect(api.eject).not.toHaveBeenCalled();
    expect(local).not.toHaveBeenCalled();
  });
  it('待核对状态从服务端恢复，只有名称与依据齐全才可记录未执行，不补发设备命令', async () => {
    let reviewed = false;
    const original = {
      id: '9007199254740999',
      serverId: server.id,
      slotId: 'CD',
      imageId: null,
      action: 'Mount',
      status: 'Unknown',
      requestId: 'original-media-request',
      message: 'Result unknown',
      createdAt: '2026-10-25T08:00:00Z',
      updatedAt: '2026-10-25T08:00:00Z',
      canReviewNotExecuted: true,
    };
    api.status.mockImplementation(async () => ({
      success: true,
      data: {
        serverId: server.id,
        supported: true,
        canMount: true,
        mediaBaseUrlConfigured: false,
        slots: [],
        operations: [
          {
            ...original,
            status: reviewed ? 'NotExecuted' : 'Unknown',
            canReviewNotExecuted: !reviewed,
          },
        ],
        warnings: [],
      },
    }));
    api.review.mockImplementation(async () => {
      reviewed = true;
      return {
        success: true,
        data: {
          ...original,
          status: 'NotExecuted',
          canReviewNotExecuted: false,
        },
      };
    });
    const view = render(
      <App>
        <VirtualMediaDrawer
          server={server}
          open
          onClose={() => {}}
          getContainer={() => document.body}
          localSupported
          localReady
          onLocalFile={() => true}
          onLocalEject={() => {}}
        />
      </App>,
    );
    fireEvent.click(await screen.findByText('URL'));
    fireEvent.click(await screen.findByText('oob.console.reviewTitle'));
    const submit = await screen.findByRole('button', {
      name: 'oob.console.reviewSubmit',
    });
    expect(submit).toBeDisabled();
    fireEvent.change(
      screen.getByRole('textbox', { name: 'oob.console.reviewName' }),
      { target: { value: server.name } },
    );
    expect(submit).toBeDisabled();
    fireEvent.change(
      screen.getByRole('textbox', { name: 'oob.console.reviewNote' }),
      { target: { value: '  已现场确认设备未插入介质  ' } },
    );
    fireEvent.click(submit);
    await waitFor(() =>
      expect(api.review).toHaveBeenCalledWith(server.id, original.requestId, {
        confirmation: server.name,
        note: '已现场确认设备未插入介质',
      }),
    );
    await screen.findByText('oob.console.operation.NotExecuted');
    expect(api.mount).not.toHaveBeenCalled();
    expect(api.eject).not.toHaveBeenCalled();
    view.unmount();
  });
  it('可选镜像库上传展示实际回调进度和速率，不冒充本机直接映射', async () => {
    let finish!: (value: unknown) => void;
    api.upload.mockImplementation(async (_file, _signal, progress) => {
      progress({
        loaded: 4096,
        total: 8192,
        percent: 50,
        bytesPerSecond: 1024,
      });
      return new Promise((resolve) => {
        finish = resolve;
      });
    });
    const local = vi.fn((_file: File) => true);
    const view = render(
      <App>
        <VirtualMediaDrawer
          server={server}
          open
          onClose={() => {}}
          getContainer={() => document.body}
          localSupported
          localReady
          onLocalFile={local}
          onLocalEject={() => {}}
        />
      </App>,
    );
    fireEvent.click(await screen.findByText('oob.console.mediaLibrary'));
    const file = new File([new Uint8Array(8192)], 'library.iso');
    const picker = document.querySelector<HTMLInputElement>('input[type=file]');
    if (!picker) throw new Error('Library file picker missing');
    fireEvent.change(picker, { target: { files: [file] } });
    expect(await screen.findByRole('progressbar')).toHaveAttribute(
      'aria-valuenow',
      '50',
    );
    expect(
      screen.getByText(/4\.0 KiB \/ 8\.0 KiB · 1\.0 KiB\/s/),
    ).toBeInTheDocument();
    expect(local).not.toHaveBeenCalled();
    finish({
      success: true,
      data: {
        id: '9007199254740999',
        fileName: file.name,
        sizeBytes: '8192',
        mediaType: 'CD',
        sha256: 'fixture',
        createdAt: '2026-10-25T08:00:00Z',
      },
    });
    await waitFor(() =>
      expect(screen.queryByRole('progressbar')).not.toBeInTheDocument(),
    );
    view.unmount();
  });
});
