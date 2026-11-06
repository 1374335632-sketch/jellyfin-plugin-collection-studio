#!/usr/bin/env node
// 从 src/ 打包三个目标框架的插件包到 dist/（可选参数指定目标框架）
import { execFileSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const targets = process.argv.slice(2);
const list = targets.length ? targets : ['net8.0', 'net9.0', 'net10.0'];

for (const tfm of list) {
  console.log('=== building ' + tfm + ' ===');
  execFileSync(process.execPath, [join(here, '..', 'src', 'tools', 'build-zip.mjs'), tfm], { stdio: 'inherit' });
}
console.log('done. packages are in dist/');
