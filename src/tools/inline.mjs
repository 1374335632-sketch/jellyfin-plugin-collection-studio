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

// 保留 index.html 里的内联 <style>（纯 CSS 标签切换的规则就写在那里）
const inlineStyleMatch = html.match(/<style>([\s\S]*?)<\/style>/i);
const inlineStyle = inlineStyleMatch ? inlineStyleMatch[1] : '';

const styleTag = '<style>' + inlineStyle + '\n/* Collection Studio 内联样式 */\n' + css + '\n</style>';
const scriptTag = '<script>\n' + js.replace(/<\/script/gi, '<\\/script') + '\n</script>';

// 关键：根节点必须是 Jellyfin 标准配置页外壳
// （pluginConfigurationPage + data-require + data-role=page），
// MetaTube 等能在控制台内正常工作的插件页都是这个结构。
// 之前这里手写了一个 <div class="cs-page">，把标准属性丢掉了。
const rootTag = '<div class="page type-interior pluginConfigurationPage CollectionStudioConfigurationPage cs-page"'
  + ' id="csPageRoot" data-require="emby-input,emby-button,emby-select,emby-checkbox,emby-linkbutton"'
  + ' data-role="page" data-cs-root="1">';

const fragment = rootTag + '\n' + styleTag + '\n' + bodyInner + '\n' + scriptTag + '\n</div>\n';

if (fragment.includes('./app.css') || fragment.includes('./app.js')) {
  throw new Error('内联失败：仍有外部资源引用');
}
if (!fragment.includes('pluginConfigurationPage') || !fragment.includes('data-require')) {
  throw new Error('内联失败：根节点缺少 Jellyfin 配置页标准属性');
}
if (!fragment.includes('cs-radio-rules')) {
  throw new Error('内联失败：纯 CSS 标签切换规则丢失');
}

writeFileSync(join(web, 'page.html'), fragment, 'utf8');
console.log('generated web/page.html bytes=' + fragment.length);
