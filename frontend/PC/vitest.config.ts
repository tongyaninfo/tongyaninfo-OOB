import { join } from 'node:path';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  resolve: {
    alias: {
      '@': join(__dirname, 'src'),
      '@root': join(__dirname),
      '@@': join(__dirname, 'src', '.umi'),
      // 真实组件测试走同一 ESM 入口，避免其 CJS 入口 require 扩展名省略的 antd/es 资源。
      '@ant-design/pro-components': join(
        __dirname,
        'node_modules/@ant-design/pro-components/es/index.js',
      ),
    },
  },
  test: {
    // 真实 antd/ProTable 测试加载成本较高，限制并发以避免资源争用拖延表单异步验证。
    maxWorkers: 4,
    // 真实 ProTable 回归需由 Vite 解析组件库的扩展名省略 ESM 引用。
    server: { deps: { inline: ['@ant-design/pro-components', 'antd'] } },
    environment: 'happy-dom',
    globals: true,
    setupFiles: ['./tests/setupTests.ts'],
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
    // Exclude Umi integration tests that depend on @umijs/max test infrastructure
    // These require Umi's Jest runner and cannot be used with Vitest directly
    exclude: [
      'src/pages/user/login/login.test.tsx',
      'node_modules',
      'dist',
      '.umi',
    ],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'json', 'html'],
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/.umi/**',
        'src/services/ant-design-pro/**',
        'src/**/*.d.ts',
        'src/**/index.style.ts',
      ],
    },
    passWithNoTests: true,
    testTimeout: 15000,
  },
});
