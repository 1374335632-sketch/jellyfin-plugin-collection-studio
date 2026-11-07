using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CollectionStudio.Core;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CollectionStudio.Api
{
    [ApiController]
    [Authorize]
    [Route("CollectionStudio")]
    [Produces("application/json")]
    public class CollectionStudioController : ControllerBase
    {
        private readonly ILibraryManager _library;
        private readonly ICollectionManager _collections;
        private readonly IServerApplicationHost _host;
        private readonly ILogger<CollectionStudioController> _logger;
        private readonly MediaBrowser.Model.Tasks.ITaskManager _scheduler;

        public CollectionStudioController(ILibraryManager library, ICollectionManager collections,
            IServerApplicationHost host, MediaBrowser.Model.Tasks.ITaskManager scheduler,
            ILogger<CollectionStudioController> logger)
        {
            _library = library;
            _collections = collections;
            _host = host;
            _scheduler = scheduler;
            _logger = logger;
        }

        private Store Open() => Plugin.OpenStore();

        private static ActionResult Error(int code, string message) =>
            new ObjectResult(new { error = message }) { StatusCode = code };

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = false
        };

        /// <summary>一键添加写真刮削源：只给一个网址，自动推断出可用的源配置。</summary>
        [HttpPost("Covers/AddSourceByUrl")]
        public ActionResult AddSourceByUrl([FromBody] AddSourceRequest body)
        {
            if (body == null || string.IsNullOrWhiteSpace(body.Url)) return Error(400, "请填写刮削源网址。");
            var raw = body.Url.Trim();

            string name = body.Name;
            string urlTemplate = raw;
            string imagesPath = "images[*]";
            string imageBase = string.Empty;
            var notes = new List<string>();

            try
            {
                var uri = new Uri(raw);
                // 若给的是 index.json 之类的清单地址，尝试从路径推断源名
                if (string.IsNullOrWhiteSpace(name))
                {
                    var seg = uri.AbsolutePath.Trim('/').Split('/');
                    name = seg.Length > 0 && seg[seg.Length - 1].Contains('.')
                        ? (seg.Length > 1 ? seg[seg.Length - 2] : "自定义源")
                        : seg[seg.Length - 1];
                    if (string.IsNullOrWhiteSpace(name)) name = uri.Host;
                }

                // 常见写真源都是「GET {base}/{id}.json」形态：若给的是目录或域名，自动补模板
                if (!urlTemplate.Contains("{id}") && !urlTemplate.Contains("{0}"))
                {
                    if (uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal) || string.IsNullOrEmpty(uri.AbsolutePath.Trim('/')))
                    {
                        urlTemplate = raw.TrimEnd('/') + "/{id}.json";
                        notes.Add("未检测到 {id} 占位符，已按「目录/{id}.json」补全，可稍后微调。");
                    }
                }

                // 补全 imageBaseUrl：默认取响应的 host
                imageBase = uri.GetLeftPart(UriPartial.Authority);
            }
            catch (Exception ex)
            {
                return Error(400, "网址格式不正确：" + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(name)) name = "自定义源";

            using var store = Open();
            var settings = store.GetSettings();
            settings.CustomSources ??= new List<CustomSource>();

            var id = Guid.NewGuid().ToString("N");
            var src = new CustomSource
            {
                Id = id,
                Name = name,
                UrlTemplate = urlTemplate,
                ImagesPath = imagesPath,
                ImageBaseUrl = imageBase,
                Headers = new List<HeaderPair>()
            };
            settings.CustomSources.Add(src);
            // 顺便把 MetaTube 之外的第一个源设为当前写真来源
            settings.MetaTubeActorProvider = name;
            store.SaveSettings(settings);

            return Ok(new
            {
                ok = true,
                source = new { id, name, urlTemplate, imagesPath, imageBaseUrl = imageBase },
                notes
            });
        }

        public sealed class AddSourceRequest
        {
            public string Url { get; set; }
            public string Name { get; set; }
        }

        // ---------------------------------------------------------------- 源名称替换 + 刮削

        /// <summary>规则试算：给一个源名称，返回每条规则的替换结果，便于在界面上验证正则。</summary>
        [HttpPost("Rename/Test")]
        public ActionResult TestRename([FromBody] System.Text.Json.JsonElement payload)
        {
            try
            {
                var source = string.Empty;
                if (TryProp(payload, "source", out var sv) && sv.ValueKind == System.Text.Json.JsonValueKind.String) source = sv.GetString() ?? string.Empty;

                var rules = ReadRules(payload, "rules", "Rules");
                using var store = Open();
                if (rules.Count == 0) rules = store.GetSettings().RenameRules ?? new List<RenameRule>();

                var steps = new List<object>();
                var current = source;
                foreach (var rule in rules)
                {
                    if (rule == null) continue;
                    var (pattern, replacement, isRegex) = RenameEngine.ParseRuleText(rule.Pattern);
                    var next = RenameEngine.ReplaceOne(current, pattern, replacement, isRegex, out var hit);
                    steps.Add(new { rule = rule.Pattern, pattern, replacement, isRegex, matched = hit, before = current, after = next });
                    if (hit) current = next;
                }

                return Ok(new { source, result = current, changed = !string.Equals(current, source, StringComparison.Ordinal), steps });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "试算失败");
                return Error(500, "试算失败：" + ex.Message);
            }
        }

        /// <summary>对媒体库执行「先重命名、再刮削」。dryRun=true 只预演。</summary>
        /// <remarks>
        /// 直接用 JsonElement 接收，再自行宽松解析：避免因前端字段大小写或结构差异
        /// 在模型绑定阶段就返回 400（那种错误没有可读信息，很难定位）。
        /// </remarks>
        [HttpPost("Rename/Run")]
        public async Task<ActionResult> RunRename([FromBody] System.Text.Json.JsonElement payload)
        {
            try
            {
                var dryRun = ReadBool(payload, "dryRun", "DryRun");
                var modeText = ReadString(payload, "mode", "Mode") ?? "both";
                var mode = modeText.Equals("rename", StringComparison.OrdinalIgnoreCase) ? ScrapePipeline.RunMode.RenameOnly
                         : modeText.Equals("scrape", StringComparison.OrdinalIgnoreCase) ? ScrapePipeline.RunMode.ScrapeOnly
                         : ScrapePipeline.RunMode.RenameThenScrape;
                var rules = ReadRules(payload, "rules", "Rules");
                var libraryIds = ReadStringList(payload, "libraryIds", "LibraryIds");

                using var store = Open();
                var settings = store.GetSettings();
                if (rules.Count > 0) settings.RenameRules = rules;
                // 前端未传媒体库时，沿用设置里保存的选择
                if (libraryIds.Count == 0 && settings.RenameLibraryIds != null) libraryIds = settings.RenameLibraryIds;

                // 执行时的三个开关以请求体为准：否则用户勾选后没点「保存」，
                // 后端仍读数据库里的旧值，导致「同时重命名磁盘文件」等选项看起来不起作用。
                if (TryProp(payload, "renamePhysicalFiles", out var phy))
                {
                    settings.RenamePhysicalFiles = phy.ValueKind == System.Text.Json.JsonValueKind.True;
                }
                if (TryProp(payload, "renameWriteName", out var wr))
                {
                    settings.RenameWriteName = wr.ValueKind == System.Text.Json.JsonValueKind.True;
                }
                if (TryProp(payload, "renameBeforeScrape", out var en))
                {
                    settings.RenameBeforeScrape = en.ValueKind == System.Text.Json.JsonValueKind.True;
                }

                var providerManager = (MediaBrowser.Controller.Providers.IProviderManager)HttpContext.RequestServices
                    .GetService(typeof(MediaBrowser.Controller.Providers.IProviderManager));
                // MetadataRefreshOptions 需要 IDirectoryService，必须从容器里取，不能 new
                var directoryService = (MediaBrowser.Controller.Providers.IDirectoryService)HttpContext.RequestServices
                    .GetService(typeof(MediaBrowser.Controller.Providers.IDirectoryService));
                var pipeline = new ScrapePipeline(_library, providerManager, directoryService, _logger);

                var reader = MakeService().Reader;
                var items = reader.GetAllScannableItems();
                if (libraryIds.Count > 0)
                {
                    items = items.Where(i =>
                    {
                        var doc = reader.ToDoc(i);
                        return libraryIds.Any(x => string.Equals(x, doc.LibraryId, StringComparison.OrdinalIgnoreCase));
                    }).ToList();
                }

                var result = await pipeline.RunAsync(settings, items, dryRun, CancellationToken.None, mode)
                    .ConfigureAwait(false);

                // 预演不写日志；真实执行才记录，供「复原」使用
                if (!dryRun && result.RenameLogs.Count > 0)
                {
                    foreach (var log in result.RenameLogs) { store.RenameLogs.Upsert(log); }
                }
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重命名执行失败");
                return Error(500, "执行失败：" + ex.Message);
            }
        }

        private static bool ReadBool(System.Text.Json.JsonElement node, params string[] names)
        {
            foreach (var n in names)
            {
                if (!TryProp(node, n, out var v)) continue;
                if (v.ValueKind == System.Text.Json.JsonValueKind.True) return true;
                if (v.ValueKind == System.Text.Json.JsonValueKind.False) return false;
                if (v.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = v.GetString();
                    return s != null && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1");
                }
            }
            return false;
        }

        private static string ReadString(System.Text.Json.JsonElement node, params string[] names)
        {
            foreach (var n in names)
            {
                if (!TryProp(node, n, out var v)) continue;
                if (v.ValueKind == System.Text.Json.JsonValueKind.String) return v.GetString();
            }
            return null;
        }

        private static List<string> ReadStringList(System.Text.Json.JsonElement node, params string[] names)
        {
            var list = new List<string>();
            foreach (var n in names)
            {
                if (!TryProp(node, n, out var v) || v.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (var item in v.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                    }
                }
                break;
            }
            return list;
        }

        private static List<RenameRule> ReadRules(System.Text.Json.JsonElement node, params string[] names)
        {
            var list = new List<RenameRule>();
            foreach (var n in names)
            {
                if (!TryProp(node, n, out var v) || v.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (var item in v.EnumerateArray())
                {
                    if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    var rule = new RenameRule();
                    if (TryProp(item, "pattern", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String) rule.Pattern = p.GetString() ?? string.Empty;
                    if (TryProp(item, "Pattern", out var p2) && p2.ValueKind == System.Text.Json.JsonValueKind.String) rule.Pattern = p2.GetString() ?? string.Empty;
                    rule.Enabled = !TryProp(item, "enabled", out var en) || en.ValueKind == System.Text.Json.JsonValueKind.True || en.ValueKind == System.Text.Json.JsonValueKind.Null;
                    if (TryProp(item, "Enabled", out var en2)) rule.Enabled = en2.ValueKind == System.Text.Json.JsonValueKind.True;
                    if (TryProp(item, "onlyIfPathContains", out var op) && op.ValueKind == System.Text.Json.JsonValueKind.String) rule.OnlyIfPathContains = op.GetString() ?? string.Empty;
                    if (TryProp(item, "note", out var nt) && nt.ValueKind == System.Text.Json.JsonValueKind.String) rule.Note = nt.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(rule.Pattern)) continue;
                    list.Add(rule);
                }
                break;
            }
            return list;
        }

        private static bool TryProp(System.Text.Json.JsonElement node, string name, out System.Text.Json.JsonElement value)
        {
            value = default;
            if (node.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            foreach (var prop in node.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) { value = prop.Value; return true; }
            }
            return false;
        }

        public sealed class RenameTestRequest
        {
            public string Source { get; set; } = string.Empty;
            public List<RenameRule> Rules { get; set; }
        }

        public sealed class RenameRunRequest
        {
            public bool DryRun { get; set; }
            public List<string> LibraryIds { get; set; }
            public List<RenameRule> Rules { get; set; }
        }

        /// <summary>重命名历史（用于复原）。</summary>
        [HttpGet("Rename/Logs")]
        public ActionResult GetRenameLogs()
        {
            using var store = Open();
            var rows = store.RenameLogs.FindAll()
                .OrderByDescending(x => x.CreatedAt)
                .Take(500)
                .Select(x => new
                {
                    batchId = x.BatchId,
                    itemId = x.ItemId,
                    oldName = x.OldName,
                    newName = x.NewName,
                    oldPath = x.OldPath,
                    newPath = x.NewPath,
                    fileMoved = x.FileMoved,
                    nameChanged = x.NameChanged,
                    restored = x.Restored,
                    createdAt = x.CreatedAt
                })
                .ToList();
            var batches = rows.GroupBy(x => x.batchId)
                .Select(g => new { batchId = g.Key, count = g.Count(), createdAt = g.Max(y => y.createdAt) })
                .ToList();
            return Ok(new { rows, batches });
        }

        /// <summary>复原重命名：把文件搬回原名、把 Jellyfin 名称改回原名。可按批次或单条。</summary>
        [HttpPost("Rename/Restore")]
        public async Task<ActionResult> RestoreRename([FromBody] RestoreRenameRequest body)
        {
            if (body == null || (string.IsNullOrWhiteSpace(body.BatchId) && string.IsNullOrWhiteSpace(body.LogId)))
            {
                return Error(400, "请指定要复原的批次或单条记录。");
            }
            using var store = Open();
            var targets = body.LogId != null
                ? store.RenameLogs.Find(x => x.Id == body.LogId).ToList()
                : store.RenameLogs.Find(x => x.BatchId == body.BatchId).ToList();

            var restored = 0;
            var failed = new List<object>();

            foreach (var log in targets)
            {
                if (log.Restored) continue;
                try
                {
                    // 1) 文件搬回原路径（若当初确实移动过）
                    if (log.FileMoved && !string.IsNullOrWhiteSpace(log.OldPath) && !string.IsNullOrWhiteSpace(log.NewPath))
                    {
                        if (System.IO.File.Exists(log.NewPath) && !System.IO.File.Exists(log.OldPath))
                        {
                            System.IO.File.Move(log.NewPath, log.OldPath);
                        }
                    }

                    // 2) Jellyfin 名称改回原名
                    if (log.NameChanged && !string.IsNullOrWhiteSpace(log.OldName))
                    {
                        var item = _library.GetItemById(Guid.Parse(log.ItemId));
                        if (item == null) return Error(404, "影片已不存在，无法复原名称。");
                        item.Name = log.OldName;
                        await _library.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    log.Restored = true;
                    log.RestoredAt = DateTime.Now;
                    store.RenameLogs.Update(log);
                    restored++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "复原重命名失败 {Id}", log.Id);
                    failed.Add(new { id = log.Id, message = ex.Message });
                }
            }

            return Ok(new { ok = true, restored, failed });
        }

        public sealed class RestoreRenameRequest
        {
            public string BatchId { get; set; }
            public string LogId { get; set; }
        }

        // ---------------------------------------------------------------- 完整页面

        /// <summary>
        /// 返回完整 HTML 文档，供配置页里的 iframe 加载。
        /// 配置页本身被控制台以「视图」注入，注入内容中的脚本不会执行；
        /// iframe 加载的是独立文档，脚本正常执行，因此所有按钮与数据加载都可用。
        /// </summary>
        [HttpGet("/CollectionStudio/Full")]
        [AllowAnonymous]
        [Produces("text/html")]
        public ActionResult GetFullPage()
        {
            try
            {
                var asm = typeof(Plugin).Assembly;
                var resource = typeof(Plugin).Namespace + ".web.full.html";
                using var stream = asm.GetManifestResourceStream(resource);
                if (stream == null) return Error(500, "页面资源缺失：" + resource);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                var content = reader.ReadToEnd();
                if (!content.TrimStart().StartsWith("<!", StringComparison.Ordinal))
                {
                    content = "<!doctype html>\n<html lang=\"zh-CN\">\n<head><meta charset=\"utf-8\" /></head>\n<body>\n"
                        + content + "\n</body>\n</html>\n";
                }
                // 允许同源页面嵌入
                Response.Headers.Remove("X-Frame-Options");
                // 禁止缓存：否则升级插件后浏览器仍用旧页面，界面上看不到新功能
                Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                Response.Headers["Pragma"] = "no-cache";
                Response.Headers["Expires"] = "0";
                return new ContentResult
                {
                    Content = content,
                    ContentType = "text/html; charset=utf-8",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取完整页面失败");
                return Error(500, ex.Message);
            }
        }

        // ---------------------------------------------------------------- 清单代理

        /// <summary>
        /// 插件清单的转发端点。直连 raw.githubusercontent.com 在国内网络常被阻断，
        /// 而 gh-proxy 会带 60 秒 CDN 缓存，导致刚发布的新版本在控制台里看不到。
        /// 这里加时间戳绕过缓存，并把结果原样返回，作为「存储库 URL」使用即可。
        /// </summary>
        [HttpGet("/CollectionStudio/Manifest")]
        [AllowAnonymous]
        [Produces("application/json")]
        public async Task<ActionResult> GetManifest([FromQuery] string name = "manifest-12.json")
        {
            if (!Regex.IsMatch(name, @"^manifest(-(10|109|12)?)?\.json$", RegexOptions.IgnoreCase))
            {
                return Error(400, "非法清单名");
            }

            var baseUrl = "https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio";
            var branch = name.Contains("-109") ? "main" : (name.Contains("-10") ? "main" : "main");
            var target = baseUrl + "/" + branch + "/" + name + "?v=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.Add("Cache-Control", "no-cache, no-store");
                http.DefaultRequestHeaders.Add("Pragma", "no-cache");
                using var response = await http.GetAsync(target, CancellationToken.None);
                var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
                if (!response.IsSuccessStatusCode)
                {
                    return Error(502, "上游清单获取失败：" + response.StatusCode);
                }
                return new ContentResult
                {
                    Content = body,
                    ContentType = "application/json; charset=utf-8",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取清单失败");
                return Error(502, ex.Message);
            }
        }

        // ---------------------------------------------------------------- 状态

        [HttpGet("Status")]
        public ActionResult GetStatus()
        {
            using var store = Open();
            var settings = store.GetSettings();
            var items = store.Items;
            var personSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lastIndexed = DateTime.MinValue;
            foreach (var it in items.FindAll())
            {
                foreach (var a in it.Actors ?? Array.Empty<string>()) personSet.Add("A|" + a);
                foreach (var d in it.Directors ?? Array.Empty<string>()) personSet.Add("D|" + d);
                if (it.IndexedAt > lastIndexed) lastIndexed = it.IndexedAt;
            }
            var mt = new MetaTubeClient(() => store.GetSettings());
            var reachable = false;
            try { reachable = mt.PingAsync().GetAwaiter().GetResult(); } catch { }
            var backups = store.Backups.FindAll().ToList();
            return Ok(new
            {
                pluginVersion = Plugin.Instance?.Version.ToString() ?? "0.0.0",
                jellyfinVersion = _host.ApplicationVersion.ToString(),
                serverTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                indexedItems = items.Count(),
                personCount = personSet.Count,
                lastIndexedAt = lastIndexed == DateTime.MinValue ? string.Empty : lastIndexed.ToString("yyyy-MM-dd HH:mm:ss"),
                metaTubeConfigured = !string.IsNullOrWhiteSpace(settings.MetaTubeBaseUrl),
                metaTubeReachable = reachable,
                dbPath = store.FilePath,
                backupCount = backups.Count,
                customSourceCount = settings.CustomSources?.Count ?? 0
            });
        }

        // ---------------------------------------------------------------- 设置

        [HttpGet("Settings")]
        public ActionResult GetSettings()
        {
            using var store = Open();
            return Ok(store.GetSettings());
        }

        [HttpPost("Settings")]
        public ActionResult SaveSettings([FromBody] CsSettings settings)
        {
            if (settings == null) return Error(400, "没有收到设置内容。");
            settings.MetaTubeTimeoutSeconds = Math.Clamp(settings.MetaTubeTimeoutSeconds, 3, 120);
            settings.DefaultPageSize = settings.DefaultPageSize <= 0 ? 50 : Math.Clamp(settings.DefaultPageSize, 10, 500);
            settings.CustomSources ??= new List<CustomSource>();
            foreach (var s in settings.CustomSources)
            {
                if (string.IsNullOrWhiteSpace(s.Id)) s.Id = Guid.NewGuid().ToString("N");
                s.Headers ??= new List<HeaderPair>();
            }
            using var store = Open();
            store.SaveSettings(settings);
            return Ok(new { ok = true, settings });
        }

        // ---------------------------------------------------------------- 字段

        [HttpGet("Fields")]
        public ActionResult GetFields() => Ok(RuleEngine.Fields);

        /// <summary>媒体库列表：规则里用来限定分类范围。</summary>
        [HttpGet("Libraries")]
        public ActionResult GetLibraries()
        {
            using var store = Open();
            var libs = MakeService().Reader.GetLibraries();
            var index = store.AllItems();
            foreach (var lib in libs)
            {
                lib.ItemCount = index.Count(i => string.Equals(i.LibraryId, lib.Id, StringComparison.OrdinalIgnoreCase));
            }
            return Ok(libs);
        }

        // ---------------------------------------------------------------- 规则

        [HttpGet("Rules")]
        public ActionResult GetRules()
        {
            using var store = Open();
            var rules = store.Rules.FindAll().OrderBy(r => r.CreatedAt).ToList();
            return Ok(rules);
        }

        [HttpPost("Rules")]
        public ActionResult SaveRule([FromBody] RuleDoc rule)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Name)) return Error(400, "规则名称不能为空。");
            using var store = Open();
            if (string.IsNullOrWhiteSpace(rule.Id)) { rule.Id = Guid.NewGuid().ToString("N"); rule.CreatedAt = DateTime.Now; }
            rule.UpdatedAt = DateTime.Now;
            rule.Conditions ??= new List<RuleCondition>();
            rule.TitleTemplate = string.IsNullOrWhiteSpace(rule.TitleTemplate) ? "{value}" : rule.TitleTemplate;
            rule.MinItems = Math.Max(1, rule.MinItems);
            var existing = store.Rules.FindById(rule.Id);
            if (existing != null) rule.CreatedAt = existing.CreatedAt;
            store.Rules.Upsert(rule);
            return Ok(rule);
        }

        [HttpDelete("Rules/{id}")]
        public ActionResult DeleteRule(string id)
        {
            using var store = Open();
            var rule = store.Rules.FindById(id);
            if (rule == null) return Error(404, "规则不存在或已被删除。");
            store.Backup(Plugin.Instance?.Name ?? "Collection Studio", "predelete", "删除规则前备份：" + rule.Name);
            store.Rules.Delete(id);
            return Ok(new { ok = true });
        }

        [HttpPost("Rules/{id}/Preview")]
        public ActionResult PreviewRule(string id)
        {
            using var store = Open();
            var rule = store.Rules.FindById(id);
            if (rule == null) return Error(404, "规则不存在或已被删除。");
            var svc = MakeService();
            var result = svc.Preview(rule, store.AllItems());
            return Ok(result);
        }

        /// <summary>规则执行诊断：把索引数量、媒体库匹配、条件过滤、分组结果逐步暴露出来。</summary>
        [HttpGet("Rules/{id}/Diagnose")]
        public ActionResult DiagnoseRule(string id)
        {
            using var store = Open();
            var rule = store.Rules.FindById(id);
            if (rule == null) return Error(404, "规则不存在。");

            var index = store.AllItems();
            var byLibrary = index
                .GroupBy(i => i.LibraryId ?? string.Empty)
                .Select(g => new { libraryId = g.Key, libraryName = g.FirstOrDefault()?.LibraryName ?? string.Empty, count = g.Count() })
                .ToList();

            var afterLib = index.Where(i => RuleEngine.InSelectedLibraries(i, rule)).ToList();
            var afterCond = afterLib.Where(i => RuleEngine.PassesConditions(i, rule)).ToList();
            var groups = RuleEngine.Group(index, rule);

            var withActors = index.Count(i => i.Actors != null && i.Actors.Length > 0);
            var withDirectors = index.Count(i => i.Directors != null && i.Directors.Length > 0);

            return Ok(new
            {
                ruleId = rule.Id,
                ruleName = rule.Name,
                mode = rule.Mode,
                personType = rule.PersonType,
                libraryIds = rule.LibraryIds,
                minItems = rule.MinItems,
                titleTemplate = rule.TitleTemplate,
                conditions = rule.Conditions?.Count ?? 0,
                indexedTotal = index.Count,
                librariesInIndex = byLibrary,
                afterLibraryFilter = afterLib.Count,
                afterConditions = afterCond.Count,
                itemsWithActors = withActors,
                itemsWithDirectors = withDirectors,
                groupCount = groups.Count,
                groups = groups.Take(20).Select(g => new { name = g.Key, count = g.Value.Count }).ToList()
            });
        }

        [HttpPost("Rules/{id}/Apply")]
        public ActionResult ApplyRule(string id)
        {
            using var store = Open();
            var rule = store.Rules.FindById(id);
            if (rule == null) return Error(404, "规则不存在或已被删除。");
            var svc = MakeService();
            try
            {
                var result = svc.ApplyAsync(rule, store, CancellationToken.None).GetAwaiter().GetResult();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行规则 {Rule} 失败", rule.Name);
                return Error(500, "执行规则失败：" + ex.Message);
            }
        }

        private CollectionService MakeService()
        {
            var providerManager = (MediaBrowser.Controller.Providers.IProviderManager)HttpContext.RequestServices
                .GetService(typeof(MediaBrowser.Controller.Providers.IProviderManager));
            var repo = (MediaBrowser.Controller.Persistence.IItemRepository)HttpContext.RequestServices
                .GetService(typeof(MediaBrowser.Controller.Persistence.IItemRepository));
            return new CollectionService(_library, _collections, providerManager, repo, _logger, () => Plugin.OpenStore());
        }

        // ---------------------------------------------------------------- 合集

        [HttpGet("Collections")]
        public ActionResult GetCollections()
        {
            using var store = Open();
            var docs = store.Collections.FindAll().OrderByDescending(c => c.UpdatedAt).ToList();
            var rows = new List<object>();
            foreach (var d in docs)
            {
                var box = ResolveBoxSet(d.Id);
                rows.Add(new
                {
                    id = d.Id,
                    name = d.Name,
                    ruleId = d.RuleId,
                    itemCount = box != null ? MakeService().GetMemberIds(box).Count : d.ItemCount,
                    createdAt = d.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                    updatedAt = d.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                    coverUrl = box != null && box.HasImage(MediaBrowser.Model.Entities.ImageType.Primary, 0)
                        ? ("/Items/" + box.Id.ToString("N") + "/Images/Primary?maxHeight=180&quality=80")
                        : string.Empty,
                    managedBy = d.ManagedBy,
                    backupCount = store.Backups.Count()
                });
            }
            return Ok(rows);
        }

        private BaseItem ResolveBoxSet(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            Guid g;
            try { g = Guid.Parse(id); } catch { return null; }
            try { return _library.GetItemById(g); } catch { return null; }
        }

        [HttpGet("Collections/{id}/Items")]
        public ActionResult GetCollectionItems(string id)
        {
            var box = ResolveBoxSet(id);
            if (box == null) return Error(404, "合集不存在（可能已被删除，请重新执行规则）。");
            var items = new List<object>();
            try
            {
                if (box is Folder folder && folder.LinkedChildren != null)
                {
                    foreach (var lc in folder.LinkedChildren)
                    {
                        if (lc == null || !lc.ItemId.HasValue) continue;
                        var target = _library.GetItemById(lc.ItemId.Value);
                        items.Add(new
                        {
                            itemId = lc.ItemId.Value.ToString("N"),
                            name = target?.Name ?? string.Empty,
                            path = target?.Path ?? string.Empty
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("读取合集成员失败：{Message}", ex.Message);
            }
            return Ok(new { name = box.Name, items });
        }

        [HttpPost("Collections/{id}/Delete")]
        public ActionResult DeleteCollection(string id)
        {
            using var store = Open();
            var doc = store.Collections.FindById(id);
            var box = ResolveBoxSet(id);
            if (box == null && doc == null) return Error(404, "合集不存在。");
            var backup = store.Backup(Plugin.Instance?.Name ?? "Collection Studio", "predelete", "删除合集前备份：" + (doc?.Name ?? box?.Name ?? id));
            try
            {
                if (box != null) _library.DeleteItem(box, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除合集失败");
                return Error(500, "删除合集失败：" + ex.Message);
            }
            store.Collections.Delete(id);
            return Ok(new { ok = true, backupId = backup.Id, backupName = backup.Name });
        }

        /// <summary>一次性删除多个合集（只删合集条目，绝不删除媒体文件）。</summary>
        [HttpPost("Collections/BulkDelete")]
        public ActionResult BulkDeleteCollections([FromBody] BulkDeleteRequest body)
        {
            if (body == null || body.Ids == null || body.Ids.Count == 0) return Error(400, "没有选择要删除的合集。");
            using var store = Open();
            var deleted = new List<string>();
            var failed = new List<object>();

            // 整批只备份一次，避免每删一个就写一个备份
            try { store.Backup(Plugin.Instance?.Name ?? "Collection Studio", "predelete", "批量删除合集前备份（" + body.Ids.Count + " 个）"); }
            catch (Exception ex) { _logger.LogWarning("批量删除前备份失败：{Message}", ex.Message); }

            foreach (var id in body.Ids)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                try
                {
                    var box = ResolveBoxSet(id);
                    // DeleteFileLocation=false：仅移除合集条目，媒体文件保持原样
                    if (box != null)
                    {
                        _library.DeleteItem(box, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false });
                    }
                    store.Collections.Delete(id);
                    deleted.Add(id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "删除合集 {Id} 失败", id);
                    failed.Add(new { id, message = ex.Message });
                }
            }

            return Ok(new { ok = true, deletedCount = deleted.Count, deleted, failed });
        }

        /// <summary>清理失效的合集记录：Jellyfin 里已不存在的合集，同步从表格中移除。</summary>
        [HttpPost("Collections/Prune")]
        public ActionResult PruneCollections()
        {
            using var store = Open();
            var removed = 0;
            foreach (var doc in store.Collections.FindAll().ToList())
            {
                if (ResolveBoxSet(doc.Id) == null)
                {
                    store.Collections.Delete(doc.Id);
                    removed++;
                }
            }
            return Ok(new { ok = true, removed, remaining = store.Collections.Count() });
        }

        public sealed class BulkDeleteRequest
        {
            public List<string> Ids { get; set; } = new List<string>();
        }

        [HttpPost("Collections/{id}/Cover")]
        public ActionResult SetCover(string id, [FromBody] CoverRequest body)
        {
            var box = ResolveBoxSet(id);
            if (box == null) return Error(404, "合集不存在。");
            if (body == null || string.IsNullOrWhiteSpace(body.Url)) return Error(400, "请先选择一张图片作为封面。");
            var providerManager = (MediaBrowser.Controller.Providers.IProviderManager)HttpContext.RequestServices
                .GetService(typeof(MediaBrowser.Controller.Providers.IProviderManager));
            try
            {
                providerManager.SaveImage(box, body.Url.Trim(), MediaBrowser.Model.Entities.ImageType.Primary, 0, CancellationToken.None)
                    .GetAwaiter().GetResult();
                using var store = Open();
                var doc = store.Collections.FindById(id);
                if (doc != null) { doc.CoverUrl = body.Url.Trim(); doc.UpdatedAt = DateTime.Now; store.Collections.Upsert(doc); }
                return Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning("设置封面失败：{Message}", ex.Message);
                return Error(500, "设置封面失败：" + ex.Message);
            }
        }

        public sealed class CoverRequest
        {
            public string Url { get; set; } = string.Empty;
        }

        // ---------------------------------------------------------------- 封面库

        [HttpGet("Covers/Search")]
        public ActionResult SearchCovers([FromQuery] string? name, [FromQuery] string? provider, [FromQuery] string? source)
        {
            if (string.IsNullOrWhiteSpace(name)) return Error(400, "请先填写要搜索的演员或导演名称。");
            using var store = Open();
            var settings = store.GetSettings();
            var mt = new MetaTubeClient(() => settings);
            try
            {
                if (!string.IsNullOrWhiteSpace(source) && source.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
                {
                    var sid = source.Substring(7);
                    var src = settings.CustomSources?.FirstOrDefault(s => string.Equals(s.Id, sid, StringComparison.OrdinalIgnoreCase));
                    if (src == null) return Error(404, "找不到该自定义刮削源，可能已被删除。");
                    var res = mt.SearchCustomAsync(name, src, CancellationToken.None).GetAwaiter().GetResult();
                    return Ok(res);
                }
                var res2 = mt.SearchActorAsync(name, provider, CancellationToken.None).GetAwaiter().GetResult();
                return Ok(res2);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("搜索封面失败：{Message}", ex.Message);
                return Error(502, "搜索封面失败：" + ex.Message);
            }
        }

        [HttpGet("Covers/Proxy")]
        public ActionResult ProxyImage([FromQuery] string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return Error(400, "缺少图片地址。");
            if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                return Error(400, "只允许代理 http/https 图片地址。");
            var settings = LoadSettingsSafe();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(settings.MetaTubeTimeoutSeconds, 3, 120)));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "image/*");
                if (!string.IsNullOrWhiteSpace(settings.MetaTubeToken) && url.IndexOf("MetaTube", StringComparison.OrdinalIgnoreCase) >= 0)
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.MetaTubeToken.Trim());
                using var resp = MetaTubeClient.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) return Error(502, "图片源返回 HTTP " + (int)resp.StatusCode + "。");
                var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                using var stream = resp.Content.ReadAsStreamAsync(cts.Token).GetAwaiter().GetResult();
                var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                bytes.Position = 0;
                return new FileStreamResult(bytes, mediaType);
            }
            catch (Exception ex)
            {
                return Error(502, "代理图片失败：" + ex.Message);
            }
        }

        private static CsSettings LoadSettingsSafe()
        {
            try { using var store = Plugin.OpenStore(); return store.GetSettings(); }
            catch { return new CsSettings(); }
        }

        /// <summary>
        /// 写权限诊断：对指定媒体库的实际目录做真实的写入测试，
        /// 判断是「只读挂载」「无写权限」还是「目录不存在」，并返回可执行的结论。
        /// </summary>

        /// <summary>
        /// 读取容器内的 /proc/mounts，找出某个路径实际落在哪个挂载点、是否为只读。
        /// 这是判断「挂载参数说 RW=true 但实际只读」的唯一可靠办法：
        /// Docker 记录的 RW 只是 bind 参数，不反映底层文件系统（例如 OpenWrt 的根是只读 squashfs）。
        /// </summary>
        private static (string mountPoint, bool readOnly, string fsType) ResolveMount(string path)
        {
            try
            {
                var full = System.IO.Path.GetFullPath(path ?? string.Empty);
                var lines = System.IO.File.ReadAllLines("/proc/mounts");
                string bestPoint = string.Empty;
                bool bestRo = false;
                string bestType = string.Empty;

                foreach (var line in lines)
                {
                    var parts = line.Split(' ');
                    if (parts.Length < 4) continue;
                    var point = parts[1].Replace("\\040", " ");
                    var opts = parts[3];
                    var fsType = parts[2];

                    // 路径必须停在该挂载点之下（或正好是它），取最长前缀匹配
                    var under = point == "/"
                        ? true
                        : (full == point || full.StartsWith(point.TrimEnd('/') + "/", StringComparison.Ordinal));
                    if (!under) continue;
                    if (point.Length < bestPoint.Length) continue;

                    bestPoint = point;
                    bestType = fsType;
                    bestRo = opts.StartsWith("ro,") || opts == "ro" || opts.Contains(",ro,") || opts.EndsWith(",ro");
                }

                return (bestPoint, bestRo, bestType);
            }
            catch
            {
                return (string.Empty, false, string.Empty);
            }
        }
        [HttpGet("Rename/WriteCheck")]
        public ActionResult WriteCheck([FromQuery] string? libraryId = null)
        {
            var reader = MakeService().Reader;
            var report = new List<object>();

            List<IndexItemDoc> docs;
            try
            {
                docs = reader.GetAllScannableItems().Select(i => reader.ToDoc(i)).ToList();
            }
            catch (Exception ex)
            {
                return Error(500, "读取媒体库失败：" + ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(libraryId))
            {
                docs = docs.Where(d => string.Equals(d.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // 按目录去重，逐个测试该目录是否可写
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in docs.Take(2000))
            {
                try
                {
                    var dir = string.IsNullOrWhiteSpace(d.Path) ? null : System.IO.Path.GetDirectoryName(d.Path);
                    if (!string.IsNullOrWhiteSpace(dir)) dirs.Add(dir);
                }
                catch { }
            }

            foreach (var dir in dirs.Take(20))
            {
                var entry = new Dictionary<string, object>
                {
                    ["directory"] = dir,
                    ["exists"] = false,
                    ["writable"] = false,
                    ["verdict"] = string.Empty,
                    ["detail"] = string.Empty
                };

                try
                {
                    var dirExists = System.IO.Directory.Exists(dir);
                    entry["exists"] = dirExists;

                    // 先给出该目录实际所在的挂载点与只读标记，这对判断「RW=true 却报只读」最关键
                    var mount = ResolveMount(dir);
                    entry["mountPoint"] = mount.mountPoint;
                    entry["mountReadOnly"] = mount.readOnly;
                    entry["fileSystem"] = mount.fsType;

                    if (!dirExists)
                    {
                        entry["verdict"] = "目录不存在";
                        report.Add(entry);
                        continue;
                    }

                    if (mount.readOnly)
                    {
                        entry["verdict"] = "该目录所在挂载点在容器内是只读（" + mount.mountPoint + "，" + mount.fsType + "）";
                        entry["detail"] = "Docker 的 RW=true 只是 bind 参数，不代表底层文件系统可写。"
                                        + "请把媒体目录挂到真正可写的位置（例如 OpenWrt 的 /overlay 或 /mnt 下的可写分区），"
                                        + "而不是把只读的根文件系统挂进容器。";
                        report.Add(entry);
                        continue;
                    }

                    // 真实写入测试：创建再删除一个临时文件
                    var probe = System.IO.Path.Combine(dir, ".cs-write-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    try
                    {
                        using (var fs = System.IO.File.Create(probe, 1, FileOptions.DeleteOnClose))
                        {
                            fs.Write(new byte[] { 0x43, 0x53 }, 0, 2);
                        }
                        entry["writable"] = true;
                        entry["verdict"] = "可写，重命名磁盘文件应能成功";
                    }
                    catch (Exception ex)
                    {
                        var msg = ex.GetType().Name + " - " + ex.Message;
                        if (msg.IndexOf("Read-only file system", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            entry["verdict"] = "容器内该目录是只读挂载（挂载参数为 :ro）";
                            entry["detail"] = "在宿主机检查 docker 挂载：docker inspect jellyfin --format '{{range .Mounts}}{{.Source}} -> {{.Destination}} RW={{.RW}}{{println}}{{end}}'；"
                                            + "把媒体卷的 :ro 去掉或改为 :rw 后重建容器。";
                        }
                        else if (msg.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            entry["verdict"] = "目录可访问但当前用户无写权限";
                            entry["detail"] = "在宿主机给该目录授权（容器内用户通常 UID=1000）："
                                            + "chown -R 1000:1000 <宿主机目录> 或 chmod -R a+rw <宿主机目录>。";
                        }
                        else if (msg.IndexOf("No space", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            entry["verdict"] = "磁盘空间不足";
                        }
                        else
                        {
                            entry["verdict"] = "写入失败：" + msg;
                        }
                    }
                }
                catch (Exception ex)
                {
                    entry["verdict"] = "检测异常：" + ex.Message;
                }
                report.Add(entry);
            }

            return Ok(new { checkedDirectories = report.Count, report });
        }

        // ---------------------------------------------------------------- 媒体库文件清单

        /// <summary>
        /// 按媒体库列出磁盘上的实际文件信息，用于排查命名。
        /// 路径、文件名、修改时间、大小都是实时从磁盘读取，不使用索引缓存。
        /// 支持表头正序 / 倒序排序与关键词筛选。
        /// </summary>
        [HttpGet("LibraryFiles")]
        public async Task<ActionResult> GetLibraryFiles([FromQuery] int page, [FromQuery] int pageSize,
            [FromQuery] string? libraryId = null, [FromQuery] string? filter = null,
            [FromQuery] string? sortBy = null, [FromQuery] bool sortDesc = false)
        {
            var reader = MakeService().Reader;
            List<IndexItemDoc> docs;
            try
            {
                var items = reader.GetAllScannableItems();
                docs = new List<IndexItemDoc>();
                foreach (var it in items)
                {
                    try { docs.Add(reader.ToDoc(it)); } catch { }
                }
            }
            catch (Exception ex)
            {
                return Error(500, "读取媒体库失败：" + ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(libraryId))
            {
                docs = docs.Where(d => string.Equals(d.LibraryId, libraryId.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            }

            var keyword = (filter ?? string.Empty).Trim();
            if (keyword.Length > 0)
            {
                docs = docs.Where(d =>
                    (d.Path ?? string.Empty).IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (d.FileName ?? string.Empty).IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (d.Name ?? string.Empty).IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            var rows = docs.Select(d =>
            {
                long size = 0;
                DateTime modified = DateTime.MinValue;
                try
                {
                    if (!string.IsNullOrWhiteSpace(d.Path) && System.IO.File.Exists(d.Path))
                    {
                        var info = new System.IO.FileInfo(d.Path);
                        size = info.Length;
                        modified = info.LastWriteTime;
                    }
                }
                catch { }

                return new LibFileRow
                {
                    ItemId = d.ItemId,
                    Path = d.Path ?? string.Empty,
                    FileName = d.FileName ?? string.Empty,
                    JellyfinName = d.Name ?? string.Empty,
                    LibraryName = d.LibraryName ?? string.Empty,
                    SizeBytes = size,
                    ModifiedAt = modified == DateTime.MinValue ? string.Empty : modified.ToString("yyyy-MM-dd HH:mm:ss"),
                    // 名称与文件名不一致时标出来，方便一眼定位可疑项
                    NameMismatch = !string.Equals((d.FileName ?? string.Empty).Trim(),
                                                  (d.Name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
                };
            }).ToList();

            rows = SortLibFiles(rows, sortBy, sortDesc);
            var sizePage = pageSize <= 0 ? 50 : Math.Clamp(pageSize, 10, 500);
            var pageIndex = page <= 0 ? 1 : page;
            var total = rows.Count;
            var slice = rows.Skip((pageIndex - 1) * sizePage).Take(sizePage).ToList();
            return Ok(new { total, page = pageIndex, pageSize = sizePage, rows = slice });
        }

        private static List<LibFileRow> SortLibFiles(List<LibFileRow> rows, string sortBy, bool desc)
        {
            var key = (sortBy ?? "path").ToLowerInvariant();
            Comparer<LibFileRow> cmp;
            switch (key)
            {
                case "filename":
                    cmp = Comparer<LibFileRow>.Create((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));
                    break;
                case "modified":
                    cmp = Comparer<LibFileRow>.Create((a, b) => string.CompareOrdinal(a.ModifiedAt, b.ModifiedAt));
                    break;
                case "size":
                    cmp = Comparer<LibFileRow>.Create((a, b) => a.SizeBytes.CompareTo(b.SizeBytes));
                    break;
                case "path":
                default:
                    cmp = Comparer<LibFileRow>.Create((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
                    break;
            }
            var sorted = rows.OrderBy(x => x, cmp).ToList();
            if (desc) sorted = sorted.AsEnumerable().Reverse().ToList();
            return sorted;
        }

        // ---------------------------------------------------------------- 改名对照

        [HttpGet("Rename")]
        public ActionResult GetRenames([FromQuery] int page, [FromQuery] int pageSize, [FromQuery] string? filter,
            [FromQuery] string? sortBy, [FromQuery] bool sortDesc)
        {
            using var store = Open();
            var all = store.AllItems();
            var collectionMap = BuildCollectionMap(store);
            var rows = all.Select(i =>
            {
                var jt = i.Name ?? string.Empty;
                var orig = string.IsNullOrWhiteSpace(i.OriginalTitle) ? jt : i.OriginalTitle;
                return new RenameRow
                {
                    ItemId = i.ItemId,
                    Path = i.Path,
                    OriginalTitle = orig,
                    JellyfinTitle = jt,
                    Changed = !string.Equals(orig.Trim(), jt.Trim(), StringComparison.OrdinalIgnoreCase),
                    Kind = i.Kind,
                    Year = i.Year,
                    PremiereDate = i.PremiereDate?.ToString("yyyy-MM-dd") ?? string.Empty,
                    Collection = collectionMap.TryGetValue(i.ItemId, out var cols) ? string.Join("、", cols) : string.Empty
                };
            }).ToList();

            if (!string.IsNullOrWhiteSpace(filter))
            {
                var f = filter.Trim();
                rows = rows.Where(r => r.JellyfinTitle != null && r.JellyfinTitle.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            rows = SortRenames(rows, sortBy, sortDesc);
            var size = pageSize <= 0 ? 50 : Math.Clamp(pageSize, 10, 500);
            var total = rows.Count;
            var pages = Math.Max(1, (int)Math.Ceiling(total / (double)size));
            var p = Math.Clamp(page <= 0 ? 1 : page, 1, pages);
            return Ok(new PagedRenames
            {
                Total = total,
                Page = p,
                PageSize = size,
                Rows = rows.Skip((p - 1) * size).Take(size).ToList()
            });
        }

        private static List<RenameRow> SortRenames(List<RenameRow> rows, string sortBy, bool desc)
        {
            IEnumerable<RenameRow> ordered;
            switch ((sortBy ?? "jellyfinTitle").ToLowerInvariant())
            {
                case "path": ordered = desc ? rows.OrderByDescending(r => r.Path, StringComparer.CurrentCulture) : rows.OrderBy(r => r.Path, StringComparer.CurrentCulture); break;
                case "originalTitle": ordered = desc ? rows.OrderByDescending(r => r.OriginalTitle, StringComparer.CurrentCulture) : rows.OrderBy(r => r.OriginalTitle, StringComparer.CurrentCulture); break;
                case "year": ordered = desc ? rows.OrderByDescending(r => r.Year ?? 0) : rows.OrderBy(r => r.Year ?? 0); break;
                case "kind": ordered = desc ? rows.OrderByDescending(r => r.Kind, StringComparer.CurrentCulture) : rows.OrderBy(r => r.Kind, StringComparer.CurrentCulture); break;
                default: ordered = desc ? rows.OrderByDescending(r => r.JellyfinTitle, StringComparer.CurrentCulture) : rows.OrderBy(r => r.JellyfinTitle, StringComparer.CurrentCulture); break;
            }
            return ordered.ToList();
        }

        private static Dictionary<string, List<string>> BuildCollectionMap(Store store)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in store.Collections.FindAll())
            {
                foreach (var id in c.ItemIds ?? new List<string>())
                {
                    if (!map.TryGetValue(id, out var list)) { list = new List<string>(); map[id] = list; }
                    if (!list.Contains(c.Name)) list.Add(c.Name);
                }
            }
            return map;
        }

        [HttpGet("Rename/Export")]
        public ActionResult ExportRenames([FromQuery] int page, [FromQuery] int pageSize, [FromQuery] string? filter,
            [FromQuery] string? sortBy, [FromQuery] bool sortDesc, [FromQuery] string? destination, [FromQuery] string? scope)
        {
            destination = string.IsNullOrWhiteSpace(destination) ? "client" : destination.Trim().ToLowerInvariant();
            scope = string.IsNullOrWhiteSpace(scope) ? "page" : scope.Trim().ToLowerInvariant();

            List<RenameRow> rows;
            using (var store = Open())
            {
                var all = store.AllItems();
                var collectionMap = BuildCollectionMap(store);
                rows = all.Select(i => new RenameRow
                {
                    ItemId = i.ItemId,
                    Path = i.Path,
                    OriginalTitle = string.IsNullOrWhiteSpace(i.OriginalTitle) ? (i.Name ?? string.Empty) : i.OriginalTitle,
                    JellyfinTitle = i.Name ?? string.Empty,
                    Changed = !string.Equals((string.IsNullOrWhiteSpace(i.OriginalTitle) ? i.Name : i.OriginalTitle)?.Trim() ?? string.Empty,
                        (i.Name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase),
                    Kind = i.Kind,
                    Year = i.Year,
                    PremiereDate = i.PremiereDate?.ToString("yyyy-MM-dd") ?? string.Empty,
                    Collection = collectionMap.TryGetValue(i.ItemId, out var cols) ? string.Join("、", cols) : string.Empty
                }).ToList();
            }

            if (!string.IsNullOrWhiteSpace(filter))
            {
                var f = filter.Trim();
                rows = rows.Where(r => r.JellyfinTitle != null && r.JellyfinTitle.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }
            rows = SortRenames(rows, sortBy, sortDesc);

            if (scope == "page")
            {
                var size = pageSize <= 0 ? 50 : Math.Clamp(pageSize, 10, 500);
                var p = Math.Max(1, page);
                rows = rows.Skip((p - 1) * size).Take(size).ToList();
            }

            var fileName = "改名对照_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".xlsx";
            var headers = new[] { "路径", "原始名称", "Jellyfin 名称", "类型", "年份", "发行日期", "所属合集" };
            var data = rows.Select(r => (IReadOnlyList<object?>)new object?[]
            {
                r.Path, r.OriginalTitle, r.JellyfinTitle, r.Kind, r.Year, r.PremiereDate, r.Collection
            }).ToList();
            byte[] bytes;
            try
            {
                bytes = Xlsx.BuildSingle("改名对照", headers.ToList(), data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成 Excel 失败");
                return Error(500, "生成 Excel 失败：" + ex.Message);
            }

            if (destination == "server")
            {
                try
                {
                    var path = Path.Combine(Plugin.Instance?.ExportFolder ?? Path.GetTempPath(), fileName);
                    System.IO.File.WriteAllBytes(path, bytes);
                    return Ok(new { ok = true, path, fileName, rows = data.Count });
                }
                catch (Exception ex)
                {
                    return Error(500, "保存到服务器失败：" + ex.Message);
                }
            }

            var result = new FileContentResult(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
            {
                FileDownloadName = fileName
            };
            return result;
        }

        // ---------------------------------------------------------------- 备份与还原

        [HttpGet("Backups")]
        public ActionResult GetBackups([FromQuery] string? from, [FromQuery] string? to)
        {
            using var store = Open();
            IEnumerable<BackupDoc> docs = store.Backups.FindAll();
            if (!string.IsNullOrWhiteSpace(from))
            {
                if (DateTime.TryParse(from, out var d1)) docs = docs.Where(d => d.CreatedAt >= d1);
            }
            if (!string.IsNullOrWhiteSpace(to))
            {
                if (DateTime.TryParse(to, out var d2)) docs = docs.Where(d => d.CreatedAt <= d2);
            }
            return Ok(docs.OrderByDescending(d => d.CreatedAt).Select(ToSummary).ToList());
        }

        private static BackupSummary ToSummary(BackupDoc d) => new BackupSummary
        {
            Id = d.Id,
            Name = d.Name,
            CreatedAt = d.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            Kind = d.Kind,
            Note = d.Note,
            SizeBytes = d.SizeBytes,
            Rules = d.Rules,
            Collections = d.Collections,
            Renames = d.Renames
        };

        [HttpPost("Backups")]
        public ActionResult CreateBackup([FromBody] BackupRequest body)
        {
            using var store = Open();
            var doc = store.Backup(Plugin.Instance?.Name ?? "Collection Studio", "manual", body?.Note ?? "手动备份");
            return Ok(ToSummary(doc));
        }

        public sealed class BackupRequest
        {
            public string Note { get; set; } = string.Empty;
        }

        [HttpPost("Backups/Restore")]
        public ActionResult RestoreBackup([FromBody] RestoreRequest body)
        {
            if (body == null || string.IsNullOrWhiteSpace(body.Id)) return Error(400, "请先选择一个要还原的备份。");
            using var store = Open();
            var doc = store.Backups.FindById(body.Id);
            if (doc == null) return Error(404, "备份不存在或已被删除。");
            BackupPayload payload;
            try { payload = store.ReadPayload(doc); }
            catch (Exception ex) { return Error(500, "备份内容损坏，无法还原：" + ex.Message); }

            store.Backup(Plugin.Instance?.Name ?? "Collection Studio", "predelete", "还原前自动备份");
            foreach (var r in payload.Rules) store.Rules.Upsert(r);
            foreach (var c in payload.Collections)
            {
                if (payload.Members.TryGetValue(c.Id, out var members)) c.ItemIds = members;
                store.Collections.Upsert(c);
            }
            if (payload.Settings != null) store.SaveSettings(payload.Settings);
            return Ok(new
            {
                ok = true,
                restoredRules = payload.Rules.Count,
                restoredCollections = payload.Collections.Count,
                backupName = doc.Name,
                backupTime = doc.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss.fff")
            });
        }

        public sealed class RestoreRequest
        {
            public string Id { get; set; } = string.Empty;
        }

        [HttpDelete("Backups/{id}")]
        public ActionResult DeleteBackup(string id)
        {
            using var store = Open();
            if (store.Backups.FindById(id) == null) return Error(404, "备份不存在或已被删除。");
            store.Backups.Delete(id);
            return Ok(new { ok = true });
        }

        // ---------------------------------------------------------------- 定时任务触发器

        /// <summary>把触发器的 Type 设为「每日」——不同版本枚举类型名不同，用反射设置。</summary>
        private static void SetTriggerType(MediaBrowser.Model.Tasks.TaskTriggerInfo info, string prefix)
        {
            try
            {
                var prop = info.GetType().GetProperty("Type");
                if (prop == null || !prop.CanWrite) return;
                var enumType = prop.PropertyType;
                var names = Enum.GetNames(enumType);
                var pick = names.FirstOrDefault(n => n.Equals(prefix + "Trigger", StringComparison.OrdinalIgnoreCase))
                           ?? names.FirstOrDefault(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                           ?? names.FirstOrDefault();
                if (pick != null) prop.SetValue(info, Enum.Parse(enumType, pick));
            }
            catch { }
        }

        /// <summary>判断某触发器是否为「每日」类型（跨版本）。</summary>
        private static bool IsDaily(MediaBrowser.Model.Tasks.TaskTriggerInfo info)
        {
            try
            {
                var prop = info.GetType().GetProperty("Type");
                if (prop == null) return false;
                var v = prop.GetValue(info);
                return v != null && v.ToString().StartsWith("Daily", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>取 worker 上的触发器列表，兼容数组与集合两种返回类型。</summary>
        private static List<MediaBrowser.Model.Tasks.TaskTriggerInfo> TriggerList(MediaBrowser.Model.Tasks.IScheduledTaskWorker worker)
        {
            var list = new List<MediaBrowser.Model.Tasks.TaskTriggerInfo>();
            try
            {
                var raw = worker?.Triggers;
                if (raw == null) return list;
                foreach (var t in (IEnumerable<MediaBrowser.Model.Tasks.TaskTriggerInfo>)raw)
                {
                    if (t != null) list.Add(t);
                }
            }
            catch { }
            return list;
        }

        /// <summary>写回触发器，兼容 10.9 的数组类型与之后的集合类型。</summary>
        private static void SetTriggers(MediaBrowser.Model.Tasks.IScheduledTaskWorker worker, List<MediaBrowser.Model.Tasks.TaskTriggerInfo> list)
        {
            var prop = worker.GetType().GetProperty("Triggers");
            if (prop == null || !prop.CanWrite) return;
            var target = prop.PropertyType;
            if (target.IsArray)
            {
                var elem = target.GetElementType();
                var arr = Array.CreateInstance(elem, list.Count);
                for (var i = 0; i < list.Count; i++) arr.SetValue(list[i], i);
                prop.SetValue(worker, arr);
            }
            else
            {
                prop.SetValue(worker, list);
            }
        }

        /// <summary>按任务键名找到调度器里的 worker（用于读写触发器）。</summary>
        private MediaBrowser.Model.Tasks.IScheduledTaskWorker FindWorker()
        {
            var list = _scheduler.ScheduledTasks;
            if (list == null) return null;
            foreach (var w in list)
            {
                if (w == null) continue;
                if (string.Equals(w.ScheduledTask?.Key, "CollectionStudioScan", StringComparison.OrdinalIgnoreCase)) return w;
                if (string.Equals(w.ScheduledTask?.GetType().Name, "CollectionStudioScan", StringComparison.OrdinalIgnoreCase)) return w;
            }
            return list.FirstOrDefault();
        }



        /// <summary>读取扫描任务的触发器配置。</summary>
        [HttpGet("Tasks/Trigger")]
        public ActionResult GetTaskTrigger()
        {
            try
            {
                var worker = FindWorker();
                if (worker == null) return Error(404, "找不到扫描任务。");
                var current = TriggerList(worker);
                return Ok(new { triggers = current });
            }
            catch (Exception ex)
            {
                return Error(500, ex.Message);
            }
        }

        /// <summary>设置扫描任务的每日运行时间（例 "03:30"）。</summary>
        [HttpPost("Tasks/Trigger")]
        public ActionResult SetTaskTrigger([FromBody] TaskTriggerRequest body)
        {
            var time = body?.DailyTime?.Trim();
            if (string.IsNullOrWhiteSpace(time)) return Error(400, "请提供每日运行时间，例如 03:30。");
            var parts = time.Split(':');
            if (parts.Length < 2) return Error(400, "时间格式应为 HH:mm。");
            int hh, mm;
            try { hh = int.Parse(parts[0]); mm = int.Parse(parts[1]); }
            catch { return Error(400, "时间格式应为 HH:mm。"); }
            if (hh < 0 || hh > 23 || mm < 0 || mm > 59) return Error(400, "时间超出范围。");

            try
            {
                var worker = FindWorker();
                if (worker == null) return Error(404, "找不到扫描任务。");

                var triggers = new List<MediaBrowser.Model.Tasks.TaskTriggerInfo>();
                if (body.DailyEnabled)
                {
                    var daily = new MediaBrowser.Model.Tasks.TaskTriggerInfo
                    {
                        TimeOfDayTicks = TimeSpan.FromHours(hh).Add(TimeSpan.FromMinutes(mm)).Ticks,
                        MaxRuntimeTicks = TimeSpan.FromHours(6).Ticks
                    };
                    SetTriggerType(daily, "Daily");
                    triggers.Add(daily);
                }
                // 保留非每日类触发器（例如启动时触发）
                foreach (var t in TriggerList(worker))
                {
                    if (!IsDaily(t)) triggers.Add(t);
                }

                SetTriggers(worker, triggers);
                worker.ReloadTriggerEvents();
                return Ok(new { ok = true, dailyTime = time, dailyEnabled = body.DailyEnabled, triggers = triggers.Count });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置任务触发时间失败");
                return Error(500, "设置失败：" + ex.Message);
            }
        }

        public sealed class TaskTriggerRequest
        {
            public string DailyTime { get; set; }
            public bool DailyEnabled { get; set; } = true;
        }

        // ---------------------------------------------------------------- 任务

        [HttpGet("Tasks")]
        public ActionResult GetTasks()
        {
            var scan = CollectionStudioTasks.Instance;
            return Ok(new[]
            {
                new
                {
                    id = "scan",
                    name = "扫描媒体库并应用全部启用规则",
                    lastRun = scan?.LastRunAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty,
                    state = scan?.IsRunning == true ? "running" : "idle"
                }
            });
        }

        [HttpPost("Tasks/{id}/Run")]
        public ActionResult RunTask(string id)
        {
            var scan = CollectionStudioTasks.Instance;
            if (scan == null) return Error(500, "定时任务尚未注册，请重启 Jellyfin。");
            if (scan.IsRunning) return Error(409, "任务正在运行，请稍候。");
            _ = Task.Run(() => scan.RunNowAsync(false));
            return Ok(new { ok = true });
        }

        [HttpGet("Tasks/{id}/Log")]
        public ActionResult GetTaskLog(string id, [FromQuery] int lines)
        {
            var scan = CollectionStudioTasks.Instance;
            var count = lines <= 0 ? 200 : Math.Clamp(lines, 10, 2000);
            return Ok(new { lines = scan?.RecentLog(count) ?? Array.Empty<string>() });
        }
    }
}
