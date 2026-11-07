using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.CollectionStudio.Core
{
    /// <summary>
    /// 源名称替换规则引擎。规则文本形如 "模式:替换文本"：
    /// 冒号前为匹配模式（支持正则；不含正则元字符时按字面量匹配），冒号后为替换文本，留空即删除。
    /// 例：源名 "1561684@vvvv-66"，规则 "*@:" 得到 "vvvv-66"。
    /// </summary>
    public static class RenameEngine
    {
        /// <summary>把一条规则文本拆成「模式 + 替换文本」。冒号取第一个未转义的冒号。</summary>
        public static (string pattern, string replacement, bool isRegex) ParseRuleText(string text)
        {
            var raw = (text ?? string.Empty).Trim();
            if (raw.Length == 0) return (string.Empty, string.Empty, false);

            // 支持全角冒号，方便中文输入
            raw = raw.Replace('：', ':');

            var idx = -1;
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] != ':') continue;
                if (i > 0 && raw[i - 1] == '\\') continue;
                idx = i;
                break;
            }

            string pattern, replacement;
            if (idx < 0)
            {
                // 没有冒号：整条当作模式，等效于删除匹配内容
                pattern = raw;
                replacement = string.Empty;
            }
            else
            {
                pattern = raw.Substring(0, idx);
                replacement = raw.Substring(idx + 1);
            }

            return (pattern, replacement, LooksLikeRegex(pattern));
        }

        /// <summary>
        /// 是否按正则处理。含「成对」正则元字符才算正则；
        /// 单独出现的 * @ ? 等按通配/字面处理（例如 "*@:" 是「删除到 @ 为止」的通配写法，不是正则）。
        /// </summary>
        public static bool LooksLikeRegex(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;

            var has = false;
            foreach (var ch in pattern)
            {
                if ("+[]{}()|^$\\".IndexOf(ch) >= 0) { has = true; break; }
            }
            if (has) return true;

            // 有 ? 但没有成组的量词/分组时，仍视为正则（? 是合法的量词）
            if (pattern.Contains("?")) return true;

            // "*" 只有出现在字符之后（作为量词）才像正则；出现在开头或紧跟在 * 后则是通配
            for (var i = 1; i < pattern.Length; i++)
            {
                if (pattern[i] == '*' && pattern[i - 1] != '*') return true;
            }

            return false;
        }

        /// <summary>
        /// 按要求把源名称替换成目标名称。多条规则依次生效，只要有一条命中就返回 true。
        /// 未命中任何规则时返回原名（changed=false）。
        /// </summary>
        public static (string result, bool changed, string appliedRule) Apply(string source, IEnumerable<RenameRule> rules, string path)
        {
            var current = source ?? string.Empty;
            var changed = false;
            var applied = string.Empty;

            foreach (var rule in rules ?? Enumerable.Empty<RenameRule>())
            {
                if (rule == null || !rule.Enabled) continue;
                if (!string.IsNullOrWhiteSpace(rule.OnlyIfPathContains)
                    && !string.Equals(rule.OnlyIfPathContains.Trim(), "*", StringComparison.Ordinal)
                    && !(path ?? string.Empty).Contains(rule.OnlyIfPathContains.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (pattern, replacement, isRegex) = ParseRuleText(rule.Pattern);
                if (string.IsNullOrEmpty(pattern)) continue;

                var next = ReplaceOne(current, pattern, replacement, isRegex, out var hit);
                if (!hit) continue;
                if (next == current) continue;

                current = next;
                changed = true;
                applied = rule.Pattern;
            }

            return (current, changed, applied);
        }

        /// <summary>单条替换。字面量模式按「全部出现」替换，正则模式按正则替换。</summary>
        public static string ReplaceOne(string input, string pattern, string replacement, bool isRegex, out bool hit)
        {
            hit = false;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(pattern)) return input;

            // 通配写法（以 * 开头）：星号代表「任意前缀」，把到该字面量为止（含）的内容替换掉。
            // 规则 "*@:" 语义 = 删除最后一个 @ 及其之前的所有内容，保留其后文本。
            if (!isRegex && pattern.StartsWith("*", StringComparison.Ordinal))
            {
                var core = pattern.TrimStart('*');
                if (core.Length > 0)
                {
                    var pos = input.LastIndexOf(core, StringComparison.OrdinalIgnoreCase);
                    if (pos >= 0)
                    {
                        hit = true;
                        return replacement + input.Substring(pos + core.Length);
                    }
                }
                return input;
            }

            try
            {
                if (isRegex)
                {
                    var re = new Regex(pattern, RegexOptions.None);
                    if (re.IsMatch(input))
                    {
                        hit = true;
                        return re.Replace(input, replacement ?? string.Empty);
                    }
                    return input;
                }

                if (input.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    hit = true;
                    return input.Replace(pattern, replacement ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                }
                return input;
            }
            catch (ArgumentException)
            {
                // 正则写法非法时退化为字面量替换，绝不抛出中断流程
                if (input.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    hit = true;
                    return input.Replace(pattern, replacement ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                }
                return input;
            }
        }
    }
}
