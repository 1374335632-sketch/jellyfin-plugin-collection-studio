#!/usr/bin/env node
// 生成 Jellyfin 插件仓库清单。用法：node tools/gen-manifest.mjs [baseUrl]
import { writeFileSync, readFileSync, existsSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');

// 默认下载前缀走 gh-proxy：raw.githubusercontent.com 在国内及部分服务器网络不可直达
const DEFAULT_BASE = 'https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/main';
const base = (process.argv[2] || DEFAULT_BASE).replace(/\/+$/, '');

const GUID = 'c7d1a2b4-9e53-4f18-8a2c-6b3d5e7f9041';
const NAME = '合集工坊';
const OWNER = '1374335632-sketch';

const variants = [
  { file: 'Jellyfin.Plugin.CollectionStudio-1.0.0.2-net10.0.zip', version: '1.0.0.2', abi: '12.4.0.0', manifest: 'manifest-12.json', note: 'Jellyfin 12.x' },
  { file: 'Jellyfin.Plugin.CollectionStudio-1.0.0.1-net9.0.zip', version: '1.0.0.1', abi: '10.11.0.0', manifest: 'manifest-10.json', note: 'Jellyfin 10.10 / 10.11' },
  { file: 'Jellyfin.Plugin.CollectionStudio-1.0.0.0-net8.0.zip', version: '1.0.0.0', abi: '10.9.0.0', manifest: 'manifest-109.json', note: 'Jellyfin 10.9' }
];

for (const v of variants) {
  const pkg = join(root, 'dist', v.file);
  if (!existsSync(pkg)) {
    console.warn('跳过 ' + v.manifest + '：缺 ' + v.file);
    continue;
  }
  const data = readFileSync(pkg);
  const checksum = createHash('md5').update(data).digest('hex');
  const manifest = [{
    guid: GUID,
    name: NAME,
    description: '按元数据自动生成合集：演员/导演写真封面、高级条件规则、改名对照表、备份还原，全中文界面。',
    overview: 'Collection Studio for Jellyfin（' + v.note + '）',
    owner: OWNER,
    category: 'General',
    imageUrl: '',
    versions: [{
      version: v.version,
      changelog: '按演员/导演/厂商/类型/年份/复杂条件生成合集；支持 MetaTube 与自定义刮削源写真封面；改名对照表与 Excel 导出；备份与按时间段还原。',
      targetAbi: v.abi,
      sourceUrl: base + '/dist/' + v.file,
      checksum: checksum,
      timestamp: '2026-11-07T00:00:00Z'
    }]
  }];
  writeFileSync(join(root, v.manifest), JSON.stringify(manifest, null, 2), 'utf8');
  console.log(v.manifest + ' -> ' + v.file + ' (md5 ' + checksum + ')');
}
