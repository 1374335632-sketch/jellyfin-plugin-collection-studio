#!/usr/bin/env node
// 从源码打包三个目标框架的插件包到 dist/
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const targets = process.argv.slice(2);
const list = targets.length ? targets : ['net8.0', 'net9.0', 'net10.0'];

for (const tfm of list) {
  console.log('=== building ' + tfm + ' ===');
  execFileSync('node', [join(here, 'src', 'tools', 'build-zip.mjs'), tfm], { stdio: 'inherit' });
}
console.log('done. packages are in dist/');
