#!/usr/bin/env node
// 生成 Jellyfin 插件仓库清单。用法：node tools/gen-manifest.mjs [baseUrl]
// 每份清单会累计该 Jellyfin 大版本线的全部已发布版本，服务器端就能看到并选择版本。
import { writeFileSync, readFileSync, existsSync, readdirSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');

const DEFAULT_BASE = 'https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/main';
const base = (process.argv[2] || DEFAULT_BASE).replace(/\/+$/, '');

const GUID = 'c7d1a2b4-9e53-4f18-8a2c-6b3d5e7f9041';
const NAME = '合集工坊';
const OWNER = '1374335632-sketch';

// 每条产品线：目标框架、targetAbi、清单文件名；该线下的所有版本都会被列出
const LINES = [
  { tfm: 'net10.0', abi: '12.4.0.0', manifest: 'manifest-12.json', note: 'Jellyfin 12.x' },
  { tfm: 'net9.0', abi: '10.11.0.0', manifest: 'manifest-10.json', note: 'Jellyfin 10.10 / 10.11' },
  { tfm: 'net8.0', abi: '10.9.0.0', manifest: 'manifest-109.json', note: 'Jellyfin 10.9' }
];

const CHANGELOG = {
  '1.0.0': '首个版本：按演员/导演/厂商/类型/年份/复杂条件生成合集，支持刮削写真封面、改名对照表、Excel 导出与备份还原。',
  '1.1': '设置页改为 Jellyfin 控制台内部页面（不再接管整页）；分类规则新增「参与分类的媒体库」选择；适配 Jellyfin 12.4 授权方式。'
};

function changelogFor(version) {
  const major = version.split('.').slice(0, 2).join('.');
  return CHANGELOG[major] || '';
}

// 扫 dist/ 里属于本条产品线的所有包，按版本号降序
function collectVersions(tfm) {
  const dir = join(root, 'dist');
  if (!existsSync(dir)) return [];
  const suffix = '-' + tfm + '.zip';
  return readdirSync(dir)
    .filter((f) => f.startsWith('Jellyfin.Plugin.CollectionStudio-') && f.endsWith(suffix))
    .map((f) => {
      const version = f.replace('Jellyfin.Plugin.CollectionStudio-', '').replace(suffix, '');
      const data = readFileSync(join(dir, f));
      return {
        version,
        changelog: changelogFor(version),
        targetAbi: LINE_ABI(tfm),
        sourceUrl: base + '/dist/' + f,
        checksum: createHash('md5').update(data).digest('hex'),
        timestamp: '2026-11-07T00:00:00Z'
      };
    })
    .sort((a, b) => compareVersion(b.version, a.version));
}

function LINE_ABI(tfm) {
  const hit = LINES.find((l) => l.tfm === tfm);
  return hit ? hit.abi : '';
}

function compareVersion(a, b) {
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d) return d;
  }
  return 0;
}

for (const line of LINES) {
  const versions = collectVersions(line.tfm);
  if (!versions.length) {
    console.warn('跳过 ' + line.manifest + '：dist 里没有 ' + line.tfm + ' 的包');
    continue;
  }
  const manifest = [{
    guid: GUID,
    name: NAME,
    description: '按元数据自动生成合集：演员/导演写真封面、高级条件规则、改名对照表、备份还原，全中文界面。',
    overview: 'Collection Studio for Jellyfin（' + line.note + '）',
    owner: OWNER,
    category: 'General',
    imageUrl: '',
    versions
  }];
  writeFileSync(join(root, line.manifest), JSON.stringify(manifest, null, 2), 'utf8');
  console.log(line.manifest + ' -> ' + versions.length + ' 个版本：' + versions.map((v) => v.version).join(', '));
}
