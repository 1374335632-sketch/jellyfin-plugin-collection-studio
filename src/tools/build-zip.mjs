// 打包：生成可直接安装的插件 zip（内含 meta.json）
import { execFileSync } from 'node:child_process';
import { mkdirSync, rmSync, existsSync, copyFileSync, writeFileSync, readdirSync, readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { deflateRawSync, crc32 } from 'node:zlib';

const srcDir = join(dirname(fileURLToPath(import.meta.url)), '..');
const root = join(srcDir, '..');
const DOTNET = 'C:\\Program Files\\dotnet\\dotnet.exe';
const tfm = process.argv[2] || 'net9.0';
const pluginId = 'c7d1a2b4-9e53-4f18-8a2c-6b3d5e7f9041';
const name = 'Jellyfin.Plugin.CollectionStudio';
const targetAbi = tfm === 'net8.0' ? '10.9.0.0' : (tfm === 'net9.0' ? '10.11.0.0' : '12.4.0.0');
const version = tfm === 'net8.0' ? '1.1.0.0' : (tfm === 'net9.0' ? '1.1.0.1' : '1.1.0.2');

const outDir = join(srcDir, '_out-' + tfm);
const stage = join(root, 'dist', 'stage-' + tfm);
rmSync(outDir, { recursive: true, force: true });
rmSync(stage, { recursive: true, force: true });
mkdirSync(stage, { recursive: true });

execFileSync(DOTNET, ['publish', join(srcDir, name + '.csproj'), '-c', 'Release', '-f', tfm, '-o', outDir, '--nologo'], { stdio: 'inherit' });

for (const f of [name + '.dll', 'LiteDB.dll']) {
  const src = join(outDir, f);
  if (existsSync(src)) { copyFileSync(src, join(stage, f)); }
}

const meta = {
  category: 'General',
  changelog: '1.1：插件页改为 Jellyfin 控制台内部设置页（不再接管整页）；分类规则新增「参与分类的媒体库」选择；Jellyfin 12.4 授权方式适配。',
  description: '按元数据自动生成合集：演员/导演写真封面、高级条件规则、改名对照表、备份还原，全中文界面。',
  guid: pluginId,
  name: '合集工坊',
  overview: 'Collection Studio for Jellyfin',
  owner: '合集工坊',
  targetAbi,
  timestamp: new Date().toISOString().replace(/\.\d{3}Z$/, 'Z'),
  version,
  status: 'Active',
  autoUpdate: false,
  imagePath: '',
  assemblies: []
};
writeFileSync(join(stage, 'meta.json'), JSON.stringify(meta, null, 2), 'utf8');

// 手写 zip（PowerShell Compress-Archive 对中文文件名/编码不稳定）
const files = readdirSync(stage).map((f) => ({ name: f, data: readFileSync(join(stage, f)) }));
const zipBytes = buildZip(files);
const zipPath = join(root, 'dist', name + '-' + version + '-' + tfm + '.zip');
writeFileSync(zipPath, zipBytes);
console.log('已生成：' + zipPath + ' (' + zipBytes.length + ' 字节, ' + files.length + ' 个文件)');



function buildZip(entries) {
  const local = [];
  const central = [];
  let offset = 0;
  for (const e of entries) {
    const nameBuf = Buffer.from(e.name, 'utf8');
    const crc = crcOf(e.data);
    const lh = Buffer.alloc(30);
    lh.writeUInt32LE(0x04034b50, 0);
    lh.writeUInt16LE(20, 4);
    lh.writeUInt16LE(0x0800, 6);
    lh.writeUInt16LE(0, 8);
    lh.writeUInt16LE(0, 10);
    lh.writeUInt16LE(0, 12);
    lh.writeUInt32LE(crc, 14);
    lh.writeUInt32LE(e.data.length, 18);
    lh.writeUInt32LE(e.data.length, 22);
    lh.writeUInt16LE(nameBuf.length, 26);
    lh.writeUInt16LE(0, 28);
    const localPart = Buffer.concat([lh, nameBuf, e.data]);
    local.push(localPart);

    const ch = Buffer.alloc(46);
    ch.writeUInt32LE(0x02014b50, 0);
    ch.writeUInt16LE(20, 4);
    ch.writeUInt16LE(20, 6);
    ch.writeUInt16LE(0x0800, 8);
    ch.writeUInt16LE(0, 10);
    ch.writeUInt16LE(0, 12);
    ch.writeUInt16LE(0, 14);
    ch.writeUInt32LE(crc, 16);
    ch.writeUInt32LE(e.data.length, 20);
    ch.writeUInt32LE(e.data.length, 24);
    ch.writeUInt16LE(nameBuf.length, 28);
    ch.writeUInt16LE(0, 30);
    ch.writeUInt16LE(0, 32);
    ch.writeUInt16LE(0, 34);
    ch.writeUInt16LE(0, 36);
    ch.writeUInt32LE(0, 38);
    ch.writeUInt32LE(offset, 42);
    central.push(Buffer.concat([ch, nameBuf]));
    offset += localPart.length;
  }
  const centralBuf = Buffer.concat(central);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(0, 4);
  end.writeUInt16LE(0, 6);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralBuf.length, 12);
  end.writeUInt32LE(offset, 16);
  end.writeUInt16LE(0, 20);
  return Buffer.concat([Buffer.concat(local), centralBuf, end]);
}

function crcOf(buf) {
  let c, crc = 0xFFFFFFFF;
  for (let i = 0; i < buf.length; i++) {
    c = (crc ^ buf[i]) & 0xFF;
    for (let k = 0; k < 8; k++) { c = (c & 1) ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1); }
    crc = (crc >>> 8) ^ c;
  }
  return (crc ^ 0xFFFFFFFF) >>> 0;
}
