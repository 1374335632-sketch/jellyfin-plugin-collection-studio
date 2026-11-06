using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.CollectionStudio.Core
{
    /// <summary>MetaTube 刮削客户端：查演员/导演写真，并支持自定义刮削源。</summary>
    public sealed class MetaTubeClient
    {
        private static readonly HttpClient Http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });

        private readonly CsSettings _settings;
        private readonly Func<CsSettings> _settingsAccessor;

        public MetaTubeClient(Func<CsSettings> settingsAccessor)
        {
            _settingsAccessor = settingsAccessor;
            _settings = settingsAccessor();
        }

        public static string NormalizeBase(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;
            var b = baseUrl.Trim().TrimEnd('/');
            if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) b = b.Substring(0, b.Length - 3);
            return b;
        }

        public static HttpClient Client => Http;

        private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp((_settingsAccessor?.Invoke() ?? _settings).MetaTubeTimeoutSeconds, 3, 120));

        /// <summary>按名字搜索演员信息（MetaTube /v1/actors/search）。</summary>
        public async Task<CoverSearchResult> SearchActorAsync(string name, string provider, CancellationToken ct)
        {
            var s = _settingsAccessor();
            var result = new CoverSearchResult { Name = name, Source = "MetaTube/" + (string.IsNullOrWhiteSpace(provider) ? s.MetaTubeActorProvider : provider) };
            var baseUrl = NormalizeBase(s.MetaTubeBaseUrl);
            if (string.IsNullOrEmpty(baseUrl))
            {
                result.Message = "尚未配置 MetaTube 后端地址，请到「设置」页填写。";
                return result;
            }

            var q = name?.Trim() ?? string.Empty;
            if (!q.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // 也允许用户直接贴演员主页地址
            }

            var provName = string.IsNullOrWhiteSpace(provider) ? (string.IsNullOrWhiteSpace(s.MetaTubeActorProvider) ? "GFriends" : s.MetaTubeActorProvider) : provider;
            var url = baseUrl + "/v1/actors/search?q=" + Uri.EscapeDataString(q) + "&provider=" + Uri.EscapeDataString(provName);
            JsonDocument doc;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(Timeout);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(s.MetaTubeToken)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + s.MetaTubeToken.Trim());
                using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    result.Message = "MetaTube 返回 HTTP " + (int)resp.StatusCode + "：" + Truncate(body, 200);
                    return result;
                }
                doc = JsonDocument.Parse(body);
            }
            catch (OperationCanceledException)
            {
                result.Message = "请求 MetaTube 超时（" + Timeout.TotalSeconds + " 秒）。";
                return result;
            }
            catch (Exception ex)
            {
                result.Message = "连接 MetaTube 失败：" + ex.Message;
                return result;
            }

            using (doc)
            {
                JsonElement? best = null;
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        best ??= el;
                        if (ExactName(el, q)) { best = el; break; }
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    best = doc.RootElement;
                }

                if (best == null || best.Value.ValueKind != JsonValueKind.Object)
                {
                    result.Message = "MetaTube 没有搜到「" + q + "」的资料。";
                    return result;
                }

                var actor = best.Value;
                if (actor.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                    result.MatchedName = nm.GetString() ?? q;

                if (actor.TryGetProperty("images", out var imgs))
                {
                    foreach (var url2 in JsonPathLite.ExtractStrings(imgs, string.Empty))
                        Add(result, url2);
                }

                // 详情接口补充写真（部分版本搜索结果不含 images）
                if (result.Candidates.Count == 0 && actor.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    var detailUrl = baseUrl + "/v1/actors/" + Uri.EscapeDataString(idEl.GetString() ?? string.Empty) + "?provider=" + Uri.EscapeDataString(provName);
                    try
                    {
                        using var cts2 = new CancellationTokenSource(Timeout);
                        using var req2 = new HttpRequestMessage(HttpMethod.Get, detailUrl);
                        if (!string.IsNullOrWhiteSpace(s.MetaTubeToken)) req2.Headers.TryAddWithoutValidation("Authorization", "Bearer " + s.MetaTubeToken.Trim());
                        using var resp2 = await Http.SendAsync(req2, cts2.Token).ConfigureAwait(false);
                        if (resp2.IsSuccessStatusCode)
                        {
                            var body2 = await resp2.Content.ReadAsStringAsync(cts2.Token).ConfigureAwait(false);
                            using var doc2 = JsonDocument.Parse(body2);
                            if (doc2.RootElement.TryGetProperty("images", out var imgs2))
                            {
                                foreach (var url3 in JsonPathLite.ExtractStrings(imgs2, string.Empty)) Add(result, url3);
                            }
                        }
                    }
                    catch { /* 详情失败不影响列表结果 */ }
                }
            }

            if (result.Candidates.Count == 0 && string.IsNullOrEmpty(result.Message))
                result.Message = "已匹配到人物，但该刮削源没有返回写真图片。";
            return result;
        }

        /// <summary>按自定义刮削源搜索写真。</summary>
        public async Task<CoverSearchResult> SearchCustomAsync(string name, CustomSource source, CancellationToken ct)
        {
            var result = new CoverSearchResult { Name = name, Source = "自定义/" + (source?.Name ?? "未命名") };
            if (source == null || string.IsNullOrWhiteSpace(source.UrlTemplate))
            {
                result.Message = "该自定义刮削源缺少 URL 模板。";
                return result;
            }

            var url = source.UrlTemplate.Replace("{name}", Uri.EscapeDataString(name?.Trim() ?? string.Empty))
                                       .Replace("{raw}", name?.Trim() ?? string.Empty);
            JsonDocument doc;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(Timeout);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                foreach (var h in source.Headers ?? new List<HeaderPair>())
                {
                    if (!string.IsNullOrWhiteSpace(h.Name)) req.Headers.TryAddWithoutValidation(h.Name.Trim(), h.Value ?? string.Empty);
                }
                using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    result.Message = "自定义刮削源返回 HTTP " + (int)resp.StatusCode + "：" + Truncate(body, 180);
                    return result;
                }
                doc = JsonDocument.Parse(body);
            }
            catch (OperationCanceledException)
            {
                result.Message = "请求自定义刮削源超时。";
                return result;
            }
            catch (Exception ex)
            {
                result.Message = "请求自定义刮削源失败：" + ex.Message;
                return result;
            }

            using (doc)
            {
                var found = string.IsNullOrWhiteSpace(source.ImagesPath)
                    ? JsonPathLite.ImageUrls(doc.RootElement)
                    : JsonPathLite.ExtractStrings(doc.RootElement, source.ImagesPath);
                if (!string.IsNullOrWhiteSpace(source.ImagesPath))
                {
                    var extra = JsonPathLite.ExtractStrings(doc.RootElement, source.ImagesPath);
                    foreach (var e in extra) if (JsonPathLite.LooksLikeImage(e)) found.Add(e);
                }
                foreach (var u in found)
                {
                    var abs = MakeAbsolute(u, source.ImageBaseUrl);
                    if (abs == null) continue;
                    Add(result, abs);
                }
            }

            if (result.Candidates.Count == 0 && string.IsNullOrEmpty(result.Message))
                result.Message = "该刮削源返回了数据，但没有解析到图片地址，请检查「图片字段路径」。";
            return result;
        }

        private static string MakeAbsolute(string url, string imageBase)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var u = url.Trim();
            if (u.StartsWith("//", StringComparison.Ordinal)) return "https:" + u;
            if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return u;
            if (u.StartsWith("/", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(imageBase))
                return imageBase.TrimEnd('/') + u;
            return JsonPathLite.LooksLikeImage(u) && u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? u : null;
        }

        private static void Add(CoverSearchResult result, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var u = url.Trim();
            if (!(u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return;
            if (result.Candidates.Any(c => string.Equals(c.Url, u, StringComparison.OrdinalIgnoreCase))) return;
            result.Candidates.Add(new CoverCandidate
            {
                Url = u,
                ThumbUrl = "/CollectionStudio/Covers/Proxy?url=" + Uri.EscapeDataString(u)
            });
        }

        private static bool ExactName(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("name", out var nm) || nm.ValueKind != JsonValueKind.String) return false;
            var n = nm.GetString() ?? string.Empty;
            return string.Equals(n.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>探测 MetaTube 是否可用。</summary>
        public async Task<bool> PingAsync()
        {
            var s = _settingsAccessor();
            var baseUrl = NormalizeBase(s.MetaTubeBaseUrl);
            if (string.IsNullOrEmpty(baseUrl)) return false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var resp = await Http.GetAsync(baseUrl + "/v1/providers", cts.Token).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }
}
