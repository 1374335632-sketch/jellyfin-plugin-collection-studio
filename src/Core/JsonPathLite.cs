using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.CollectionStudio.Core
{
    /// <summary>极简 JSON 路径求值：支持 a.b、a[0]、a[*]、a[*].b 与以 $ 开头的写法。</summary>
    public static class JsonPathLite
    {
        public static List<string> ExtractStrings(JsonElement root, string path)
        {
            var result = new List<string>();
            foreach (var el in Select(root, path)) CollectStrings(el, result, 0);
            return result;
        }

        public static IEnumerable<JsonElement> Select(JsonElement root, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) { yield return root; yield break; }
            var p = path.Trim();
            if (p.StartsWith("$", StringComparison.Ordinal)) p = p.Substring(1);
            if (p.StartsWith(".", StringComparison.Ordinal)) p = p.Substring(1);
            var current = new List<JsonElement> { root };
            foreach (var rawSeg in Split(p))
            {
                var seg = rawSeg.Trim();
                if (seg.Length == 0) continue;
                var wildcard = seg.EndsWith("[*]", StringComparison.Ordinal);
                if (wildcard) seg = seg.Substring(0, seg.Length - 3);
                var next = new List<JsonElement>();
                foreach (var cur in current)
                {
                    if (seg.Length > 0)
                    {
                        if (cur.ValueKind != JsonValueKind.Object) continue;
                        if (!cur.TryGetProperty(seg, out var child)) continue;
                        if (wildcard)
                        {
                            if (child.ValueKind == JsonValueKind.Array) foreach (var x in child.EnumerateArray()) next.Add(x);
                            else next.Add(child);
                        }
                        else next.Add(child);
                    }
                    else
                    {
                        if (cur.ValueKind == JsonValueKind.Array) foreach (var x in cur.EnumerateArray()) next.Add(x);
                        else next.Add(cur);
                    }
                }
                current = next;
            }
            foreach (var el in current) yield return el;
        }

        private static IEnumerable<string> Split(string path)
        {
            var parts = new List<string>();
            var buf = new System.Text.StringBuilder();
            foreach (var ch in path)
            {
                if (ch == '.') { parts.Add(buf.ToString()); buf.Clear(); }
                else buf.Append(ch);
            }
            parts.Add(buf.ToString());
            return parts;
        }

        private static void CollectStrings(JsonElement el, List<string> sink, int depth)
        {
            if (depth > 6) return;
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) sink.Add(s.Trim());
                    break;
                case JsonValueKind.Array:
                    foreach (var x in el.EnumerateArray()) CollectStrings(x, sink, depth + 1);
                    break;
                case JsonValueKind.Object:
                    foreach (var prop in el.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String || prop.Value.ValueKind == JsonValueKind.Array)
                            CollectStrings(prop.Value, sink, depth + 1);
                    }
                    break;
            }
        }

        /// <summary>从任意 JSON 里挑出形如图片地址的字符串。</summary>
        public static List<string> ImageUrls(JsonElement el, int depth = 0, List<string> sink = null)
        {
            sink ??= new List<string>();
            if (depth > 8) return sink;
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && LooksLikeImage(s) && !sink.Contains(s)) sink.Add(s);
                    break;
                case JsonValueKind.Array:
                    foreach (var x in el.EnumerateArray()) ImageUrls(x, depth + 1, sink);
                    break;
                case JsonValueKind.Object:
                    foreach (var p in el.EnumerateObject()) ImageUrls(p.Value, depth + 1, sink);
                    break;
            }
            return sink;
        }

        public static bool LooksLikeImage(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var lower = s.ToLowerInvariant();
            if (!(lower.StartsWith("http://", StringComparison.Ordinal) || lower.StartsWith("https://", StringComparison.Ordinal) || lower.StartsWith("/", StringComparison.Ordinal))) return false;
            return lower.Contains(".jpg") || lower.Contains(".jpeg") || lower.Contains(".png") || lower.Contains(".webp") || lower.Contains(".gif")
                || lower.Contains("image") || lower.Contains("photo") || lower.Contains("cover") || lower.Contains("thumb");
        }
    }
}
