using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.CollectionStudio.Core
{
    /// <summary>对 Jellyfin 库的只读取数封装：拉取可建合集的影片与人物信息。</summary>
    public sealed class LibraryReader
    {
        private readonly ILibraryManager _library;
        private readonly IItemRepository _repo;

        public LibraryReader(ILibraryManager library, IItemRepository repo)
        {
            _library = library;
            _repo = repo;
        }

        private static readonly BaseItemKind[] ScanKinds =
        {
            BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.MusicVideo
        };

        /// <summary>列出服务器上的媒体库（虚拟文件夹），供规则选择分类范围。</summary>
        public List<LibraryInfo> GetLibraries()
        {
            var result = new List<LibraryInfo>();
            try
            {
                foreach (var vf in _library.GetVirtualFolders() ?? new List<VirtualFolderInfo>())
                {
                    result.Add(new LibraryInfo
                    {
                        Id = vf.ItemId ?? string.Empty,
                        Name = vf.Name ?? string.Empty,
                        CollectionType = vf.CollectionType.HasValue ? vf.CollectionType.Value.ToString() : string.Empty
                    });
                }
            }
            catch { }
            return result.OrderBy(x => x.Name, StringComparer.CurrentCulture).ToList();
        }

        private List<VirtualFolderInfo> _virtualFoldersCache;

        private List<VirtualFolderInfo> VirtualFolders()
        {
            if (_virtualFoldersCache != null) return _virtualFoldersCache;
            try { _virtualFoldersCache = _library.GetVirtualFolders() ?? new List<VirtualFolderInfo>(); }
            catch { _virtualFoldersCache = new List<VirtualFolderInfo>(); }
            return _virtualFoldersCache;
        }

        /// <summary>
        /// 取条目所属的媒体库。Jellyfin 的条目父级链通常是 影片 → 物理目录 → root，
        /// 不含 CollectionFolder，因此优先用路径前缀匹配虚拟文件夹的 Locations；匹配不到再回溯父级。
        /// </summary>
        private (string id, string name) ResolveLibrary(BaseItem item)
        {
            var path = item?.Path ?? string.Empty;
            var folders = VirtualFolders();

            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    var normalized = path.Replace('/', '\\').TrimEnd('\\');
                    foreach (var vf in folders)
                    {
                        foreach (var loc in vf.Locations ?? Array.Empty<string>())
                        {
                            if (string.IsNullOrWhiteSpace(loc)) continue;
                            var root = loc.Replace('/', '\\').TrimEnd('\\');
                            if (normalized.Length >= root.Length &&
                                normalized.Substring(0, root.Length).Equals(root, StringComparison.OrdinalIgnoreCase) &&
                                (normalized.Length == root.Length || normalized[root.Length] == '\\'))
                            {
                                return (vf.ItemId ?? string.Empty, vf.Name ?? string.Empty);
                            }
                        }
                    }
                }
                catch { }
            }

            try
            {
                var current = item;
                for (var guard = 0; guard < 12 && current != null; guard++)
                {
                    if (current is CollectionFolder folder)
                        return (folder.Id.ToString("N"), folder.Name ?? string.Empty);
                    var byId = folders.FirstOrDefault(f => string.Equals(f.ItemId, current.Id.ToString("N"), StringComparison.OrdinalIgnoreCase));
                    if (byId != null) return (byId.ItemId ?? string.Empty, byId.Name ?? string.Empty);
                    current = current.GetParent();
                }
            }
            catch { }
            return (string.Empty, string.Empty);
        }

        public List<BaseItem> GetAllScannableItems()
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = ScanKinds,
                Recursive = true,
                IsVirtualItem = false,
                EnableTotalRecordCount = true
            };
            var result = _library.GetItemList(query, false);
            return result.Where(i => i != null && !i.IsVirtualItem && !string.IsNullOrEmpty(i.Name)).ToList();
        }

        public PersonInfo[] GetPeople(BaseItem item)
        {
            try
            {
                var people = _library.GetPeople(item);
                return people?.ToArray() ?? Array.Empty<PersonInfo>();
            }
            catch
            {
                return Array.Empty<PersonInfo>();
            }
        }

        public IndexItemDoc ToDoc(BaseItem item)
        {
            var people = GetPeople(item);
            var studios = new List<string>();
            try
            {
                if (item.Studios != null) studios.AddRange(item.Studios.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
            }
            catch { }

            var providerIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (item.ProviderIds != null)
                {
                    foreach (var kv in item.ProviderIds) if (!string.IsNullOrWhiteSpace(kv.Value)) providerIds[kv.Key] = kv.Value;
                }
            }
            catch { }

            var library = ResolveLibrary(item);
            var runtime = item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0
                ? (int?)Math.Round(item.RunTimeTicks.Value / 600000000.0)
                : null;

            return new IndexItemDoc
            {
                ItemId = item.Id.ToString("N"),
                Name = item.Name ?? string.Empty,
                OriginalTitle = string.IsNullOrWhiteSpace(item.OriginalTitle) ? (item.Name ?? string.Empty) : item.OriginalTitle,
                Path = item.Path ?? string.Empty,
                Kind = item.GetBaseItemKind().ToString(),
                Year = item.ProductionYear,
                PremiereDate = item.PremiereDate,
                Rating = item.CommunityRating,
                RuntimeMinutes = runtime,
                OfficialRating = item.OfficialRating ?? string.Empty,
                Studios = studios.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Genres = (item.Genres ?? Array.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct().ToArray(),
                Tags = (item.Tags ?? Array.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct().ToArray(),
                Actors = people.Where(p => p.Type == PersonKind.Actor || p.Type == PersonKind.Unknown).Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray(),
                Directors = people.Where(p => p.Type == PersonKind.Director).Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray(),
                ProviderIds = providerIds,
                Container = item.Container ?? string.Empty,
                FileName = item.FileNameWithoutExtension ?? string.Empty,
                LibraryId = library.id,
                LibraryName = library.name,
                IndexedAt = DateTime.Now
            };
        }
    }
}
