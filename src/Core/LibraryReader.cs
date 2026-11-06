using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;

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
                IndexedAt = DateTime.Now
            };
        }
    }
}
