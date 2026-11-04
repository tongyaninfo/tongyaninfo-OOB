/**
 * 本地开发通过同源 /api 访问后端，生产由同源站点或反向代理提供接口。
 * 不将服务器账号、密码或认证信息发送给模板演示服务。
 */
export default {
  dev: {
    '/api/': {
      target: 'http://127.0.0.1:5080',
      changeOrigin: false,
      ws: true,
    },
  },
  test: {
    '/api/': {
      target: 'http://127.0.0.1:5080',
      changeOrigin: false,
      ws: true,
    },
  },
};
