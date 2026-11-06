import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const web = join(here, '..', 'web');

const html = readFileSync(join(web, 'index.html'), 'utf8');
const css = readFileSync(join(web, 'app.css'), 'utf8');
const js = readFileSync(join(web, 'app.js'), 'utf8');

const bodyMatch = html.match(/<body[^>]*>([\s\S]*?)<\/body>/i);
if (!bodyMatch) { throw new Error('index.html 缺少 <body>'); }

let bodyInner = bodyMatch[1]
  .replace(/<link[^>]*href="\.\/app\.css"[^>]*>/gi, '')
  .replace(/<script[^>]*src="\.\/app\.js"[^>]*><\/script>/gi, '')
  .trim();

const styleTag = '<style>\n/* 合集工坊内联样式 */\n' + css + '\n</style>';
const scriptTag = '<script>\n' + js.replace(/<\/script/gi, '<\\/script') + '\n</script>';

const fragment = '<div class="cs-page type-interior" data-role="page" id="csPageRoot">\n'
  + styleTag + '\n' + bodyInner + '\n' + scriptTag + '\n</div>\n';

if (fragment.includes('./app.css') || fragment.includes('./app.js')) {
  throw new Error('内联失败：仍有外部资源引用');
}

writeFileSync(join(web, 'page.html'), fragment, 'utf8');
console.log('generated web/page.html bytes=' + fragment.length);
