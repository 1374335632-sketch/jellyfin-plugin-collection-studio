using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.CollectionStudio.Core
{
    /// <summary>规则求值：把索引数据按规则分组，产出「合集名 → 影片列表」。</summary>
    public static class RuleEngine
    {
        public static readonly FieldInfo[] Fields =
        {
            new FieldInfo { Key = "actor", Label = "演员", Kind = "person", Groupable = true, MultiValue = true,
                Ops = new[] { "eq", "ne", "contains", "notcontains", "startswith" } },
            new FieldInfo { Key = "director", Label = "导演", Kind = "person", Groupable = true, MultiValue = true,
                Ops = new[] { "eq", "ne", "contains", "notcontains", "startswith" } },
            new FieldInfo { Key = "studio", Label = Carriers(), Kind = "text", Groupable = true, MultiValue = true,
                Ops = new[] { "eq", "ne", "contains", "notcontains", "startswith" } },
            new FieldInfo { Key = "name", Label = "标题", Kind = "text", Groupable = false, MultiValue = false,
                Ops = new[] { "eq", "ne", "contains", "notcontains", "startswith", "gt", "lt", "ge", "le" } },
            new FieldInfo { Key = "num", Label = "品番", Kind = "text", Groupable = false, MultiValue = false,
                Ops = new[] { "eq", "ne", "contains", "notcontains", "startswith", "gt", "lt", "ge", "le" } },
            new FieldInfo { Key = "year", Label = "发行年份", Kind = "number", Groupable = true, MultiValue = false,
                Ops = new[] { "eq", "ne", "gt", "lt", "ge", "le" } },
            new FieldInfo { Key = "premiereDate", Label = "发行日期", Kind = "date", Groupable = true, MultiValue = false,
                Ops = new[] { "eq", "ne", "gt", "lt", "ge", "le" } },
            new FieldInfo { Key = "genre", Label = "类型", Kind = "list", Groupable = true, MultiValue = true,
                Ops = new[] { "eq", "ne", "contains", "notcontains", "startswith" } },
            new FieldInfo { Key = "tag", Label = "标签", Kind = "list", Groupable = true, MultiValue = true,
                Ops = new[] { "eq", "ne", "contains", "notcontains", "startswith" } },
            new FieldInfo { Key = "rating", Label = "评分", Kind = "number", Groupable = false, MultiValue = false,
                Ops = new[] { "eq", "ne", "gt", "lt", "ge", "le" } },
            new FieldInfo { Key = "runtime", Label = "时长(分钟)", Kind = "number", Groupable = false, MultiValue = false,
                Ops = new[] { "eq", "ne", "gt", "lt", "ge", "le" } },
            new FieldInfo { Key = "officialRating", Label = "官方分级", Kind = "text", Groupable = true, MultiValue = false,
                Ops = new[] { "eq", "ne", "contains", "notcontains" } },
            new FieldInfo { Key = "itemType", Label = "媒体类型", Kind = "text", Groupable = true, MultiValue = false,
                Ops = new[] { "eq", "ne" } },
            new FieldInfo { Key = "path", Label = "路径", Kind = "text", Groupable = false, MultiValue = false,
                Ops = new[] { "contains", "notcontains", "startswith", "eq", "ne" } },
            new FieldInfo { Key = "providerId", Label = "外部ID", Kind = "text", Groupable = false, MultiValue = true,
                Ops = new[] { "eq", "ne", "contains", "notcontains", "startswith" } }
        };

        private static string Carriers() => "厂商(片商)";

        /// <summary>规则未限定媒体库时视为全部媒体库。</summary>
        public static bool InSelectedLibraries(IndexItemDoc item, RuleDoc rule)
        {
            var ids = rule?.LibraryIds;
            if (ids == null || ids.Count == 0) return true;
            var itemLib = item?.LibraryId ?? string.Empty;
            return ids.Any(x => string.Equals(x, itemLib, StringComparison.OrdinalIgnoreCase));
        }

        public static FieldInfo FindField(string key) =>
            Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

        /// <summary>取一条影片在某字段上的字符串值集合；数值/日期字段返回单值。</summary>
        public static List<string> Values(IndexItemDoc item, string field)
        {
            switch ((field ?? string.Empty).ToLowerInvariant())
            {
                case "actor": return item.Actors?.ToList() ?? new List<string>();
                case "director": return item.Directors?.ToList() ?? new List<string>();
                case "studio": return item.Studios?.ToList() ?? new List<string>();
                case "name": return new List<string> { item.Name ?? string.Empty };
                case "originaltitle": return new List<string> { item.OriginalTitle ?? string.Empty };
                case "num": return new List<string> { item.FileName ?? string.Empty, item.Name ?? string.Empty };
                case "year": return new List<string> { item.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty };
                case "premiereDate": return new List<string> { item.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty };
                case "genre": return item.Genres?.ToList() ?? new List<string>();
                case "tag": return item.Tags?.ToList() ?? new List<string>();
                case "rating": return new List<string> { item.Rating.HasValue ? item.Rating.Value.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty };
                case "runtime": return new List<string> { item.RuntimeMinutes.HasValue ? item.RuntimeMinutes.Value.ToString(CultureInfo.InvariantCulture) : string.Empty };
                case "officialrating": return new List<string> { item.OfficialRating ?? string.Empty };
                case "itemtype": return new List<string> { item.Kind ?? string.Empty };
                case "path": return new List<string> { item.Path ?? string.Empty };
                case "providerid": return item.ProviderIds == null ? new List<string>() : item.ProviderIds.Values.ToList();
                default: return new List<string>();
            }
        }

        public static bool Matches(IndexItemDoc item, RuleCondition cond)
        {
            if (cond == null) return true;
            var values = Values(item, cond.Field).Where(v => !string.IsNullOrEmpty(v)).ToList();
            var field = FindField(cond.Field);
            if (field != null && field.MultiValue)
                return values.Any(v => Compare(v, cond.Op, cond.Value));
            var single = values.FirstOrDefault() ?? string.Empty;
            return Compare(single, cond.Op, cond.Value);
        }

        public static bool Compare(string actual, string op, string expected)
        {
            actual ??= string.Empty;
            expected ??= string.Empty;
            switch ((op ?? "eq").ToLowerInvariant())
            {
                case "eq": return string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
                case "ne": return !string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
                case "contains": return actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
                case "notcontains": return expected.Length == 0 || actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0;
                case "startswith": return actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
                case "gt": return NumericCompare(actual, expected, (a, b) => a > b, (a, b) => a.CompareTo(b) > 0);
                case "lt": return NumericCompare(actual, expected, (a, b) => a < b, (a, b) => a.CompareTo(b) < 0);
                case "ge": return NumericCompare(actual, expected, (a, b) => a >= b, (a, b) => a.CompareTo(b) >= 0);
                case "le": return NumericCompare(actual, expected, (a, b) => a <= b, (a, b) => a.CompareTo(b) <= 0);
                default: return false;
            }
        }

        private static bool NumericCompare(string actual, string expected, Func<double, double, bool> numCmp, Func<string, string, bool> strCmp)
        {
            if (double.TryParse(actual.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var a)
                && double.TryParse(expected.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var b))
                return numCmp(a, b);
            var da = TryDate(actual);
            var db = TryDate(expected);
            if (da.HasValue && db.HasValue) return strCmp(da.Value.ToString("yyyyMMddHHmmss"), db.Value.ToString("yyyyMMddHHmmss"));
            return strCmp(actual.Trim(), expected.Trim());
        }

        private static DateTime? TryDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            return null;
        }

        public static bool PassesAllConditions(IndexItemDoc item, IList<RuleCondition> conditions)
        {
            if (conditions == null || conditions.Count == 0) return true;
            var result = Matches(item, conditions[0]);
            for (var i = 1; i < conditions.Count; i++)
            {
                var m = Matches(item, conditions[i]);
                result = (conditions[i].Conjunction ?? "and").ToLowerInvariant() == "or" ? (result || m) : (result && m);
            }
            return result;
        }

        /// <summary>按规则把影片分组。自定义高级规则只有一个组；按人/厂商/年份等按值分组。</summary>
        /// <summary>按规则上的条件过滤（诊断与执行共用）。</summary>
        public static bool PassesConditions(IndexItemDoc item, RuleDoc rule)
            => PassesAllConditions(item, rule?.Conditions ?? new List<RuleCondition>());

        public static List<KeyValuePair<string, List<IndexItemDoc>>> Group(IList<IndexItemDoc> items, RuleDoc rule)
        {
            var filtered = items.Where(i => InSelectedLibraries(i, rule) && PassesAllConditions(i, rule.Conditions)).ToList();
            var groups = new List<KeyValuePair<string, List<IndexItemDoc>>>();
            string groupField;
            switch ((rule.Mode ?? "person").ToLowerInvariant())
            {
                case "person": groupField = rule.PersonType == "Director" ? "director" : "actor"; break;
                case "director": groupField = "director"; break;
                case "actor": groupField = "actor"; break;
                case "studio": groupField = "studio"; break;
                case "genre": groupField = "genre"; break;
                case "tag": groupField = "tag"; break;
                case "year": groupField = "year"; break;
                case "officialrating": groupField = "officialRating"; break;
                case "custom":
                default:
                    groupField = null; break;
            }

            if (groupField == null)
            {
                var name = string.IsNullOrWhiteSpace(rule.TargetName) ? rule.Name : rule.TargetName;
                if (!filtered.Any()) return groups;
                groups.Add(new KeyValuePair<string, List<IndexItemDoc>>(name, Sort(filtered, rule)));
                return groups;
            }

            var map = new Dictionary<string, List<IndexItemDoc>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in filtered)
            {
                foreach (var value in Values(item, groupField))
                {
                    var v = value?.Trim();
                    if (string.IsNullOrEmpty(v)) continue;
                    if (!map.TryGetValue(v, out var list)) { list = new List<IndexItemDoc>(); map[v] = list; }
                    if (!list.Contains(item)) list.Add(item);
                }
            }

            var keys = map.Keys.OrderBy(k => k, StringComparer.CurrentCulture).ToList();
            foreach (var key in keys)
            {
                var list = Sort(map[key], rule);
                if (list.Count < Math.Max(1, rule.MinItems)) continue;
                var name = (rule.TitleTemplate ?? "{value}").Replace("{value}", key).Replace("{field}", groupField).Trim();
                if (string.IsNullOrWhiteSpace(name)) name = key;
                groups.Add(new KeyValuePair<string, List<IndexItemDoc>>(name, list));
            }

            if (rule.MaxCollections > 0 && groups.Count > rule.MaxCollections)
                groups = groups.Take(rule.MaxCollections).ToList();
            return groups;
        }

        public static List<IndexItemDoc> Sort(List<IndexItemDoc> items, RuleDoc rule)
        {
            IEnumerable<IndexItemDoc> ordered;
            switch ((rule.SortBy ?? "premiereDate").ToLowerInvariant())
            {
                case "name": ordered = rule.SortDesc ? items.OrderByDescending(i => i.Name, StringComparer.CurrentCulture) : items.OrderBy(i => i.Name, StringComparer.CurrentCulture); break;
                case "productionyear": ordered = rule.SortDesc ? items.OrderByDescending(i => i.Year ?? 0) : items.OrderBy(i => i.Year ?? 0); break;
                case "datecreated": ordered = rule.SortDesc ? items.OrderByDescending(i => i.IndexedAt) : items.OrderBy(i => i.IndexedAt); break;
                case "runtime": ordered = rule.SortDesc ? items.OrderByDescending(i => i.RuntimeMinutes ?? 0) : items.OrderBy(i => i.RuntimeMinutes ?? 0); break;
                case "rating": ordered = rule.SortDesc ? items.OrderByDescending(i => i.Rating ?? 0) : items.OrderBy(i => i.Rating ?? 0); break;
                default:
                    ordered = rule.SortDesc
                        ? items.OrderByDescending(i => i.PremiereDate ?? DateTime.MinValue).ThenBy(i => i.Name, StringComparer.CurrentCulture)
                        : items.OrderBy(i => i.PremiereDate ?? DateTime.MaxValue).ThenBy(i => i.Name, StringComparer.CurrentCulture);
                    break;
            }
            return ordered.ToList();
        }

        /// <summary>把「发行日期区间」这类复合条件解析成上下界。</summary>
        public static (DateTime? from, DateTime? to) ParseDateRange(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return (null, null);
            var m = Regex.Match(expression, @"(\d{4}(?:-\d{1,2}(?:-\d{1,2})?)?)\s*(~|--|至|到)\s*(\d{4}(?:-\d{1,2}(?:-\d{1,2})?)?)");
            if (!m.Success) return (null, null);
            var a = TryDate(m.Groups[1].Value);
            var b = TryDate(m.Groups[3].Value);
            return (a, b);
        }
    }
}
