using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CollectionStudio.Core
{
    /// <summary>合集服务：扫描索引、预演规则、落库生成合集、设置封面。</summary>
    public sealed class CollectionService
    {
        private readonly ILibraryManager _library;
        private readonly ICollectionManager _collections;
        private readonly IProviderManager _providers;
        private readonly ILogger _logger;
        private readonly Func<Store> _storeFactory;
        private readonly LibraryReader _reader;

        public CollectionService(ILibraryManager library, ICollectionManager collections, IProviderManager providers,
            MediaBrowser.Controller.Persistence.IItemRepository repo, ILogger logger, Func<Store> storeFactory)
        {
            _library = library;
            _collections = collections;
            _providers = providers;
            _logger = logger;
            _storeFactory = storeFactory;
            _reader = new LibraryReader(library, repo);
        }

        public LibraryReader Reader => _reader;

        /// <summary>全量扫描：把库里的影片与人物写入索引库。</summary>
        public (int scanned, int people) ScanAll(IProgress<double> progress, CancellationToken ct)
        {
            var items = _reader.GetAllScannableItems();
            var docs = new List<IndexItemDoc>(items.Count);
            var peopleCount = 0;
            var total = Math.Max(1, items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var doc = _reader.ToDoc(items[i]);
                    docs.Add(doc);
                    peopleCount += doc.Actors.Length + doc.Directors.Length;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("扫描影片 {Name} 失败：{Message}", items[i]?.Name, ex.Message);
                }
                if (i % 50 == 0) progress?.Report(i * 100.0 / total);
            }

            using var store = _storeFactory();
            store.UpsertItems(docs);
            store.RemoveMissingItems(docs.Select(d => d.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase));
            progress?.Report(100);
            return (docs.Count, peopleCount);
        }

        public PreviewResult Preview(RuleDoc rule, IList<IndexItemDoc> index)
        {
            var result = new PreviewResult { TotalItems = index.Count };
            foreach (var g in RuleEngine.Group(index, rule))
            {
                var list = g.Value;
                var sampleCount = Math.Min(3, list.Count);
                result.Groups.Add(new PreviewGroup
                {
                    Name = g.Key,
                    Count = list.Count,
                    Sample = list.Take(sampleCount).Select(x => new PreviewSample { ItemId = x.ItemId, Name = x.Name, Path = x.Path }).ToList()
                });
            }
            return result;
        }

        public async Task<ApplyResult> ApplyAsync(RuleDoc rule, Store store, CancellationToken ct)
        {
            var result = new ApplyResult();
            var index = store.AllItems();

            if (store.GetSettings().AutoBackupBeforeApply)
            {
                result.BackupId = store.Backup(Plugin.Instance?.Name ?? "Collection Studio", "auto", "执行规则前自动备份：" + rule.Name).Id;
            }

            var groups = RuleEngine.Group(index, rule);
            var existing = store.Collections.FindAll().Where(c => string.Equals(c.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var g in groups)
            {
                ct.ThrowIfCancellationRequested();
                var name = g.Key;
                var items = g.Value;
                var itemGuids = items.Select(x => Guid.TryParse(x.ItemId, out var gg) ? gg : Guid.Empty).Where(g2 => g2 != Guid.Empty).ToList();
                var prior = existing.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                var boxSet = prior == null ? null : ResolveCollection(prior.Id);

                if (boxSet == null)
                {
                    try
                    {
                        boxSet = await _collections.CreateCollectionAsync(new CollectionCreationOptions
                        {
                            Name = name,
                            IsLocked = false,
                            ItemIdList = itemGuids.Select(x => x.ToString("N")).ToList(),
                            UserIds = Array.Empty<Guid>()
                        }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        result.Log.Add("创建合集「" + name + "」失败：" + ex.Message);
                        result.Skipped++;
                        continue;
                    }
                    // 新建分支：文档必须绑定到刚创建的 BoxSet，旧文档（其 BoxSet 已不存在）只继承创建时间
                    var doc = new CollectionDoc { Id = boxSet.Id.ToString("N"), CreatedAt = prior?.CreatedAt ?? DateTime.Now };
                    if (prior != null) { store.Collections.Delete(prior.Id); }
                    doc.Name = name;
                    doc.RuleId = rule.Id;
                    doc.UpdatedAt = DateTime.Now;
                    doc.ManagedBy = rule.Name;
                    doc.ItemIds = itemGuids.Select(x => x.ToString("N")).ToList();
                    doc.ItemCount = itemGuids.Count;
                    store.Collections.Upsert(doc);
                    result.Created++;
                    result.Log.Add("已创建合集「" + name + "」，含 " + itemGuids.Count + " 部影片。");
                }
                else
                {
                    var current = GetMemberIds(boxSet);
                    var toAdd = itemGuids.Where(id => !current.Contains(id)).ToList();
                    var toRemove = current.Where(id => !itemGuids.Contains(id)).ToList();
                    if (toAdd.Count > 0) await _collections.AddToCollectionAsync(boxSet.Id, toAdd).ConfigureAwait(false);
                    if (toRemove.Count > 0) await _collections.RemoveFromCollectionAsync(boxSet.Id, toRemove).ConfigureAwait(false);

                    var doc = prior ?? new CollectionDoc { Id = boxSet.Id.ToString("N"), CreatedAt = DateTime.Now };
                    doc.Name = name;
                    doc.RuleId = rule.Id;
                    doc.UpdatedAt = DateTime.Now;
                    doc.ManagedBy = rule.Name;
                    doc.ItemIds = itemGuids.Select(x => x.ToString("N")).ToList();
                    doc.ItemCount = itemGuids.Count;
                    store.Collections.Upsert(doc);
                    if (toAdd.Count + toRemove.Count > 0)
                    {
                        result.Updated++;
                        result.Log.Add("已更新合集「" + name + "」：新增 " + toAdd.Count + "，移除 " + toRemove.Count + "。");
                    }
                    else
                    {
                        result.Log.Add("合集「" + name + "」内容未变化。");
                    }
                }

                await ApplyCoverAsync(rule, name, boxSet ?? ResolveCollectionByDocId(store, name), items, ct).ConfigureAwait(false);
            }

            // 规则范围外、且由本规则管理的旧合集：保留但标记跳过
            foreach (var stale in existing.Where(c => !groups.Any(g => string.Equals(g.Key, c.Name, StringComparison.OrdinalIgnoreCase))))
            {
                result.Skipped++;
                result.Log.Add("合集「" + stale.Name + "」不在当前规则结果内，已保留（如需清理请手动删除）。");
            }

            return result;
        }

        private BaseItem ResolveCollection(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (!Guid.TryParseExact(id, "N", out var guid) && !Guid.TryParse(id, out guid)) return null;
            return _library.GetItemById(guid);
        }

        private BaseItem ResolveCollectionByDocId(Store store, string name)
        {
            var doc = store.Collections.FindOne(c => c.Name == name);
            return doc == null ? null : ResolveCollection(doc.Id);
        }

        /// <summary>读取合集成员：优先走库查询（新集合的 LinkedChildren 缓存可能是旧的）。</summary>
        public HashSet<Guid> GetMemberIds(BaseItem boxSet)
        {
            var set = new HashSet<Guid>();
            if (boxSet == null) return set;
            try
            {
                var query = new InternalItemsQuery
                {
                    ParentId = boxSet.Id,
                    Recursive = true,
                    IsVirtualItem = false,
                    EnableTotalRecordCount = true
                };
                foreach (var child in _library.GetItemList(query, false))
                {
                    if (child != null) set.Add(child.Id);
                }
            }
            catch { }
            if (set.Count == 0 && boxSet is Folder folder && folder.LinkedChildren != null)
            {
                foreach (var lc in folder.LinkedChildren)
                {
                    if (lc == null) continue;
                    if (lc.ItemId.HasValue) { set.Add(lc.ItemId.Value); continue; }
                    if (string.IsNullOrEmpty(lc.Path)) continue;
                    try
                    {
                        var found = _library.FindByPath(lc.Path, false);
                        if (found != null) set.Add(found.Id);
                    }
                    catch { }
                }
            }
            return set;
        }

        private async Task ApplyCoverAsync(RuleDoc rule, string collectionName, BaseItem boxSet, List<IndexItemDoc> items, CancellationToken ct)
        {
            if (boxSet == null) return;
            string url = null;
            switch ((rule.CoverMode ?? "firstItem").ToLowerInvariant())
            {
                case "url":
                    url = rule.CoverUrl;
                    break;
                case "scraped":
                    var client = new MetaTubeClient(() => Plugin.Instance == null ? new CsSettings() : LoadSettings());
                    try
                    {
                        var res = await client.SearchActorAsync(collectionName, null, ct).ConfigureAwait(false);
                        url = res.Candidates.FirstOrDefault()?.Url;
                    }
                    catch { }
                    break;
                case "firstItem":
                    var first = items.FirstOrDefault();
                    if (first != null && Guid.TryParse(first.ItemId, out var gid))
                    {
                        try
                        {
                            var src = _library.GetItemById(gid);
                            if (src != null && src.HasImage(ImageType.Primary, 0))
                            {
                                var path = src.GetImagePath(ImageType.Primary, 0);
                                if (!string.IsNullOrEmpty(path))
                                {
                                    await _providers.SaveImage(boxSet, path, ImageType.Primary, 0, ct).ConfigureAwait(false);
                                    return;
                                }
                            }
                            if (src != null)
                            {
                                var tag = src.GetImageInfo(ImageType.Primary, 0);
                                if (tag != null && !string.IsNullOrEmpty(tag.Path))
                                {
                                    var parentTag = _providers.SaveImage(boxSet, tag.Path, ImageType.Primary, 0, ct);
                                    await parentTag.ConfigureAwait(false);
                                    return;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug("取第一个影片封面失败：{Message}", ex.Message);
                        }
                    }
                    break;
                default:
                    return;
            }

            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                await _providers.SaveImage(boxSet, url.Trim(), ImageType.Primary, 0, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("设置合集封面失败：{Message}", ex.Message);
            }
        }

        private static CsSettings LoadSettings()
        {
            try
            {
                using var store = Plugin.OpenStore();
                return store.GetSettings();
            }
            catch { return new CsSettings(); }
        }

        /// <summary>从服务端覆盖合集封面（使用已选中的图片地址）。</summary>
        public async Task<bool> SetCoverAsync(BaseItem boxSet, string url, CancellationToken ct)
        {
            if (boxSet == null || string.IsNullOrWhiteSpace(url)) return false;
            await _providers.SaveImage(boxSet, url.Trim(), ImageType.Primary, 0, ct).ConfigureAwait(false);
            return true;
        }
    }
}
