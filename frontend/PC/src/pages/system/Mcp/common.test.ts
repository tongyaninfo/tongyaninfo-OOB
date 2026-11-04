import { describe, expect, it } from 'vitest';
import { lines, validNetwork, validOrigin, validRedirect } from './common';

describe('MCP白名单表单校验', () => {
  it('保留合法IPv4 IPv6 CIDR并拒绝错误前缀和简写地址', () => {
    for (const value of [
      '192.0.2.1',
      '192.0.2.0/24',
      '2001:db8::/32',
      '::ffff:192.0.2.1/128',
    ])
      expect(validNetwork(value)).toBe(true);
    for (const value of [
      '127.1',
      '999.0.0.1',
      '192.0.2.1/33',
      '2001:db8::/129',
      '192.0.2.1/-1',
      '::gg',
      '192.0.2.1/1/2',
      '::ffff:192.0.2.1/64',
    ])
      expect(validNetwork(value)).toBe(false);
    expect(lines(' 192.0.2.1\n\n192.0.2.1\r\n2001:db8::/32 ')).toEqual([
      '192.0.2.1',
      '2001:db8::/32',
    ]);
  });
  it('Origin精确匹配并禁止把路径通配符或认证材料当成Origin', () => {
    expect(validOrigin('https://client.example.com')).toBe(true);
    for (const value of [
      'https://client.example.com/',
      'https://*.example.com',
      'https://client.example.com/path',
      'https://user:secret@client.example.com',
      'javascript:alert(1)',
      'http://client.example.com',
    ])
      expect(validOrigin(value)).toBe(false);
    expect(validRedirect('http://127.0.0.1:1234/callback')).toBe(true);
    expect(validRedirect('http://client.example.com/callback')).toBe(false);
    expect(validRedirect('https://client.example.com/callback#fragment')).toBe(
      false,
    );
  });
});
