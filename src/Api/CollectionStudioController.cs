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

        public CollectionStudioController(ILibraryManager library, ICollectionManager collections,
            IServerApplicationHost host, ILogger<CollectionStudioController> logger)
        {
            _library = library;
            _collections = collections;
            _host = host;
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
