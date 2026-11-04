const { writeFileSync } = require('node:fs');
const path = require('node:path');
const { deflateSync } = require('node:zlib');

// 图标是项目自有的简单电源符号；直接生成 PNG，构建不依赖在线图片或全局转换工具。
function crc32(buffer) {
  let crc = 0xffffffff;
  for (const byte of buffer) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}
function chunk(type, body) {
  const name = Buffer.from(type); const length = Buffer.alloc(4); const crc = Buffer.alloc(4);
  length.writeUInt32BE(body.length); crc.writeUInt32BE(crc32(Buffer.concat([name, body])));
  return Buffer.concat([length, name, body, crc]);
}
function create(size, template) {
  const scan = Buffer.alloc(size * (1 + size * 4));
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const u = (x + 0.5) / size; const v = (y + 0.5) / size;
    const rounded = Math.hypot(Math.max(0.14 - u, 0, u - 0.86), Math.max(0.14 - v, 0, v - 0.86)) < 0.14;
    const distance = Math.hypot(u - 0.5, v - 0.54);
    const ring = Math.abs(distance - 0.255) < 0.048 && !(v < 0.37 && Math.abs(u - 0.5) < 0.13);
    const stroke = Math.abs(u - 0.5) < 0.044 && v >= 0.19 && v <= 0.52;
    const rgba = template ? [0, 0, 0, ring || stroke ? 255 : 0] : ring || stroke ? [255, 255, 255, 255] : rounded ? [24, 86, 171, 255] : [0, 0, 0, 0];
    const offset = y * (1 + size * 4) + 1 + x * 4;
    for (let channel = 0; channel < 4; channel++) scan[offset + channel] = rgba[channel];
  }
  const header = Buffer.alloc(13); header.writeUInt32BE(size, 0); header.writeUInt32BE(size, 4); header[8] = 8; header[9] = 6;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header), chunk('IDAT', deflateSync(scan)), chunk('IEND', Buffer.alloc(0))]);
}
writeFileSync(path.join(__dirname, 'icon.png'), create(512, false));
writeFileSync(path.join(__dirname, 'trayTemplate.png'), create(32, true));

// 原生 ICNS / ICO 容器直接内嵌 PNG，避免打包阶段额外下载图标转换工具。
const icnsParts = [[512, 'ic09'], [1024, 'ic10']].map(([size, type]) => {
  const png = create(size, false); const header = Buffer.alloc(8);
  header.write(type, 0, 4, 'ascii'); header.writeUInt32BE(png.length + 8, 4);
  return Buffer.concat([header, png]);
});
const icnsHeader = Buffer.alloc(8); icnsHeader.write('icns'); icnsHeader.writeUInt32BE(icnsParts.reduce((sum, item) => sum + item.length, 8), 4);
writeFileSync(path.join(__dirname, 'icon.icns'), Buffer.concat([icnsHeader, ...icnsParts]));
const sizes = [16, 32, 48, 256]; const images = sizes.map(size => create(size, false));
const icoHeader = Buffer.alloc(6 + sizes.length * 16); icoHeader.writeUInt16LE(1, 2); icoHeader.writeUInt16LE(sizes.length, 4);
let offset = icoHeader.length;
for (let index = 0; index < sizes.length; index++) {
  const entry = 6 + index * 16;
  icoHeader[entry] = sizes[index] === 256 ? 0 : sizes[index]; icoHeader[entry + 1] = icoHeader[entry];
  icoHeader.writeUInt16LE(1, entry + 4); icoHeader.writeUInt16LE(32, entry + 6);
  icoHeader.writeUInt32LE(images[index].length, entry + 8); icoHeader.writeUInt32LE(offset, entry + 12);
  offset += images[index].length;
}
writeFileSync(path.join(__dirname, 'icon.ico'), Buffer.concat([icoHeader, ...images]));
