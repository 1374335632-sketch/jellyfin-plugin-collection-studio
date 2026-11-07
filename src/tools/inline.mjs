import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const web = join(here, '..', 'web');

const css = readFileSync(join(web, 'app.css'), 'utf8');
const js = readFileSync(join(web, 'app.js'), 'utf8');
const appHtml = readFileSync(join(web, 'index.html'), 'utf8');
const shellHtml = readFileSync(join(web, 'shell.html'), 'utf8');

// ---------------------------------------------------------------- 完整页面
// index.html 是一个真正的完整文档：内联样式与脚本，无外部依赖。
// 它由 /CollectionStudio/Full 返回，被配置页里的 iframe 加载。
// 关键：iframe 加载的是真实文档，脚本一定会执行（不受宿主注入机制影响）。
const bodyMatch = appHtml.match(/<body[^>]*>([\s\S]*?)<\/body>/i);
if (!bodyMatch) { throw new Error('index.html 缺少 <body>'); }
let bodyInner = bodyMatch[1]
  .replace(/<link[^>]*href="\.\/app\.css"[^>]*>/gi, '')
  .replace(/<script[^>]*src="\.\/app\.js"[^>]*><\/script>/gi, '')
  .trim();

const inlineStyleMatch = appHtml.match(/<style>([\s\S]*?)<\/style>/i);
const inlineStyle = inlineStyleMatch ? inlineStyleMatch[1] : '';

const full = '<!doctype html>\n<html lang="zh-CN">\n<head>\n'
  + '<meta charset="utf-8" />\n'
  + '<meta name="viewport" content="width=device-width, initial-scale=1" />\n'
  + '<title>Collection Studio</title>\n'
  + '<style>' + inlineStyle + '\n/* Collection Studio */\n' + css + '\n</style>\n'
  + '</head>\n<body>\n'
  + bodyInner + '\n'
  + '<script>\n' + js.replace(/<\/script/gi, '<\\/script') + '\n</script>\n'
  + '</body>\n</html>\n';

if (full.includes('./app.css') || full.includes('./app.js')) {
  throw new Error('完整页面仍有外部资源引用');
}
if (!full.includes('cs-radio-rules')) {
  throw new Error('完整页面缺少纯 CSS 标签切换规则');
}
if (!full.includes('function runOnce()')) {
  throw new Error('完整页面缺少立即启动逻辑');
}
writeFileSync(join(web, 'full.html'), full, 'utf8');

// ---------------------------------------------------------------- 配置页外壳
// 外壳只保留结构与 iframe：注入页里的脚本不会执行，因此外壳不依赖任何脚本。
const shellBodyMatch = shellHtml.match(/<body[^>]*>([\s\S]*?)<\/body>/i);
if (!shellBodyMatch) { throw new Error('shell.html 缺少 <body>'); }
const shellInner = shellBodyMatch[1].trim();

const page = shellInner + '\n';
if (!page.includes('pluginConfigurationPage') || !page.includes('data-require')) {
  throw new Error('外壳缺少 Jellyfin 配置页标准属性');
}
if (!page.includes('/CollectionStudio/Full')) {
  throw new Error('外壳缺少 iframe 指向完整页面');
}
if (page.includes('<script')) {
  throw new Error('外壳不应包含脚本（注入页不执行脚本）');
}
writeFileSync(join(web, 'page.html'), page, 'utf8');

console.log('generated web/full.html bytes=' + full.length + ', web/page.html bytes=' + page.length);
