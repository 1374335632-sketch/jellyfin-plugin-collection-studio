'use strict';
(function () {

  // ===========================================================
  // 基础工具
  // ===========================================================
  // Jellyfin 控制台会把插件页缓存若干份在同名容器里，同名 id 会出现重复；
  // 因此所有查找都限定在当前这份页面的根节点内，找不到才回落到 document。
  var rootScope = null;

  function pageRootOf(node) {
    var n = node;
    while (n && n !== document) {
      if (n.classList && (n.classList.contains('cs-page') || n.classList.contains('cs-app'))) { return n; }
      n = n.parentNode;
    }
    return null;
  }

  // 宿主重渲染会留下游离的旧根节点，只有仍挂在文档上的根才可用
  function connectedRoot() {
    return (rootScope && rootScope.isConnected) ? rootScope : null;
  }

  function $(id) {
    var scope = connectedRoot() || document.querySelector('.cs-page, .cs-app') || document.body;
    if (scope) {
      var inScope = scope.querySelector('[id="' + id + '"]');
      if (inScope) { return inScope; }
    }
    return document.getElementById(id);
  }

  function $$(selector) {
    return (rootScope || document).querySelectorAll(selector);
  }

  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) { n.className = cls; }
    if (text !== undefined && text !== null) { n.textContent = String(text); }
    return n;
  }

  function clear(node) {
    while (node && node.firstChild) { node.removeChild(node.firstChild); }
  }

  // 插件页通常被 Jellyfin 控制台以 iframe 嵌入，ApiClient 在父窗口上
  function apiClientRef() {
    if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') { return window.ApiClient; }
    try {
      if (window.parent && window.parent !== window && window.parent.ApiClient
          && typeof window.parent.ApiClient.getUrl === 'function') {
        return window.parent.ApiClient;
      }
    } catch (e) { /* 跨窗口访问被拒时忽略 */ }
    return null;
  }

  // 从任意 localStorage 记录里找出 AccessToken（Jellyfin 会把它存在 jellyfin_credentials 等键里）
  function findTokenDeep(node, depth) {
    if (depth > 6 || node === null || typeof node !== 'object') { return ''; }
    if (Array.isArray(node)) {
      for (var i = 0; i < node.length; i++) {
        var t = findTokenDeep(node[i], depth + 1);
        if (t) { return t; }
      }
      return '';
    }
    for (var k in node) {
      if (!Object.prototype.hasOwnProperty.call(node, k)) { continue; }
      if (/^accesstoken$/i.test(k) && typeof node[k] === 'string' && node[k]) { return node[k]; }
      var v = findTokenDeep(node[k], depth + 1);
      if (v) { return v; }
    }
    return '';
  }

  function tokenFromStorage() {
    var stores = [];
    try { stores.push(window.localStorage); } catch (e) { /* 被禁用 */ }
    try { stores.push(window.sessionStorage); } catch (e) { /* 被禁用 */ }
    for (var s = 0; s < stores.length; s++) {
      var store = stores[s];
      if (!store) { continue; }
      for (var i = 0; i < store.length; i++) {
        var key = store.key(i);
        if (!key) { continue; }
        var raw = store.getItem(key);
        if (!raw || raw.length > 200000) { continue; }
        if (/^[0-9a-f]{32}$/i.test(raw.trim())) { return raw.trim(); }
        if (raw.indexOf('AccessToken') < 0 && raw.indexOf('accessToken') < 0) { continue; }
        try {
          var t = findTokenDeep(JSON.parse(raw), 0);
          if (t) { return t; }
        } catch (e) { /* 不是 JSON，跳过 */ }
      }
    }
    return '';
  }

  function tokenFromQuery() {
    try {
      var m = /(?:^|[?&#])api_key=([^&]+)/.exec(location.search + location.hash);
      if (m) { return decodeURIComponent(m[1]); }
      var m2 = /(?:^|[?&#])token=([^&]+)/.exec(location.search + location.hash);
      if (m2) { return decodeURIComponent(m2[1]); }
    } catch (e) { /* 忽略 */ }
    return '';
  }

  function authToken() {
    var c = apiClientRef();
    if (c && typeof c.accessToken === 'function') {
      var v = c.accessToken();
      if (v) { return String(v); }
    }
    var t = tokenFromQuery();
    if (t) { return t; }
    t = tokenFromStorage();
    if (t) { return t; }
    try { return localStorage.getItem('cs_token') || ''; } catch (e) { return ''; }
  }

  // 相对路径拼装；有 ApiClient 时用 ApiClient.getUrl，否则用同源兜底
  function apiUrl(p) {
    var c = apiClientRef();
    if (c) { return c.getUrl('CollectionStudio/' + p); }
    var base = location.origin;
    var t = authToken();
    var q = p.indexOf('?') >= 0 ? '&' : '?';
    return base + '/CollectionStudio/' + p + (t ? q + 'api_key=' + encodeURIComponent(t) : '');
  }

  function makeHttpError(status, body) {
    var msg = '';
    if (body) {
      try {
        var d = JSON.parse(body);
        if (d && d.error) { msg = String(d.error); }
      } catch (e) { /* 非 JSON 响应 */ }
    }
    var err = new Error(msg || ('请求失败：HTTP ' + status));
    err.status = status;
    return err;
  }

  function apiRequest(p, options) {
    var opt = options || {};
    var headers = {};
    var token = authToken();
    if (token) {
      // Jellyfin 12.4 起默认关闭 legacy 授权，X-Emby-Token 与 api_key 都不再被认可；
      // MediaBrowser Token 形式在 10.x 与 12.x 上都能通过，因此统一用它，旧头留作兼容。
      headers['Authorization'] = 'MediaBrowser Token="' + token + '", Client="CollectionStudio", Device="CollectionStudio", DeviceId="collection-studio", Version="1.0.0"';
      headers['X-Emby-Token'] = token;
    }
    var init = { method: opt.method || 'GET', headers: headers, credentials: 'same-origin', cache: 'no-store' };
    if (opt.body !== undefined && opt.body !== null) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(opt.body);
    }
    return fetch(apiUrl(p), init).then(function (res) {
      if (!res.ok) {
        return res.text().then(function (text) {
          throw makeHttpError(res.status, text);
        }, function () {
          throw makeHttpError(res.status, '');
        });
      }
      var ct = res.headers.get('content-type') || '';
      if (opt.text || ct.indexOf('json') < 0) { return res.text(); }
      return res.json().then(function (v) { return v; }, function () { return null; });
    });
  }

  function getJson(p) { return apiRequest(p); }
  function sendJson(method, p, body) { return apiRequest(p, { method: method, body: body }); }

  // ===========================================================
  // Toast
  // ===========================================================
  function toast(message, kind) {
    var wrap = $('csToast');
    if (!wrap) { return; }
    var box = el('div', 'cs-toast' + (kind ? ' cs-toast-' + kind : ''));
    var text = el('div', 'cs-toast-text', message);
    var close = el('button', 'cs-toast-close', '×');
    close.type = 'button';
    close.setAttribute('aria-label', '关闭提示');
    box.appendChild(text);
    box.appendChild(close);
    wrap.appendChild(box);
    var timer = setTimeout(remove, kind === 'error' ? 6500 : 3600);
    function remove() {
      if (!box.parentNode) { return; }
      clearTimeout(timer);
      box.classList.remove('is-open');
      setTimeout(function () { if (box.parentNode) { box.parentNode.removeChild(box); } }, 220);
    }
    close.addEventListener('click', remove);
    requestAnimationFrame(function () { box.classList.add('is-open'); });
  }

  function failToast(err) {
    var msg = (err && err.message) ? err.message : '请求失败';
    toast(msg, 'error');
  }

  // ===========================================================
  // 格式化
  // ===========================================================
  function pad2(n) { return (n < 10 ? '0' : '') + n; }

  function fmtDateObj(d) {
    return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()) +
      ' ' + pad2(d.getHours()) + ':' + pad2(d.getMinutes()) + ':' + pad2(d.getSeconds());
  }

  // 统一显示 yyyy-MM-dd HH:mm:ss
  function fmtDate(v) {
    if (v === undefined || v === null || v === '') { return '—'; }
    var s = String(v);
    var m = s.match(/(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2}):(\d{2})/);
    if (m) {
      return m[1] + '-' + m[2] + '-' + m[3] + ' ' + m[4] + ':' + m[5] + ':' + m[6];
    }
    var d = new Date(s);
    if (!isNaN(d.getTime())) { return fmtDateObj(d); }
    return s;
  }

  function fmtKB(bytes) {
    var n = Number(bytes);
    if (!isFinite(n) || n <= 0) { return '0'; }
    var kb = n / 1024;
    return kb >= 100 ? String(Math.round(kb)) : kb.toFixed(1);
  }

  function resolveImageUrl(u) {
    if (!u) { return ''; }
    var s = String(u);
    if (/^(https?:|data:)/i.test(s)) { return s; }
    // 服务器自身的图片地址（如 /Items/{id}/Images/Primary）不能加插件前缀，直接按服务器根拼接
    if (/^\/items\//i.test(s) || /^\/web\//i.test(s)) {
      var base = location.origin + s;
      var st = authToken();
      if (st) { base += (base.indexOf('?') >= 0 ? '&' : '?') + 'api_key=' + encodeURIComponent(st); }
      return base;
    }
    var p = s.replace(/^\/+/, '');
    if (p.indexOf('CollectionStudio/') === 0) { p = p.substring('CollectionStudio/'.length); }
    var url = apiUrl(p);
    // <img> 发不出自定义请求头，走查询串：12.4 认 ApiKey，10.x 认 api_key，两个都带上
    if (p.indexOf('Covers/Proxy') === 0) {
      var tk = authToken();
      if (tk) {
        url += (url.indexOf('?') >= 0 ? '&' : '?')
          + 'ApiKey=' + encodeURIComponent(tk)
          + '&api_key=' + encodeURIComponent(tk);
      }
    }
    return url;
  }

  function makeThumb(url, cls) {
    var src = resolveImageUrl(url);
    if (!src) {
      var ph = el('div', 'cs-thumb-empty', '无封面');
      if (cls) { ph.className += ' ' + cls; }
      return ph;
    }
    var img = el('img', cls || 'cs-thumb');
    img.src = src;
    img.alt = '封面缩略图';
    img.loading = 'lazy';
    img.addEventListener('error', function () {
      var ph = el('div', 'cs-thumb-empty', '无封面');
      if (cls) { ph.className += ' ' + cls; }
      if (img.parentNode) { img.parentNode.replaceChild(ph, img); }
    });
    return img;
  }

  function tableMessage(tbody, text, colspan) {
    clear(tbody);
    var tr = el('tr', 'cs-table-msg');
    var td = el('td', '', text);
    td.colSpan = colspan || 1;
    tr.appendChild(td);
    tbody.appendChild(tr);
  }

  function labeledField(label, input) {
    var wrap = el('label', 'cs-field');
    wrap.appendChild(el('span', 'cs-label', label));
    wrap.appendChild(input);
    return wrap;
  }

  function makeInput(cls, type, value) {
    var i = el('input', cls || 'cs-input');
    i.type = type || 'text';
    if (value !== undefined && value !== null) { i.value = String(value); }
    return i;
  }

  function makeSelect(cls, options, value, placeholder) {
    var s = el('select', cls || 'cs-select');
    if (placeholder) {
      var o0 = el('option', '', placeholder);
      o0.value = '';
      s.appendChild(o0);
    }
    (options || []).forEach(function (o) {
      var op = el('option', '', o.label);
      op.value = String(o.value);
      s.appendChild(op);
    });
    if (value !== undefined && value !== null) { s.value = String(value); }
    return s;
  }

  function makeButton(text, cls, onClick) {
    var b = el('button', 'cs-btn' + (cls ? ' ' + cls : ''), text);
    b.type = 'button';
    if (onClick) { b.addEventListener('click', onClick); }
    return b;
  }

  function makeBadge(text, cls) {
    return el('span', 'cs-badge' + (cls ? ' ' + cls : ''), text);
  }

  // ===========================================================
  // 模态层
  // ===========================================================
  function openDialog(opts) {
    // 弹窗挂在本插件自己的容器里，绝不往宿主 body 追加节点（那会污染整个控制台页面）。
    var root = $('csModalRoot');
    if (!root) { return null; }
    root.hidden = false;

    var layer = el('div', 'cs-layer');
    var backdrop = el('div', 'cs-backdrop');
    var dialog = el('div', 'cs-dialog' + (opts.wide ? ' cs-dialog-wide' : ''));
    dialog.setAttribute('role', 'dialog');
    dialog.setAttribute('aria-modal', 'true');

    var head = el('div', 'cs-dialog-head');
    var title = el('h2', 'cs-dialog-title', opts.title || '');
    var closeBtn = el('button', 'cs-icon-btn', '×');
    closeBtn.type = 'button';
    closeBtn.title = '关闭';
    closeBtn.setAttribute('aria-label', '关闭');
    head.appendChild(title);
    head.appendChild(closeBtn);

    var body = el('div', 'cs-dialog-body');
    var foot = el('div', 'cs-dialog-foot');
    dialog.appendChild(head);
    dialog.appendChild(body);
    dialog.appendChild(foot);
    layer.appendChild(backdrop);
    layer.appendChild(dialog);
    root.appendChild(layer);

    var closed = false;
    var api = {
      dialog: dialog, body: body, foot: foot, title: title, close: close,
      setTitle: function (t) { title.textContent = t; },
      setFooter: function (nodes) {
        clear(foot);
        (nodes || []).forEach(function (n) { foot.appendChild(n); });
      }
    };

    function onKey(e) { if (e.key === 'Escape') { close(); } }

    function close() {
      if (closed) { return; }
      closed = true;
      document.removeEventListener('keydown', onKey);
      layer.classList.remove('is-open');
      setTimeout(function () {
        if (layer.parentNode) { layer.parentNode.removeChild(layer); }
        if (!root.childNodes.length) { root.hidden = true; }
      }, 200);
      if (opts.onClose) { opts.onClose(); }
    }

    closeBtn.addEventListener('click', close);
    backdrop.addEventListener('click', close);
    document.addEventListener('keydown', onKey);
    requestAnimationFrame(function () { layer.classList.add('is-open'); });
    return api;
  }

  // 中文二次确认；返回 Promise<boolean>
  function confirmDialog(opts) {
    var o = opts || {};
    return new Promise(function (resolve) {
      var settled = false;
      var layer = openDialog({
        title: o.title || '请确认',
        onClose: function () {
          if (!settled) { settled = true; resolve(false); }
        }
      });
      var box = el('div', 'cs-warn-box');
      (o.lines || []).forEach(function (line) { box.appendChild(el('p', '', line)); });
      layer.body.appendChild(box);
      if (o.note) { layer.body.appendChild(el('p', 'cs-hint', o.note)); }

      var cancel = el('button', 'cs-btn', o.cancelText || '取消');
      cancel.type = 'button';
      var ok = el('button', 'cs-btn' + (o.danger === false ? ' cs-btn-primary' : ' cs-btn-danger'), o.okText || '确认');
      ok.type = 'button';

      cancel.addEventListener('click', function () {
        if (!settled) { settled = true; resolve(false); }
        layer.close();
      });
      ok.addEventListener('click', function () {
        if (!settled) { settled = true; resolve(true); }
        layer.close();
      });
      layer.setFooter([cancel, ok]);
      setTimeout(function () { try { ok.focus(); } catch (e) { } }, 60);
    });
  }

  // ===========================================================
  // 常量字典
  // ===========================================================
  var MODE_OPTIONS = [
    { value: 'person', label: '按演员 / 按导演', sub: '按人物分组' },
    { value: 'studio', label: '按厂商' },
    { value: 'genre', label: '按类型（genre）' },
    { value: 'year', label: '按发行年份' },
    { value: 'dateRange', label: '按发行日期范围' },
    { value: 'custom', label: '高级自定义' }
  ];

  // 契约 mode 允许值：person|studio|genre|year|dateRange|custom
  // 「按导演」通过 personType=Director 表达
  var MODE_LABELS = {
    person: '按演员/导演',
    studio: '按厂商',
    genre: '按类型',
    year: '按发行年份',
    dateRange: '按发行日期范围',
    custom: '高级自定义'
  };

  // 契约未给出 op 的中文映射表，这里按 /Fields 契约中的 ops 集合硬编码枚举
  var OP_LABELS = {
    eq: '等于',
    ne: '不等于',
    contains: '包含',
    notcontains: '不包含',
    gt: '大于',
    lt: '小于',
    ge: '大于等于',
    le: '小于等于',
    startswith: '开头是'
  };
  var OP_ORDER = ['eq', 'ne', 'contains', 'notcontains', 'gt', 'lt', 'ge', 'le', 'startswith'];

  var SORT_OPTIONS = [
    { value: 'premiereDate', label: '发行日期' },
    { value: 'productionYear', label: '发行年份' },
    { value: 'name', label: '名称' },
    { value: 'dateCreated', label: '信息添加时间' },
    { value: 'runtime', label: '时长' }
  ];

  var COVER_MODE_OPTIONS = [
    { value: 'firstItem', label: '取第一个影片封面' },
    { value: 'scraped', label: '刮削演员写真' },
    { value: 'url', label: '自定义 URL' },
    { value: 'none', label: '不设封面' }
  ];

  var BACKUP_KIND_LABELS = {
    manual: '手动',
    auto: '自动',
    predelete: '删除前自动'
  };

  // ===========================================================
  // 全局状态
  // ===========================================================
  var state = {
    fields: [],
    fieldsLoaded: false,
    rules: [],
    editingRuleId: null,
    collections: [],
    rename: { page: 1, pageSize: 50, filter: '', total: 0, rows: [] },
    backups: [],
    settings: null,
    tasks: [],
    libraries: [],
    currentTab: 'overview'
  };

  // ===========================================================
  // 导航
  // ===========================================================
  var TAB_META = {
    overview: { title: '总览', sub: '插件运行状态与索引概况' },
    rules: { title: '分类规则', sub: '按人物、厂商、类型、年份或自定义条件生成合集' },
    collections: { title: '合集管理', sub: '已生成的合集、封面与成员影片' },
    rename: { title: '改名对照', sub: '媒体文件原始名称与 Jellyfin 名称对照' },
    backups: { title: '备份与还原', sub: '数据备份、删除与还原' },
    settings: { title: '设置', sub: 'MetaTube 与自定义刮削源配置' }
  };

  function switchTab(tab, root) {
    if (!TAB_META[tab]) { return; }
    var scope = (root || connectedRoot() || document);
    state.currentTab = tab;
    var navList = scope ? scope.querySelector('#csNavList') : null;
    var items = navList ? navList.querySelectorAll('.cs-nav-item') : document.querySelectorAll('.cs-nav-item');
    Array.prototype.forEach.call(items, function (btn) {
      var on = btn.getAttribute('data-tab') === tab;
      btn.classList.toggle('is-active', on);
      if (on) { btn.setAttribute('aria-current', 'page'); } else { btn.removeAttribute('aria-current'); }
    });
    var views = (scope || document).querySelectorAll('.cs-view');
    Array.prototype.forEach.call(views, function (v) {
      var on = v.getAttribute('data-view') === tab;
      v.hidden = !on;
      v.classList.toggle('is-active', on);
    });
    var meta = TAB_META[tab];
    var titleEl = $('csPageTitle');
    var subEl = $('csPageSub');
    if (titleEl) { titleEl.textContent = meta.title; }
    if (subEl) { subEl.textContent = meta.sub; }
    loadTab(tab);
  }

  function loadTab(tab) {
    if (tab === 'overview') { loadOverview(); }
    else if (tab === 'rules') { loadRules(); }
    else if (tab === 'collections') { loadCollections(); }
    else if (tab === 'rename') { loadRename(); }
    else if (tab === 'backups') { loadBackups(); }
    else if (tab === 'settings') { loadSettings(); }
  }

  // ===========================================================
  // 总览
  // ===========================================================
  function statCell(label, value, cls) {
    var box = el('div', 'cs-stat');
    box.appendChild(el('div', 'cs-stat-label', label));
    box.appendChild(el('div', 'cs-stat-value' + (cls ? ' ' + cls : ''), value));
    return box;
  }

  function renderStatus(s) {
    var grid = $('ovStatusGrid');
    clear(grid);
    if (!s) {
      grid.appendChild(statCell('插件状态', '暂无数据', 'is-dim'));
      return;
    }
    grid.appendChild(statCell('插件版本', s.pluginVersion || '—'));
    grid.appendChild(statCell('Jellyfin 版本', s.jellyfinVersion || '—'));
    grid.appendChild(statCell('服务器时间', fmtDate(s.serverTime), 'is-dim'));
    grid.appendChild(statCell('已索引影片数', s.indexedItems !== undefined ? s.indexedItems : '—'));
    grid.appendChild(statCell('人物数', s.personCount !== undefined ? s.personCount : '—'));

    var mt = '未配置';
    var mtCls = 'is-warn';
    if (s.metaTubeConfigured === false) { mt = '未配置'; mtCls = 'is-warn'; }
    else if (s.metaTubeReachable === true) { mt = '已配置 · 可连通'; mtCls = 'is-ok'; }
    else if (s.metaTubeReachable === false) { mt = '已配置 · 不可连通'; mtCls = 'is-warn'; }
    else { mt = '已配置 · 未探测'; mtCls = 'is-dim'; }
    grid.appendChild(statCell('MetaTube 状态', mt, mtCls));

    grid.appendChild(statCell('备份数量', s.backupCount !== undefined ? s.backupCount : '—'));
    grid.appendChild(statCell('最近索引时间', fmtDate(s.lastIndexedAt), 'is-dim'));
  }

  function loadOverview() {
    clear($('ovStatusGrid'));
    $('ovStatusGrid').appendChild(statCell('插件状态', '加载中…', 'is-dim'));
    getJson('Status').then(function (s) {
      renderStatus(s);
    }, function (err) {
      clear($('ovStatusGrid'));
      $('ovStatusGrid').appendChild(statCell('插件状态', '加载失败', 'is-warn'));
      failToast(err);
    }).then(loadTasks);
  }

  function renderTasks(list) {
    var tbody = $('ovTasksBody');
    var sel = $('ovLogTaskSelect');
    var prevSel = sel.value;
    clear(tbody);
    clear(sel);
    state.tasks = list || [];
    if (!state.tasks.length) {
      tableMessage(tbody, '暂无数据', 4);
      $('ovLogMeta').textContent = '';
      renderLog({ lines: [] });
      return;
    }
    state.tasks.forEach(function (t) {
      var tr = el('tr');
      tr.appendChild(el('td', '', t.name || '—'));
      tr.appendChild(el('td', 'cs-mono', t.id || '—'));
      tr.appendChild(el('td', '', fmtDate(t.lastRun)));
      var stCell = el('td');
      var running = String(t.state || '') === 'running';
      stCell.appendChild(makeBadge(running ? '运行中' : '空闲', running ? 'cs-badge-ok' : 'cs-badge-dim'));
      tr.appendChild(stCell);
      tbody.appendChild(tr);

      var op = el('option', '', (t.name || t.id) + '（' + (t.id || '') + '）');
      op.value = String(t.id || '');
      sel.appendChild(op);
    });
    if (prevSel && state.tasks.some(function (t) { return String(t.id) === prevSel; })) {
      sel.value = prevSel;
    }
    loadTaskLog();
  }

  function loadTasks() {
    tableMessage($('ovTasksBody'), '加载中…', 4);
    getJson('Tasks').then(function (list) {
      renderTasks(list);
    }, function (err) {
      tableMessage($('ovTasksBody'), '加载失败', 4);
      failToast(err);
    });
  }

  function renderLog(data) {
    var box = $('ovLogList');
    clear(box);
    var lines = (data && data.lines) || [];
    if (!lines.length) {
      box.appendChild(el('div', 'cs-log-dim cs-empty', '暂无数据'));
      return;
    }
    lines.forEach(function (line) {
      box.appendChild(el('div', 'cs-log-line', line));
    });
  }

  function loadTaskLog() {
    var sel = $('ovLogTaskSelect');
    var id = sel.value;
    clear($('ovLogList'));
    $('ovLogList').appendChild(el('div', 'cs-log-dim cs-empty', '加载中…'));
    if (!id) {
      $('ovLogMeta').textContent = '';
      renderLog({ lines: [] });
      return;
    }
    $('ovLogMeta').textContent = '任务 ' + id + ' · 最近 200 行';
    getJson('Tasks/' + encodeURIComponent(id) + '/Log?lines=200').then(function (data) {
      renderLog(data);
    }, function (err) {
      clear($('ovLogList'));
      $('ovLogList').appendChild(el('div', 'cs-log-dim cs-empty', '加载失败'));
      failToast(err);
    });
  }

  // ===========================================================
  // 分类规则
  // ===========================================================
  // 模式下拉用伪值组合 mode + personType：契约 mode=person 时由 personType 区分演员/导演
  var RULE_MODE_CHOICES = [
    { value: 'person:Actor', label: '按演员', mode: 'person', personType: 'Actor' },
    { value: 'person:Director', label: '按导演', mode: 'person', personType: 'Director' },
    { value: 'studio', label: '按厂商', mode: 'studio' },
    { value: 'genre', label: '按类型(genre)', mode: 'genre' },
    { value: 'year', label: '按发行年份', mode: 'year' },
    { value: 'dateRange', label: '按发行日期范围', mode: 'dateRange' },
    { value: 'custom', label: '高级自定义', mode: 'custom' }
  ];

  function modeChoiceOf(rule) {
    var mode = (rule && rule.mode) || 'person';
    if (mode === 'person') {
      return (rule && rule.personType === 'Director') ? 'person:Director' : 'person:Actor';
    }
    return mode;
  }

  function modeLabel(code) {
    if (!code) { return '—'; }
    for (var i = 0; i < RULE_MODE_CHOICES.length; i++) {
      if (RULE_MODE_CHOICES[i].value === code) { return RULE_MODE_CHOICES[i].label; }
      if (RULE_MODE_CHOICES[i].mode === code) { return RULE_MODE_CHOICES[i].label; }
    }
    return MODE_LABELS[code] || code;
  }

  function coverModeLabel(v) {
    for (var i = 0; i < COVER_MODE_OPTIONS.length; i++) {
      if (COVER_MODE_OPTIONS[i].value === v) { return COVER_MODE_OPTIONS[i].label; }
    }
    return v || '—';
  }

  function sortLabel(v) {
    for (var i = 0; i < SORT_OPTIONS.length; i++) {
      if (SORT_OPTIONS[i].value === v) { return SORT_OPTIONS[i].label; }
    }
    return v || '—';
  }

  function opLabel(v) { return OP_LABELS[v] || v || '—'; }

  function fieldLabel(key) {
    if (!key) { return '—'; }
    for (var i = 0; i < state.fields.length; i++) {
      if (state.fields[i].key === key) { return state.fields[i].label || key; }
    }
    return key;
  }

  function conditionSummary(rule) {
    var list = rule.conditions || [];
    if (!list.length) {
      return rule.mode === 'custom' ? '未设置条件' : '按分组字段自动生成';
    }
    return list.map(function (c, i) {
      var joiner = i === 0 ? '' : (c.conjunction === 'or' ? ' 或 ' : ' 与 ');
      return joiner + fieldLabel(c.field) + ' ' + opLabel(c.op) + ' 「' + (c.value === undefined || c.value === null ? '' : c.value) + '」';
    }).join('');
  }

  function opsForField(fieldKey) {
    for (var i = 0; i < state.fields.length; i++) {
      var f = state.fields[i];
      if (f.key === fieldKey && Array.isArray(f.ops) && f.ops.length) { return f.ops; }
    }
    return OP_ORDER.slice();
  }

  function loadFields() {
    if (state.fieldsLoaded) { return Promise.resolve(state.fields); }
    return getJson('Fields').then(function (list) {
      state.fields = Array.isArray(list) ? list : [];
      state.fieldsLoaded = true;
      return state.fields;
    }, function (err) {
      state.fields = [
        { key: 'actor', label: '演员' }, { key: 'director', label: '导演' },
        { key: 'studio', label: '厂商(片商)' }, { key: 'name', label: '标题' },
        { key: 'num', label: '品番' }, { key: 'year', label: '发行年份' },
        { key: 'premiereDate', label: '发行日期' }, { key: 'genre', label: '类型' },
        { key: 'tag', label: '标签' }, { key: 'rating', label: '评分' },
        { key: 'runtime', label: '时长(分钟)' }, { key: 'officialRating', label: '官方分级' },
        { key: 'itemType', label: '媒体类型' }, { key: 'path', label: '路径' },
        { key: 'providerId', label: '外部ID' }
      ];
      state.fieldsLoaded = true;
      failToast(err);
      return state.fields;
    });
  }

  function renderRules() {
    var tbody = $('ruleListBody');
    clear(tbody);
    if (!state.rules.length) {
      tableMessage(tbody, '暂无数据', 9);
      return;
    }
    state.rules.forEach(function (rule) {
      var tr = el('tr');

      var nameCell = el('td');
      nameCell.appendChild(el('div', '', rule.name || '（未命名规则）'));
      if (rule.enabled === false) { nameCell.appendChild(makeBadge('已停用', 'cs-badge-dim')); }
      tr.appendChild(nameCell);

      tr.appendChild(el('td', '', modeLabel(modeChoiceOf(rule))));
      tr.appendChild(el('td', 'cs-ellipsis', librarySummary(rule.libraryIds)));
      tr.appendChild(el('td', 'cs-ellipsis', rule.targetName || rule.titleTemplate || '—'));
      tr.appendChild(el('td', 'cs-ellipsis', conditionSummary(rule)));
      tr.appendChild(el('td', '', sortLabel(rule.sortBy) + ' / ' + (rule.sortDesc ? '降序' : '升序')));
      tr.appendChild(el('td', '', coverModeLabel(rule.coverMode)));
      tr.appendChild(el('td', '', fmtDate(rule.updatedAt || rule.createdAt)));

      var opsCell = el('td');
      var ops = el('div', 'cs-actions');
      ops.appendChild(makeButton('预演', 'cs-btn-sm', function () { previewRule(rule); }));
      ops.appendChild(makeButton('执行', 'cs-btn-sm cs-btn-primary', function () { applyRule(rule); }));
      ops.appendChild(makeButton('编辑', 'cs-btn-sm', function () { openRuleForm(rule); }));
      ops.appendChild(makeButton('删除', 'cs-btn-sm cs-btn-ghost-danger', function () { deleteRule(rule); }));
      opsCell.appendChild(ops);
      tr.appendChild(opsCell);
      tbody.appendChild(tr);
    });
  }

  // 规则限定的媒体库摘要
  function librarySummary(ids) {
    if (!ids || !ids.length) { return '全部媒体库'; }
    if (!state.libraries || !state.libraries.length) { return ids.length + ' 个媒体库'; }
    var names = ids.map(function (id) {
      var hit = null;
      state.libraries.forEach(function (l) { if (l.id === id) { hit = l; } });
      return hit ? hit.name : '已删除的媒体库';
    });
    return names.join('、');
  }

  function loadRules() {
    tableMessage($('ruleListBody'), '加载中…', 9);
    loadFields().then(function () { return getJson('Rules'); }).then(function (list) {
      state.rules = Array.isArray(list) ? list : [];
      renderRules();
    }, function (err) {
      tableMessage($('ruleListBody'), '加载失败', 9);
      failToast(err);
    });
  }

  // ---------------- 规则表单 ----------------
  function buildConditionRow(cond) {
    var c = cond || { field: '', op: 'eq', value: '', conjunction: 'and' };
    var row = el('div', 'cs-cond-row');

    var fieldSel = makeSelect('cs-select', state.fields.map(function (f) {
      return { value: f.key, label: f.label || f.key };
    }), c.field, '选择字段');

    var opSel = makeSelect('cs-select', OP_ORDER.map(function (k) {
      return { value: k, label: OP_LABELS[k] };
    }), c.op || 'eq');

    // 字段变化时按 /Fields 的 ops 动态重建运算符下拉，不硬编码 option
    fieldSel.addEventListener('change', function () {
      var cur = opSel.value;
      var ops = opsForField(fieldSel.value);
      clear(opSel);
      ops.forEach(function (k) {
        var op = el('option', '', OP_LABELS[k] || k);
        op.value = k;
        opSel.appendChild(op);
      });
      if (ops.indexOf(cur) >= 0) { opSel.value = cur; }
    });

    var valueInput = makeInput('cs-input', 'text', c.value === undefined ? '' : c.value);
    valueInput.placeholder = '条件值，例如：剧情';

    var conjSel = makeSelect('cs-select', [
      { value: 'and', label: '与 and' },
      { value: 'or', label: '或 or' }
    ], c.conjunction || 'and');

    var delBtn = makeButton('删除', 'cs-btn-sm cs-btn-ghost-danger cs-cond-del', function () {
      row.parentNode.removeChild(row);
      if (!$('rfConditionList').children.length) { renderConditionEmpty(); }
    });

    row.appendChild(fieldSel);
    row.appendChild(opSel);
    row.appendChild(valueInput);
    row.appendChild(conjSel);
    row.appendChild(delBtn);

    // 保存引用，便于收集表单值
    row.__cond = { fieldSel: fieldSel, opSel: opSel, valueInput: valueInput, conjSel: conjSel };
    return row;
  }

  function renderConditionEmpty() {
    var list = $('rfConditionList');
    if (list.children.length) { return; }
    list.appendChild(el('div', 'cs-cond-empty', '暂无条件，点击「+ 添加条件」新增一行'));
  }

  function addConditionRow(cond) {
    var list = $('rfConditionList');
    var empty = list.querySelector('.cs-cond-empty');
    if (empty) { list.removeChild(empty); }
    list.appendChild(buildConditionRow(cond));
  }

  function syncConditionsVisibility() {
    var isCustom = $('rfMode').value === 'custom';
    $('rfConditionsWrap').hidden = !isCustom;
  }

  // 媒体库列表（规则里限定分类范围用），只加载一次并缓存
  function renderLibraryPicker(selected) {
    var box = $('rfLibraryList');
    if (!box) { return; }
    clear(box);
    var libs = state.libraries || [];
    if (!libs.length) {
      box.appendChild(el('p', 'cs-hint', '暂无媒体库：请先在控制台「媒体库」里添加影片库。'));
      return;
    }
    var chosen = {};
    (selected || []).forEach(function (id) { chosen[id] = true; });

    libs.forEach(function (lib) {
      var row = el('label', 'cs-lib-item');
      var cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.value = lib.id;
      cb.checked = Object.prototype.hasOwnProperty.call(chosen, lib.id);
      var text = el('span', 'cs-lib-name', lib.name || '(未命名媒体库)');
      var meta = el('span', 'cs-lib-meta', (lib.collectionType === 'movies' ? '电影' : (lib.collectionType || '未指定')) + ' · 已索引 ' + (lib.itemCount || 0) + ' 条');
      row.appendChild(cb);
      row.appendChild(text);
      row.appendChild(meta);
      box.appendChild(row);
    });
  }

  function selectedLibraryIds() {
    var box = $('rfLibraryList');
    if (!box) { return []; }
    var out = [];
    Array.prototype.forEach.call(box.querySelectorAll('input[type="checkbox"]'), function (cb) {
      if (cb.checked) { out.push(cb.value); }
    });
    return out;
  }

  function ensureLibraries() {
    if (state.libraries && state.libraries.length) { return Promise.resolve(state.libraries); }
    return getJson('Libraries').then(function (list) {
      state.libraries = Array.isArray(list) ? list : [];
      return state.libraries;
    }, function (err) {
      state.libraries = [];
      return [];
    });
  }

  function openRuleForm(rule) {
    var wrap = $('ruleFormWrap');
    wrap.hidden = false;
    var form = $('ruleForm');
    form.reset();

    clear($('rfConditionList'));
    state.editingRuleId = rule ? (rule.id || null) : null;
    $('rfFormTitle').textContent = rule ? ('编辑规则：' + (rule.name || '')) : '新建规则';

    var modeSel = $('rfMode');
    clear(modeSel);
    RULE_MODE_CHOICES.forEach(function (m) {
      var op = el('option', '', m.label);
      op.value = m.value;
      modeSel.appendChild(op);
    });

    var sortSel = $('rfSortBy');
    clear(sortSel);
    SORT_OPTIONS.forEach(function (s) {
      var op = el('option', '', s.label);
      op.value = s.value;
      sortSel.appendChild(op);
    });

    var coverSel = $('rfCoverMode');
    clear(coverSel);
    COVER_MODE_OPTIONS.forEach(function (c) {
      var op = el('option', '', c.label);
      op.value = c.value;
      coverSel.appendChild(op);
    });

    var r = rule || {
      name: '', mode: 'person', personType: 'Actor', targetName: '', titleTemplate: '{value} 合集',
      conditions: [], sortBy: 'premiereDate', sortDesc: false, coverMode: 'firstItem',
      coverUrl: '', coverProvider: 'GFriends', minItems: 1, maxCollections: 0, refresh: true, enabled: true
    };

    modeSel.value = modeChoiceOf(r);
    $('rfName').value = r.name || '';
    $('rfTargetName').value = r.targetName || '';
    $('rfTitleTemplate').value = r.titleTemplate || '';
    sortSel.value = r.sortBy || 'premiereDate';
    $('rfSortDesc').value = r.sortDesc ? 'true' : 'false';
    coverSel.value = r.coverMode || 'firstItem';
    $('rfCoverUrl').value = r.coverUrl || '';
    $('rfCoverProvider').value = r.coverProvider || 'GFriends';
    $('rfRefresh').checked = r.refresh !== false;
    $('rfEnabled').checked = r.enabled !== false;
    $('rfMinItems').value = (r.minItems === undefined || r.minItems === null) ? 1 : r.minItems;
    $('rfMaxCollections').value = (r.maxCollections === undefined || r.maxCollections === null) ? 0 : r.maxCollections;

    (r.conditions || []).forEach(function (c) { addConditionRow(c); });
    renderConditionEmpty();
    syncConditionsVisibility();
    renderLibraryPicker(r.libraryIds || []);
    ensureLibraries().then(function () { renderLibraryPicker(r.libraryIds || []); });

    wrap.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  function collectRule() {
    var modeVal = $('rfMode').value;
    var chosen = null;
    for (var i = 0; i < RULE_MODE_CHOICES.length; i++) {
      if (RULE_MODE_CHOICES[i].value === modeVal) { chosen = RULE_MODE_CHOICES[i]; }
    }
    if (!chosen) { chosen = RULE_MODE_CHOICES[0]; }

    var conditions = [];
    Array.prototype.forEach.call($('rfConditionList').children, function (row) {
      if (!row.__cond) { return; }
      var field = row.__cond.fieldSel.value;
      if (!field) { return; }
      conditions.push({
        field: field,
        op: row.__cond.opSel.value || 'eq',
        value: row.__cond.valueInput.value,
        conjunction: row.__cond.conjSel.value || 'and'
      });
    });

    var body = {
      name: $('rfName').value.trim(),
      enabled: $('rfEnabled').checked,
      libraryIds: selectedLibraryIds(),
      mode: chosen.mode,
      personType: chosen.personType || 'Actor',
      targetName: $('rfTargetName').value.trim(),
      titleTemplate: $('rfTitleTemplate').value.trim(),
      conditions: conditions,
      sortBy: $('rfSortBy').value,
      sortDesc: $('rfSortDesc').value === 'true',
      coverMode: $('rfCoverMode').value,
      coverUrl: $('rfCoverUrl').value.trim(),
      coverProvider: $('rfCoverProvider').value,
      minItems: Number($('rfMinItems').value) || 1,
      maxCollections: Number($('rfMaxCollections').value) || 0,
      refresh: $('rfRefresh').checked
    };
    if (state.editingRuleId) { body.id = state.editingRuleId; }
    return body;
  }

  function saveRule() {
    var body = collectRule();
    if (!body.name) {
      toast('请填写规则名称', 'error');
      $('rfName').focus();
      return;
    }
    if (body.mode !== 'custom' && !body.conditions.length && body.mode !== 'dateRange') {
      // 非自定义模式无需条件，直接放行
    }
    var btn = $('rfSave');
    btn.disabled = true;
    sendJson('POST', 'Rules', body).then(function (saved) {
      btn.disabled = false;
      toast(state.editingRuleId ? '规则已更新' : '规则已创建', 'success');
      $('ruleFormWrap').hidden = true;
      state.editingRuleId = null;
      loadRules();
      return saved;
    }, function (err) {
      btn.disabled = false;
      failToast(err);
    });
  }

  // ---------------- 预演 / 执行 / 删除 ----------------
  function previewRule(rule) {
    var dlg = openDialog({ title: '预演结果：' + (rule.name || ''), wide: true });
    var loading = el('div', 'cs-empty', '加载中…');
    dlg.body.appendChild(loading);
    dlg.setFooter([makeButton('关闭', '', function () { dlg.close(); })]);

    sendJson('POST', 'Rules/' + encodeURIComponent(rule.id) + '/Preview', {}).then(function (data) {
      clear(dlg.body);
      var total = (data && data.totalItems) !== undefined ? data.totalItems : 0;
      var groups = (data && data.groups) || [];

      var summary = el('div', 'cs-summary');
      summary.appendChild(el('div', '', '将创建的合集数量：' + groups.length + ' 个'));
      summary.appendChild(el('div', '', '涉及影片总数：' + total + ' 条'));
      dlg.body.appendChild(summary);

      if (!groups.length) {
        dlg.body.appendChild(el('div', 'cs-empty', '暂无数据：该规则没有命中任何分组'));
        return;
      }

      if (groups.length > 200) {
        dlg.body.appendChild(el('div', 'cs-notice',
          '共命中 ' + groups.length + ' 个组合集，超过 200 个，下方列表展示前 200 个。'));
      }

      var box = el('div', 'cs-scroll');
      groups.slice(0, 200).forEach(function (g) {
        var row = el('div', 'cs-preview-row');
        row.appendChild(el('div', 'cs-preview-name', g.name || '—'));
        row.appendChild(el('div', 'cs-preview-count', (g.count !== undefined ? g.count : 0) + ' 条'));
        box.appendChild(row);
      });
      dlg.body.appendChild(box);
    }, function (err) {
      clear(dlg.body);
      dlg.body.appendChild(el('div', 'cs-empty', '预演失败'));
      failToast(err);
    });
  }

  function applyRule(rule) {
    var dlg = openDialog({ title: '执行结果：' + (rule.name || ''), wide: true });
    var loading = el('div', 'cs-empty', '执行中…');
    dlg.body.appendChild(loading);
    dlg.setFooter([makeButton('关闭', '', function () { dlg.close(); })]);

    sendJson('POST', 'Rules/' + encodeURIComponent(rule.id) + '/Apply', {}).then(function (data) {
      clear(dlg.body);
      var d = data || {};
      var summary = el('div', 'cs-summary');
      summary.appendChild(el('div', '', '新建合集：' + (d.created !== undefined ? d.created : 0) + ' 个'));
      summary.appendChild(el('div', '', '更新合集：' + (d.updated !== undefined ? d.updated : 0) + ' 个'));
      summary.appendChild(el('div', '', '跳过：' + (d.skipped !== undefined ? d.skipped : 0) + ' 条'));
      summary.appendChild(el('div', '', '删除：' + (d.deleted !== undefined ? d.deleted : 0) + ' 个'));
      dlg.body.appendChild(summary);

      var cols = d.collections || [];
      if (cols.length) {
        var box = el('div', 'cs-scroll');
        cols.forEach(function (c) {
          var row = el('div', 'cs-preview-row');
          row.appendChild(el('div', 'cs-preview-name', c.name || '—'));
          row.appendChild(el('div', 'cs-preview-count', (c.itemCount !== undefined ? c.itemCount : 0) + ' 条'));
          box.appendChild(row);
        });
        dlg.body.appendChild(box);
      }

      var logs = d.log || [];
      dlg.body.appendChild(el('div', 'cs-label', '执行日志'));
      var logBox = el('div', 'cs-log');
      if (!logs.length) { logBox.appendChild(el('div', 'cs-log-dim cs-empty', '暂无数据')); }
      logs.forEach(function (line) { logBox.appendChild(el('div', 'cs-log-line', line)); });
      dlg.body.appendChild(logBox);

      toast('执行完成：新建 ' + (d.created || 0) + ' 个合集', 'success');
      if (state.currentTab === 'rules') { loadRules(); }
    }, function (err) {
      clear(dlg.body);
      dlg.body.appendChild(el('div', 'cs-empty', '执行失败'));
      failToast(err);
    });
  }

  function deleteRule(rule) {
    confirmDialog({
      title: '删除规则确认',
      lines: [
        '确定要删除规则「' + (rule.name || '（未命名规则）') + '」吗？',
        '删除后该规则不再参与合集生成，已生成的合集不会被自动删除。'
      ],
      okText: '确认删除',
      cancelText: '取消'
    }).then(function (ok) {
      if (!ok) { return; }
      sendJson('DELETE', 'Rules/' + encodeURIComponent(rule.id), undefined).then(function () {
        toast('规则已删除', 'success');
        loadRules();
      }, failToast);
    });
  }

  // ===========================================================
  // 合集管理
  // ===========================================================
  function renderCollections() {
    var tbody = $('colListBody');
    clear(tbody);
    if (!state.collections.length) {
      tableMessage(tbody, '暂无数据', 6);
      return;
    }
    state.collections.forEach(function (c) {
      var tr = el('tr');

      var coverCell = el('td');
      coverCell.appendChild(makeThumb(c.coverUrl));
      tr.appendChild(coverCell);

      tr.appendChild(el('td', '', c.name || '—'));
      tr.appendChild(el('td', '', c.managedBy || c.ruleId || '—'));
      tr.appendChild(el('td', '', (c.itemCount !== undefined ? c.itemCount : 0) + ' 条'));
      tr.appendChild(el('td', '', fmtDate(c.updatedAt || c.createdAt)));

      var opsCell = el('td');
      var ops = el('div', 'cs-actions');
      ops.appendChild(makeButton('改封面', 'cs-btn-sm', function () { openCoverDialog(c); }));
      ops.appendChild(makeButton('成员影片', 'cs-btn-sm', function () { openItemsDialog(c); }));
      ops.appendChild(makeButton('删除', 'cs-btn-sm cs-btn-ghost-danger', function () { deleteCollection(c); }));
      opsCell.appendChild(ops);
      tr.appendChild(opsCell);
      tbody.appendChild(tr);
    });
  }

  function loadCollections() {
    tableMessage($('colListBody'), '加载中…', 6);
    getJson('Collections').then(function (list) {
      state.collections = Array.isArray(list) ? list : [];
      renderCollections();
    }, function (err) {
      tableMessage($('colListBody'), '加载失败', 6);
      failToast(err);
    });
  }

  function deleteCollection(c) {
    confirmDialog({
      title: '删除合集确认',
      lines: [
        '确定要删除合集「' + (c.name || '') + '」吗？',
        '删除前系统会自动创建一次备份，删除后可通过备份还原。'
      ],
      okText: '确认删除'
    }).then(function (ok) {
      if (!ok) { return; }
      sendJson('POST', 'Collections/' + encodeURIComponent(c.id) + '/Delete', {}).then(function (res) {
        var backupId = (res && res.backupId) ? res.backupId : '';
        toast('合集已删除' + (backupId ? '，已自动创建备份 ' + backupId : ''), 'success');
        loadCollections();
      }, failToast);
    });
  }

  function openItemsDialog(c) {
    var dlg = openDialog({ title: '成员影片：' + (c.name || ''), wide: true });
    var loading = el('div', 'cs-empty', '加载中…');
    dlg.body.appendChild(loading);
    dlg.setFooter([makeButton('关闭', '', function () { dlg.close(); })]);
    getJson('Collections/' + encodeURIComponent(c.id) + '/Items').then(function (data) {
      clear(dlg.body);
      var items = (data && data.items) || [];
      var summary = el('div', 'cs-summary');
      summary.appendChild(el('div', '', '合集名称：' + ((data && data.name) || c.name || '—')));
      summary.appendChild(el('div', '', '影片数量：' + items.length + ' 条'));
      dlg.body.appendChild(summary);
      if (!items.length) {
        dlg.body.appendChild(el('div', 'cs-empty', '暂无数据'));
        return;
      }
      var box = el('div', 'cs-items-list');
      items.forEach(function (it) {
        var row = el('div', 'cs-item-row');
        row.appendChild(el('div', 'cs-item-name', it.name || '—'));
        row.appendChild(el('div', 'cs-item-path', it.path || ''));
        box.appendChild(row);
      });
      dlg.body.appendChild(box);
    }, function (err) {
      clear(dlg.body);
      dlg.body.appendChild(el('div', 'cs-empty', '加载失败'));
      failToast(err);
    });
  }

  // ---------------- 改封面 --------------
  function buildProviderOptions() {
    var opts = [
      { value: 'MetaTube', label: 'MetaTube' },
      { value: 'GFriends', label: 'GFriends' }
    ];
    var sources = (state.settings && state.settings.customSources) || [];
    sources.forEach(function (s) {
      if (!s || !s.id) { return; }
      opts.push({ value: 'custom:' + s.id, label: '自定义源：' + (s.name || s.id) });
    });
    return opts;
  }

  function ensureSettings() {
    if (state.settings) { return Promise.resolve(state.settings); }
    return getJson('Settings').then(function (s) {
      state.settings = s || {};
      return state.settings;
    }, function () {
      state.settings = {};
      return state.settings;
    });
  }

  function openCoverDialog(c) {
    var dlg = openDialog({ title: '修改封面：' + (c.name || ''), wide: true });
    var selectedUrl = '';

    // 当前封面
    var curBox = el('div', 'cs-current-cover');
    curBox.appendChild(makeThumb(c.coverUrl, ''));
    var curText = el('div', 'cs-current-text');
    curText.appendChild(el('div', 'cs-label', '当前封面'));
    curText.appendChild(el('div', '', c.coverUrl || '未设置封面'));
    curBox.appendChild(curText);
    dlg.body.appendChild(curBox);

    var form = el('div', 'cs-form');
    var row1 = el('div', 'cs-grid2');

    var nameInput = makeInput('cs-input', 'text', c.name || '');
    nameInput.placeholder = '填写演员 / 导演名称';
    row1.appendChild(labeledField('演员 / 导演名', nameInput));

    var providerSel = makeSelect('cs-select', buildProviderOptions(), 'MetaTube');
    row1.appendChild(labeledField('写真来源', providerSel));
    form.appendChild(row1);
    dlg.body.appendChild(form);

    dlg.body.appendChild(el('p', 'cs-hint',
      '「搜索写真」会调用封面库检索候选图；选中一张后点击「设为封面」写入该合集。'));

    var grid = el('div', 'cs-cover-grid');
    grid.appendChild(el('div', 'cs-empty', '暂无数据：请先搜索写真'));
    dlg.body.appendChild(grid);

    var setBtn = makeButton('设为封面', 'cs-btn-primary', function () {
      if (!selectedUrl) { toast('请先选择一张候选图片', 'error'); return; }
      setBtn.disabled = true;
      sendJson('POST', 'Collections/' + encodeURIComponent(c.id) + '/Cover', { url: selectedUrl }).then(function () {
        setBtn.disabled = false;
        toast('封面已更新', 'success');
        dlg.close();
        loadCollections();
      }, function (err) {
        setBtn.disabled = false;
        failToast(err);
      });
    });

    var searchBtn = makeButton('搜索写真', '', function () {
      var name = nameInput.value.trim();
      if (!name) { toast('请填写演员 / 导演名称', 'error'); return; }
      searchCovers(name, providerSel.value, grid, function (url) { selectedUrl = url; });
    });

    dlg.setFooter([makeButton('关闭', '', function () { dlg.close(); }), searchBtn, setBtn]);
  }

  function searchCovers(name, provider, grid, onSelect) {
    clear(grid);
    grid.appendChild(el('div', 'cs-empty', '加载中…'));
    var q = 'Covers/Search?name=' + encodeURIComponent(name) + '&provider=' + encodeURIComponent(provider);
    if (provider.indexOf('custom:') === 0) {
      q += '&source=' + encodeURIComponent(provider);
    }
    getJson(q).then(function (data) {
      clear(grid);
      var cands = (data && data.candidates) || [];
      var head = el('div', 'cs-cover-cap');
      head.textContent = '命中：' + ((data && data.matchedName) || name) +
        ' · 来源：' + ((data && data.source) || provider) + ' · 候选 ' + cands.length + ' 张';
      grid.appendChild(head);
      grid.setAttribute('data-matched', String((data && data.matchedName) || name));
      if (!cands.length) {
        grid.appendChild(el('div', 'cs-empty', '暂无数据：没有搜到写真'));
        return;
      }
      cands.forEach(function (cand, idx) {
        var btn = el('button', 'cs-cover-item');
        btn.type = 'button';
        var img = el('img', '');
        img.src = resolveImageUrl(cand.thumbUrl || cand.url);
        img.alt = '候选写真';
        img.loading = 'lazy';
        btn.appendChild(img);
        btn.appendChild(el('div', 'cs-cover-cap', '第 ' + (idx + 1) + ' 张 · 点选'));
        btn.addEventListener('click', function () {
          Array.prototype.forEach.call(grid.querySelectorAll('.cs-cover-item'), function (n) {
            n.classList.remove('is-selected');
          });
          btn.classList.add('is-selected');
          onSelect(cand.url || '');
        });
        grid.appendChild(btn);
      });
    }, function (err) {
      clear(grid);
      grid.appendChild(el('div', 'cs-empty', '搜索失败'));
      failToast(err);
    });
  }

  // ===========================================================
  // 改名对照
  // ===========================================================
  function totalPages() {
    var st = state.rename;
    return Math.max(1, Math.ceil((st.total || 0) / (st.pageSize || 50)));
  }

  function renderRename() {
    var tbody = $('rnListBody');
    var st = state.rename;
    clear(tbody);
    if (!st.rows.length) {
      tableMessage(tbody, '暂无数据', 6);
    } else {
      st.rows.forEach(function (r) {
        var tr = el('tr');
        tr.appendChild(el('td', 'cs-mono cs-ellipsis', r.path || '—'));
        tr.appendChild(el('td', '', r.originalTitle || '—'));

        var jfCell = el('td');
        jfCell.appendChild(el('div', '', r.jellyfinTitle || '—'));
        if (r.changed === true) {
          jfCell.appendChild(makeBadge('已改名', 'cs-badge-ok'));
        } else {
          jfCell.appendChild(makeBadge('未改名', 'cs-badge-dim'));
        }
        tr.appendChild(jfCell);

        tr.appendChild(el('td', '', r.kind || '—'));
        tr.appendChild(el('td', '', (r.year === undefined || r.year === null || r.year === '') ? '—' : String(r.year)));
        tr.appendChild(el('td', '', r.collection || '—'));
        tbody.appendChild(tr);
      });
    }

    var pages = totalPages();
    $('rnPageInfo').textContent = '第 ' + (st.page || 1) + ' / ' + pages + ' 页，共 ' + (st.total || 0) + ' 条';
    $('rnPrev').disabled = (st.page || 1) <= 1;
    $('rnNext').disabled = (st.page || 1) >= pages;
    $('rnJump').value = String(st.page || 1);
    $('rnJump').max = String(pages);
  }

  function renameQuery(extra) {
    var st = state.rename;
    var q = 'Rename?page=' + st.page + '&pageSize=' + st.pageSize +
      '&filter=' + encodeURIComponent(st.filter) +
      '&sortBy=name&sortDesc=false';
    return q + (extra || '');
  }

  function loadRename() {
    tableMessage($('rnListBody'), '加载中…', 6);
    var st = state.rename;
    getJson(renameQuery()).then(function (data) {
      var d = data || {};
      st.total = (d.total !== undefined) ? d.total : 0;
      st.page = (d.page !== undefined && d.page !== null) ? d.page : st.page;
      st.pageSize = (d.pageSize !== undefined && d.pageSize !== null) ? d.pageSize : st.pageSize;
      st.rows = Array.isArray(d.rows) ? d.rows : [];
      renderRename();
    }, function (err) {
      tableMessage($('rnListBody'), '加载失败', 6);
      failToast(err);
    });
  }

  function goRenamePage(p) {
    var pages = totalPages();
    var target = Math.min(Math.max(1, p), pages);
    state.rename.page = target;
    loadRename();
  }

  function openExportDialog() {
    var dlg = openDialog({ title: '导出改名对照 Excel' });
    var st = state.rename;

    var scopeRadio = el('div', 'cs-radio-row');
    var rPage = document.createElement('input');
    rPage.type = 'radio'; rPage.name = 'expScope'; rPage.value = 'page'; rPage.checked = true;
    var rAll = document.createElement('input');
    rAll.type = 'radio'; rAll.name = 'expScope'; rAll.value = 'all';
    var l1 = el('label', '');
    l1.appendChild(rPage); l1.appendChild(el('span', '', '当前页（第 ' + st.page + ' 页，每页 ' + st.pageSize + ' 条）'));
    var l2 = el('label', '');
    l2.appendChild(rAll); l2.appendChild(el('span', '', '全部筛选结果（共 ' + (st.total || 0) + ' 条）'));
    scopeRadio.appendChild(l1); scopeRadio.appendChild(l2);
    dlg.body.appendChild(el('div', 'cs-group-label', '导出范围'));
    dlg.body.appendChild(scopeRadio);

    var destRadio = el('div', 'cs-radio-row');
    var dClient = document.createElement('input');
    dClient.type = 'radio'; dClient.name = 'expDest'; dClient.value = 'client'; dClient.checked = true;
    var dServer = document.createElement('input');
    dServer.type = 'radio'; dServer.name = 'expDest'; dServer.value = 'server';
    var dl1 = el('label', '');
    dl1.appendChild(dClient); dl1.appendChild(el('span', '', '下载到电脑'));
    var dl2 = el('label', '');
    dl2.appendChild(dServer); dl2.appendChild(el('span', '', '保存到服务器'));
    destRadio.appendChild(dl1); destRadio.appendChild(dl2);
    dlg.body.appendChild(el('div', 'cs-group-label', '保存位置'));
    dlg.body.appendChild(destRadio);

    var resultBox = el('div', 'cs-hint', '');
    dlg.body.appendChild(resultBox);

    function chosenValue(name) {
      var nodes = dlg.body.querySelectorAll('input[name="' + name + '"]');
      for (var i = 0; i < nodes.length; i++) {
        if (nodes[i].checked) { return nodes[i].value; }
      }
      return '';
    }

    var doExport = makeButton('开始导出', 'cs-btn-primary', function () {
      var scope = chosenValue('expScope');
      var dest = chosenValue('expDest');
      var q = 'Rename/Export?destination=' + dest + '&scope=' + scope +
        '&page=' + st.page + '&pageSize=' + st.pageSize +
        '&filter=' + encodeURIComponent(st.filter) + '&sortBy=name&sortDesc=false';

      if (dest === 'client') {
        window.location = apiUrl(q);
        toast('已开始下载导出文件', 'success');
        dlg.close();
        return;
      }

      doExport.disabled = true;
      resultBox.textContent = '正在导出到服务器…';
      getJson(q).then(function (data) {
        doExport.disabled = false;
        var path = (data && data.path) ? data.path : '（后端未返回路径）';
        resultBox.textContent = '已保存到服务器：' + path;
        resultBox.className = 'cs-hint';
        toast('已保存到服务器：' + path, 'success');
      }, function (err) {
        doExport.disabled = false;
        resultBox.textContent = '导出失败';
        failToast(err);
      });
    });

    dlg.setFooter([makeButton('取消', '', function () { dlg.close(); }), doExport]);
  }

  // ===========================================================
  // 备份与还原
  // ===========================================================
  function renderBackups() {
    var tbody = $('bkListBody');
    clear(tbody);
    if (!state.backups.length) {
      tableMessage(tbody, '暂无数据', 9);
      return;
    }
    state.backups.forEach(function (b) {
      var tr = el('tr');
      tr.appendChild(el('td', '', b.name || '—'));
      tr.appendChild(el('td', '', fmtDate(b.createdAt)));
      tr.appendChild(el('td', '', BACKUP_KIND_LABELS[b.kind] || b.kind || '—'));
      tr.appendChild(el('td', 'cs-ellipsis', b.note || '—'));
      tr.appendChild(el('td', '', (b.rules !== undefined ? b.rules : 0)));
      tr.appendChild(el('td', '', (b.collections !== undefined ? b.collections : 0)));
      tr.appendChild(el('td', '', fmtKB(b.sizeBytes)));

      var opsCell = el('td');
      var ops = el('div', 'cs-actions');
      ops.appendChild(makeButton('还原', 'cs-btn-sm', function () { restoreBackup(b); }));
      ops.appendChild(makeButton('删除', 'cs-btn-sm cs-btn-ghost-danger', function () { deleteBackup(b); }));
      opsCell.appendChild(ops);
      tr.appendChild(opsCell);
      tbody.appendChild(tr);
    });
  }

  function loadBackups() {
    tableMessage($('bkListBody'), '加载中…', 8);
    getJson('Backups').then(function (list) {
      state.backups = Array.isArray(list) ? list : [];
      renderBackups();
    }, function (err) {
      tableMessage($('bkListBody'), '加载失败', 8);
      failToast(err);
    });
  }

  function createBackup() {
    var note = $('bkNoteInput').value.trim();
    var btn = $('bkCreateBtn');
    btn.disabled = true;
    sendJson('POST', 'Backups', { note: note }).then(function () {
      btn.disabled = false;
      $('bkNoteInput').value = '';
      toast('备份已创建', 'success');
      loadBackups();
    }, function (err) {
      btn.disabled = false;
      failToast(err);
    });
  }

  function deleteBackup(b) {
    confirmDialog({
      title: '删除备份确认',
      lines: [
        '确定要删除备份「' + (b.name || '') + '」吗？',
        '删除后该备份不可恢复。'
      ],
      okText: '确认删除'
    }).then(function (ok) {
      if (!ok) { return; }
      sendJson('DELETE', 'Backups/' + encodeURIComponent(b.id), undefined).then(function () {
        toast('备份已删除', 'success');
        loadBackups();
      }, failToast);
    });
  }

  // 将 'yyyy-MM-dd HH:mm:ss' 解析为 Date；失败返回 null
  function parseDateValue(v) {
    if (!v) { return null; }
    var m = String(v).match(/(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2})(?::(\d{2}))?/);
    if (!m) { return null; }
    var d = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]),
      Number(m[4]), Number(m[5]), Number(m[6] || 0));
    return isNaN(d.getTime()) ? null : d;
  }

  function toInputValue(d) {
    if (!d) { return ''; }
    return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()) +
      'T' + pad2(d.getHours()) + ':' + pad2(d.getMinutes()) + ':' + pad2(d.getSeconds());
  }

  function backupsInRange(from, to) {
    return state.backups.filter(function (b) {
      var d = parseDateValue(b.createdAt);
      if (!d) { return false; }
      if (from && d.getTime() < from.getTime()) { return false; }
      if (to && d.getTime() > to.getTime()) { return false; }
      return true;
    });
  }

  function restoreBackup(b) {
    confirmDialog({
      title: '还原数据确认',
      lines: [
        '确定要还原备份「' + (b.name || '') + '」吗？',
        '当前规则与合集数据将被该备份的内容覆盖，操作不可撤销。'
      ],
      okText: '确认还原'
    }).then(function (ok) {
      if (!ok) { return; }
      doRestore(b.id);
    });
  }

  function doRestore(id) {
    sendJson('POST', 'Backups/Restore', { id: id }).then(function (res) {
      var rr = (res && res.restoredRules !== undefined) ? res.restoredRules : 0;
      var rc = (res && res.restoredCollections !== undefined) ? res.restoredCollections : 0;
      toast('还原完成：规则 ' + rr + ' 条，合集 ' + rc + ' 个', 'success');
      loadBackups();
    }, failToast);
  }

  function openRestoreDialog() {
    if (!state.backups.length) {
      toast('暂无数据：没有可用备份，请先创建备份', 'error');
      return;
    }
    var dlg = openDialog({ title: '还原数据', wide: true });
    dlg.body.appendChild(el('p', 'cs-hint',
      '先选择备份时间范围，再从中挑选一个备份，确认摘要后执行还原。'));

    var now = new Date();
    var earliest = now;
    state.backups.forEach(function (b) {
      var d = parseDateValue(b.createdAt);
      if (d && d.getTime() < earliest.getTime()) { earliest = d; }
    });

    var grid = el('div', 'cs-grid2');
    var fromInput = makeInput('cs-input', 'datetime-local', toInputValue(earliest));
    fromInput.step = '1';
    var toInput = makeInput('cs-input', 'datetime-local', toInputValue(now));
    toInput.step = '1';
    grid.appendChild(labeledField('开始日期时间', fromInput));
    grid.appendChild(labeledField('结束日期时间', toInput));
    dlg.body.appendChild(el('div', 'cs-group-label', '备份时间范围'));
    dlg.body.appendChild(grid);

    var hitSelect = makeSelect('cs-select', [], '', '请选择命中的备份');
    dlg.body.appendChild(labeledField('命中备份', hitSelect));

    var summary = el('div', 'cs-summary');
    summary.appendChild(el('div', 'cs-hint', '尚未选择备份'));
    dlg.body.appendChild(el('div', 'cs-group-label', '备份摘要'));
    dlg.body.appendChild(summary);

    var selected = null;

    function refreshHits() {
      var from = fromInput.value ? new Date(fromInput.value) : null;
      var to = toInput.value ? new Date(toInput.value) : null;
      if (from && isNaN(from.getTime())) { from = null; }
      if (to && isNaN(to.getTime())) { to = null; }
      var hits = backupsInRange(from, to);
      clear(hitSelect);
      var ph = el('option', '', hits.length ? '请选择命中的备份（共 ' + hits.length + ' 个）' : '该时间范围内暂无备份');
      ph.value = '';
      hitSelect.appendChild(ph);
      hits.forEach(function (b) {
        var op = el('option', '', b.name + ' · ' + fmtDate(b.createdAt));
        op.value = b.id;
        hitSelect.appendChild(op);
      });
      selected = null;
      renderSummary();
    }

    function renderSummary() {
      clear(summary);
      if (!selected) {
        summary.appendChild(el('div', 'cs-hint', '尚未选择备份'));
        return;
      }
      summary.appendChild(el('div', '', '备份名称：' + (selected.name || '—')));
      summary.appendChild(el('div', '', '创建时间：' + fmtDate(selected.createdAt)));
      summary.appendChild(el('div', '', '来源：' + (BACKUP_KIND_LABELS[selected.kind] || selected.kind || '—')));
      summary.appendChild(el('div', '', '备注：' + (selected.note || '（无）')));
      summary.appendChild(el('div', '', '规则数：' + (selected.rules || 0) + ' 条'));
      summary.appendChild(el('div', '', '合集数：' + (selected.collections || 0) + ' 个'));
      summary.appendChild(el('div', '', '大小：' + fmtKB(selected.sizeBytes) + ' KB'));
    }

    fromInput.addEventListener('change', refreshHits);
    toInput.addEventListener('change', refreshHits);
    hitSelect.addEventListener('change', function () {
      var v = hitSelect.value;
      selected = null;
      state.backups.forEach(function (b) { if (String(b.id) === v) { selected = b; } });
      renderSummary();
    });
    refreshHits();

    var confirmBtn = makeButton('确认还原', 'cs-btn-danger', function () {
      if (!selected) { toast('请先在命中备份下拉中选择一个备份', 'error'); return; }
      confirmDialog({
        title: '还原数据确认',
        lines: [
          '确定要用备份「' + (selected.name || '') + '」还原数据吗？',
          '当前规则与合集数据将被该备份的内容覆盖，操作不可撤销。'
        ],
        okText: '确认还原'
      }).then(function (ok) {
        if (!ok) { return; }
        dlg.close();
        doRestore(selected.id);
      });
    });

    dlg.setFooter([makeButton('取消', '', function () { dlg.close(); }), confirmBtn]);
  }

  // ===========================================================
  // 设置
  // ===========================================================
  function renderSourceCard(src, index) {
    var item = src || { id: '', name: '', urlTemplate: '', imagesPath: 'data.images[*]', imageBaseUrl: '', headers: [] };
    var card = el('div', 'cs-source');

    var head = el('div', 'cs-source-head');
    head.appendChild(el('div', 'cs-source-title', '刮削源 ' + (index + 1)));
    var right = el('div', 'cs-actions');
    var delBtn = makeButton('删除此源', 'cs-btn-sm cs-btn-ghost-danger', function () {
      card.parentNode.removeChild(card);
      renderSourcesEmpty();
    });
    right.appendChild(delBtn);
    head.appendChild(right);
    card.appendChild(head);

    var g1 = el('div', 'cs-grid2');
    var nameInput = makeInput('cs-input', 'text', item.name || '');
    nameInput.placeholder = '例如：我的刮削源';
    g1.appendChild(labeledField('名称', nameInput));
    var urlInput = makeInput('cs-input', 'text', item.urlTemplate || '');
    urlInput.placeholder = 'https://x/api?q={name}';
    g1.appendChild(labeledField('URL 模板（支持 {name}）', urlInput));
    card.appendChild(g1);

    var g2 = el('div', 'cs-grid2');
    var imagesInput = makeInput('cs-input', 'text', item.imagesPath || 'data.images[*]');
    imagesInput.placeholder = 'data.images[*]';
    g2.appendChild(labeledField('图片字段路径', imagesInput));
    var baseInput = makeInput('cs-input', 'text', item.imageBaseUrl || '');
    baseInput.placeholder = '图片基地址，可留空';
    g2.appendChild(labeledField('图片基地址', baseInput));
    card.appendChild(g2);

    var headersBox = el('div', 'cs-headers');
    card.appendChild(el('div', 'cs-group-label', '请求头（可选）'));
    card.appendChild(headersBox);

    function addHeaderRow(h) {
      var row = el('div', 'cs-header-row');
      var hName = makeInput('cs-input', 'text', (h && h.name) || '');
      hName.placeholder = 'Name，例如 Authorization';
      var hValue = makeInput('cs-input', 'text', (h && h.value) || '');
      hValue.placeholder = 'Value，例如 Bearer xxx';
      var rm = makeButton('删除', 'cs-btn-sm cs-btn-ghost-danger', function () {
        headersBox.removeChild(row);
      });
      row.appendChild(hName);
      row.appendChild(hValue);
      row.appendChild(rm);
      headersBox.appendChild(row);
    }

    (item.headers || []).forEach(addHeaderRow);
    card.appendChild(makeButton('+ 添加请求头', 'cs-btn-sm', function () { addHeaderRow({}); }));

    card.__src = {
      nameInput: nameInput, urlInput: urlInput, imagesInput: imagesInput,
      baseInput: baseInput, headersBox: headersBox, id: item.id || ''
    };
    return card;
  }

  function renderSourcesEmpty() {
    var box = $('stSources');
    if (box.querySelector('.cs-source')) { return; }
    var empty = box.querySelector('.cs-sources-empty');
    if (empty) { return; }
    box.appendChild(el('div', 'cs-empty cs-sources-empty', '暂无数据：还没有配置自定义刮削源'));
  }

  function renderSources(list) {
    var box = $('stSources');
    clear(box);
    var items = list || [];
    if (!items.length) {
      box.appendChild(el('div', 'cs-empty cs-sources-empty', '暂无数据：还没有配置自定义刮削源'));
      return;
    }
    items.forEach(function (s, i) {
      box.appendChild(renderSourceCard(s, i));
    });
  }

  function collectSources() {
    var out = [];
    var cards = $('stSources').querySelectorAll('.cs-source');
    Array.prototype.forEach.call(cards, function (card, idx) {
      var meta = card.__src;
      if (!meta) { return; }
      var headers = [];
      Array.prototype.forEach.call(meta.headersBox.querySelectorAll('.cs-header-row'), function (row) {
        var inputs = row.querySelectorAll('.cs-input');
        if (inputs.length < 2) { return; }
        var n = inputs[0].value.trim();
        var v = inputs[1].value;
        if (!n) { return; }
        headers.push({ name: n, value: v });
      });
      var name = meta.nameInput.value.trim();
      var url = meta.urlInput.value.trim();
      if (!name && !url) { return; }
      out.push({
        id: meta.id || ('src' + (idx + 1)),
        name: name,
        urlTemplate: url,
        imagesPath: meta.imagesInput.value.trim() || 'data.images[*]',
        imageBaseUrl: meta.baseInput.value.trim(),
        headers: headers
      });
    });
    return out;
  }

  function applySettings(s) {
    var d = s || {};
    $('stBaseUrl').value = d.metaTubeBaseUrl || '';
    $('stToken').value = d.metaTubeToken || '';
    var prov = $('stProvider');
    var target = d.metaTubeActorProvider || 'GFriends';
    var has = false;
    Array.prototype.forEach.call(prov.options, function (o) { if (o.value === target) { has = true; } });
    prov.value = has ? target : 'GFriends';
    $('stTimeout').value = (d.metaTubeTimeoutSeconds === undefined || d.metaTubeTimeoutSeconds === null)
      ? 20 : d.metaTubeTimeoutSeconds;
    var ps = $('stPageSize');
    var pv = String(d.defaultPageSize || 50);
    var found = false;
    Array.prototype.forEach.call(ps.options, function (o) { if (o.value === pv) { found = true; } });
    ps.value = found ? pv : '50';
    $('stAutoBackup').checked = d.autoBackupBeforeApply !== false;
    renderSources(d.customSources || []);
  }

  function loadSettings() {
    $('stSources').setAttribute('data-loading', '1');
    clear($('stSources'));
    $('stSources').appendChild(el('div', 'cs-empty', '加载中…'));
    getJson('Settings').then(function (s) {
      state.settings = s || {};
      applySettings(state.settings);
    }, function (err) {
      clear($('stSources'));
      $('stSources').appendChild(el('div', 'cs-empty', '加载失败'));
      failToast(err);
    });
  }

  function saveSettings() {
    var body = {
      metaTubeBaseUrl: $('stBaseUrl').value.trim(),
      metaTubeToken: $('stToken').value.trim(),
      metaTubeActorProvider: $('stProvider').value,
      metaTubeTimeoutSeconds: Number($('stTimeout').value) || 20,
      customSources: collectSources(),
      defaultPageSize: Number($('stPageSize').value) || 50,
      autoBackupBeforeApply: $('stAutoBackup').checked
    };
    var btn = $('stSaveBtn');
    btn.disabled = true;
    sendJson('POST', 'Settings', body).then(function (res) {
      btn.disabled = false;
      state.settings = (res && res.settings) ? res.settings : body;
      toast('设置已保存', 'success');
    }, function (err) {
      btn.disabled = false;
      failToast(err);
    });
  }

  function testConnection() {
    var box = $('stTestResult');
    box.className = 'cs-test-result';
    box.textContent = '正在测试连接…';
    getJson('Status').then(function (s) {
      var d = s || {};
      if (d.metaTubeReachable === true) {
        box.className = 'cs-test-result is-ok';
        box.textContent = '连接成功：MetaTube 可连通';
        toast('连接成功：MetaTube 可连通', 'success');
      } else if (d.metaTubeConfigured === false) {
        box.className = 'cs-test-result is-bad';
        box.textContent = '连接失败：MetaTube 未配置';
        toast('连接失败：MetaTube 未配置', 'error');
      } else {
        box.className = 'cs-test-result is-bad';
        box.textContent = '连接失败：MetaTube 不可连通';
        toast('连接失败：MetaTube 不可连通', 'error');
      }
    }, function (err) {
      box.className = 'cs-test-result is-bad';
      box.textContent = '连接失败';
      failToast(err);
    });
  }

  // ===========================================================
  // 事件绑定
  // ===========================================================
  function bindEvents() {
    // 宿主（Jellyfin 控制台）用 jQuery/jQuery-mobile 接管点击，document 级监听可能被中途 stopPropagation 吞掉；
    // 因此优先在按钮自身身上挂监听（目标阶段的监听先于任何冒泡处理），并保留一次 document 捕获委托兜底。
    var navItems = document.querySelectorAll('.cs-nav-item');
    Array.prototype.forEach.call(navItems, function (btn) {
      var tab = btn.getAttribute('data-tab');
      var go = function (ev) {
        if (ev) { ev.preventDefault(); }
        rootScope = pageRootOf(btn) || rootScope;
        switchTab(tab);
      };
      btn.addEventListener('pointerdown', go);
      btn.addEventListener('click', go);
    });

    var delegated = function (ev) {
      var node = ev.target;
      while (node && node !== document) {
        if (node.getAttribute && node.classList && node.classList.contains('cs-nav-item')) {
          rootScope = pageRootOf(node) || rootScope;
          switchTab(node.getAttribute('data-tab'));
          return;
        }
        node = node.parentNode;
      }
    };
    document.addEventListener('pointerdown', delegated, true);
    document.addEventListener('click', delegated, true);

    $('ovScanBtn').addEventListener('click', function () {
      var btn = $('ovScanBtn');
      btn.disabled = true;
      sendJson('POST', 'Tasks/scan/Run', {}).then(function () {
        btn.disabled = false;
        toast('扫描任务已启动', 'success');
        loadTasks();
      }, function (err) {
        btn.disabled = false;
        failToast(err);
      });
    });
    $('ovTasksRefresh').addEventListener('click', loadTasks);
    $('ovLogTaskSelect').addEventListener('change', loadTaskLog);
    $('ovLogRefresh').addEventListener('click', loadTaskLog);

    $('ruleNewBtn').addEventListener('click', function () { openRuleForm(null); });
    $('ruleRefreshBtn').addEventListener('click', loadRules);
    $('rfAddCondition').addEventListener('click', function () { addConditionRow(null); });
    $('rfMode').addEventListener('change', syncConditionsVisibility);
    $('ruleForm').addEventListener('submit', function (e) { e.preventDefault(); saveRule(); });
    $('rfCancel').addEventListener('click', function () {
      $('ruleFormWrap').hidden = true;
      state.editingRuleId = null;
    });
    $('rfCancelTop').addEventListener('click', function () {
      $('ruleFormWrap').hidden = true;
      state.editingRuleId = null;
    });

    $('colRefreshBtn').addEventListener('click', loadCollections);

    var filterTimer = null;
    $('rnFilter').addEventListener('input', function () {
      if (filterTimer) { clearTimeout(filterTimer); }
      filterTimer = setTimeout(function () {
        state.rename.filter = $('rnFilter').value.trim();
        state.rename.page = 1;
        loadRename();
      }, 350);
    });
    $('rnPageSize').addEventListener('change', function () {
      state.rename.pageSize = Number($('rnPageSize').value) || 50;
      state.rename.page = 1;
      loadRename();
    });
    $('rnPrev').addEventListener('click', function () { goRenamePage(state.rename.page - 1); });
    $('rnNext').addEventListener('click', function () { goRenamePage(state.rename.page + 1); });
    $('rnJumpBtn').addEventListener('click', function () { goRenamePage(Number($('rnJump').value) || 1); });
    $('rnJump').addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { e.preventDefault(); goRenamePage(Number($('rnJump').value) || 1); }
    });
    $('rnRefreshBtn').addEventListener('click', loadRename);
    $('rnExportBtn').addEventListener('click', openExportDialog);

    $('bkCreateBtn').addEventListener('click', createBackup);
    $('bkRestoreBtn').addEventListener('click', openRestoreDialog);
    $('bkRefreshBtn').addEventListener('click', loadBackups);

    $('stSaveBtn').addEventListener('click', saveSettings);
    $('stTestBtn').addEventListener('click', testConnection);
    $('stAddSource').addEventListener('click', function () {
      var box = $('stSources');
      var empty = box.querySelector('.cs-sources-empty');
      if (empty) { box.removeChild(empty); }
      box.appendChild(renderSourceCard({}, box.querySelectorAll('.cs-source').length));
    });
    $('settingsForm').addEventListener('submit', function (e) { e.preventDefault(); saveSettings(); });
  }

  // ===========================================================
  // 主题跟随 Jellyfin
  // ===========================================================
  function applyTheme() {
    var dark = null;
    try {
      if (window.ActiveThemeMode !== undefined && window.ActiveThemeMode !== null) {
        dark = String(window.ActiveThemeMode).toLowerCase().indexOf('dark') >= 0;
      }
    } catch (e) { dark = null; }
    if (dark === null) {
      try {
        dark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
      } catch (e) { dark = true; }
    }
    document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
  }

  // ===========================================================
  // 启动
  // ===========================================================
  // Jellyfin 控制台（MUI）会在插件页上方留一层隐形遮罩（MuiModal-backdrop），
  // 点击会被遮罩吃掉、根本到不了我们的按钮。这里在 window 捕获阶段按坐标做一次命中测试，
  // 命中本页导航按钮时手动切换标签页，保证控制台里也能正常操作。
  function navItemAt(x, y) {
    var stack;
    try { stack = document.elementsFromPoint(x, y); } catch (e) { return null; }
    for (var i = 0; i < stack.length; i++) {
      var el = stack[i];
      if (el && el.classList && el.classList.contains('cs-nav-item')) { return el; }
    }
    return null;
  }

  // 找出当前真正显示在屏幕上的那份页面（Jellyfin 控制台会缓存多份，只显示其中一份）
  function visibleRoot() {
    var roots = document.querySelectorAll('.cs-page');
    var best = null;
    for (var i = 0; i < roots.length; i++) {
      var r = roots[i];
      if (!r.querySelector || !r.querySelector('.cs-nav-item')) { continue; }
      var hiddenByHost = false;
      var n = r;
      while (n && n !== document.body) {
        if (n.classList && n.classList.contains('hide')) { hiddenByHost = true; break; }
        n = n.parentNode;
      }
      if (hiddenByHost) { continue; }
      var box = r.getBoundingClientRect();
      if (box.width <= 0 || box.height <= 0) { continue; }
      if (!best || box.left < best.box.left) { best = { root: r, box: box }; }
    }
    return best ? best.root : null;
  }

  function hitTestNav(ev) {
    if (ev.target && rootScope && rootScope.contains(ev.target)) { return; }
    var item = navItemAt(ev.clientX, ev.clientY);
    if (!item) { return; }
    ev.preventDefault();
    ev.stopPropagation();
    navTo(item.getAttribute('data-tab'), item);
  }

  window.addEventListener('pointerdown', hitTestNav, true);
  window.addEventListener('click', hitTestNav, true);

  // Jellyfin 控制台会注入多份插件页（只显示其中一份），无法可靠判断哪份可见，
  // 因此把切换动作同步应用到所有页面实例，保证可见的那份一定跟着切换。
  function allRoots() {
    var roots = [];
    var items = document.querySelectorAll('.cs-nav-item');
    if (!items.length) { return rootScope ? [rootScope] : []; }
    for (var i = 0; i < items.length; i++) {
      var r = pageRootOf(items[i]);
      if (r && roots.indexOf(r) < 0) { roots.push(r); }
    }
    if (!roots.length && rootScope) { roots.push(rootScope); }
    return roots;
  }

  function navTo(tab, el) {
    // 宿主每次切换视图都会重建插件页 DOM，因此点击时按被点元素现场定位根节点
    var fresh = el ? pageRootOf(el) : null;
    if (fresh && fresh.isConnected) { rootScope = fresh; }
    var scope = connectedRoot();
    if (!scope) { scope = document.querySelector('.cs-page, .cs-app'); if (scope) { rootScope = scope; } }
    if (!scope) { return false; }
    var roots = allRoots();
    for (var i = 0; i < roots.length; i++) {
      var r = roots[i];
      if (r !== scope && typeof r.__csSwitch === 'function') { r.__csSwitch(tab); }
    }
    switchTab(tab, scope);
    return false;
  }

  function init() {
    // 页面根节点是 .cs-page（旧版用 .cs-app，这里两种都认）
    var claim = function () {
      var roots = document.querySelectorAll('.cs-page, .cs-app');
      for (var i = 0; i < roots.length; i++) {
        if (!roots[i].getAttribute('data-cs-root')) { return roots[i]; }
      }
      return roots.length ? roots[roots.length - 1] : null;
    };
    rootScope = (document.currentScript && pageRootOf(document.currentScript)) || claim();

    if (rootScope) { rootScope.setAttribute('data-cs-root', '1'); }
    // 即使没能认领到根节点，也要继续绑定，否则整页都点不动
    var navHost = rootScope || document;
    Array.prototype.forEach.call(navHost.querySelectorAll('.cs-nav-item'), function (btn) {
      var tab = btn.getAttribute('data-tab');
      var go = function (ev) {
        if (ev) { ev.preventDefault(); }
        navTo(tab, btn);
      };
      btn.addEventListener('pointerdown', go);
      btn.addEventListener('click', go);
    });

    if (rootScope) {
      var self = rootScope;
      rootScope.__csSwitch = function (tab) { rootScope = self; switchTab(tab); };
    }

    applyTheme();
    bindEvents();
    var lastTab = null;
    try { lastTab = document.documentElement.getAttribute('data-cs-tab'); } catch (e) { }
    switchTab(TAB_META[lastTab] ? lastTab : 'overview');

  }

  // 页面上的按钮用内联 onclick="if(window.CS){window.CS.nav(...)}" 调用这里。
  // 必须在 init 之前就挂好：否则 init 中任一处异常都会让 window.CS 不存在，
  // 整页按钮全部静默失效（这正是「点不了」的成因）。
  window.CS = {
    nav: function (tab, el) {
      try { return navTo(tab, el); }
      catch (e) { try { console.error('[Collection Studio] 切换标签失败：', e); } catch (ignore) { } return false; }
    }
  };

  // 任何初始化异常都不应让整页失去响应：捕获后仍保留已绑定的部分，并把错误暴露出来
  try {
    init();
  } catch (e) {
    window.__csInitError = e;
    try { console.error('[Collection Studio] 初始化失败：', e); } catch (ignore) { }
    // 兜底：至少让标签条能点
    try {
      var fallbackRoot = document.querySelector('.cs-page, .cs-app') || document.body;
      var items = document.querySelectorAll('.cs-nav-item');
      Array.prototype.forEach.call(items, function (btn) {
        btn.addEventListener('click', function (ev) {
          if (ev) { ev.preventDefault(); }
          try {
            rootScope = pageRootOf(btn) || fallbackRoot;
          } catch (ignore) { }
          try { switchTab(btn.getAttribute('data-tab'), fallbackRoot); } catch (ignore) { }
        });
      });
    } catch (ignore) { }
  }

})();
