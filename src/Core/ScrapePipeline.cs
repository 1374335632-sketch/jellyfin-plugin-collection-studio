using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CollectionStudio.Core
{
    /// <summary>
    /// 「先重命名、再刮削」的执行器。
    /// 顺序在代码里写死为 重命名 → 写入 Jellyfin 名称 → 触发 MetaTube 刮削，不可调换。
    /// </summary>
    public sealed class ScrapePipeline
    {
        private readonly ILibraryManager _library;
        private readonly IProviderManager _providers;
        private readonly ILogger _logger;

        public ScrapePipeline(ILibraryManager library, IProviderManager providers, ILogger logger)
        {
            _library = library;
            _providers = providers;
            _logger = logger;
        }

        public sealed class StepResult
        {
            public string ItemId { get; set; } = string.Empty;
            /// <summary>磁盘上的实际文件名（不含扩展名）——重命名的来源。</summary>
            public string SourceFileName { get; set; } = string.Empty;
            /// <summary>Jellyfin 里的影片名称（重命名的写入目标）。</summary>
            public string OriginalName { get; set; } = string.Empty;
            public string RenamedTo { get; set; } = string.Empty;
            public string AppliedRule { get; set; } = string.Empty;
            public bool Renamed { get; set; }
            public bool FileRenamed { get; set; }
            public bool ScrapeQueued { get; set; }
            public string Message { get; set; } = string.Empty;
        }

        public sealed class RunResult
        {
            public int Scanned { get; set; }
            public int Renamed { get; set; }
            public int FilesRenamed { get; set; }
            public int Scraped { get; set; }
            public string BatchId { get; set; } = string.Empty;
            public List<RenameLogDoc> RenameLogs { get; set; } = new List<RenameLogDoc>();
            public List<StepResult> Steps { get; set; } = new List<StepResult>();
            public List<string> Log { get; set; } = new List<string>();
        }

        /// <summary>
        /// 按规则重命名并刮削。dryRun=true 时只计算不落盘、不刮削。
        /// </summary>
        /// <summary>执行模式：重命名、刮削，或两者依次执行。</summary>
        public enum RunMode
        {
            RenameOnly,
            ScrapeOnly,
            RenameThenScrape
        }

        public async Task<RunResult> RunAsync(CsSettings settings, IEnumerable<BaseItem> items, bool dryRun, CancellationToken ct, RunMode mode = RunMode.RenameThenScrape)
        {
            var result = new RunResult { BatchId = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") };
            var rules = settings?.RenameRules ?? new List<RenameRule>();

            var doRename = mode == RunMode.RenameOnly || mode == RunMode.RenameThenScrape;
            var doScrape = mode == RunMode.ScrapeOnly || mode == RunMode.RenameThenScrape;

            if (doRename && rules.Count == 0)
            {
                result.Log.Add("没有配置源名称替换规则，重命名已跳过。");
                doRename = false;
            }

            foreach (var item in items ?? Enumerable.Empty<BaseItem>())
            {
                ct.ThrowIfCancellationRequested();
                if (item == null) continue;
                result.Scanned++;

                // 源名称 = 媒体库影片的实际文件地址（磁盘文件名，不含扩展名）
                var diskName = item.FileNameWithoutExtension;
                if (string.IsNullOrWhiteSpace(diskName))
                {
                    try { diskName = System.IO.Path.GetFileNameWithoutExtension(item.Path ?? string.Empty); } catch { diskName = string.Empty; }
                }
                var step = new StepResult
                {
                    ItemId = item.Id.ToString("N"),
                    SourceFileName = diskName ?? string.Empty,
                    OriginalName = item.Name ?? string.Empty
                };
                var (target, changed, rule) = RenameEngine.Apply(diskName, rules);
                step.RenamedTo = target;
                step.AppliedRule = rule;
                // 改写的是 Jellyfin 名称，因此与当前 Jellyfin 名称比较
                step.Renamed = changed && !string.IsNullOrWhiteSpace(target) && !string.Equals(target, item.Name, StringComparison.Ordinal);

                if (!step.Renamed)
                {
                    step.Message = doRename ? "文件名未命中任何规则" : "未改名";
                    // 只做刮削时仍要处理该条目
                    if (doScrape)
                    {
                        try
                        {
                            QueueMetadataRefresh(item);
                            step.ScrapeQueued = true;
                            result.Scraped++;
                            step.Message = mode == RunMode.ScrapeOnly ? "已排队刮削" : "未改名，仅排队刮削";
                        }
                        catch (Exception ex) { step.Message = "触发刮削失败：" + ex.Message; }
                    }
                    result.Steps.Add(step);
                    continue;
                }

                result.Renamed++;

                if (dryRun)
                {
                    // 预演时把「接下来会做什么」逐项写清楚，包括是否改磁盘文件
                    var plan = "预演：Jellyfin 名称 →「" + target + "」";
                    if (settings.RenameWriteName) { plan += "；写入名称：是"; } else { plan += "；写入名称：否"; }
                    if (settings.RenamePhysicalFiles)
                    {
                        var diskNow = item.FileNameWithoutExtension ?? string.Empty;
                        plan += "；磁盘文件：" + diskNow + " → " + target;
                    }
                    else
                    {
                        plan += "；磁盘文件：不改";
                    }
                    step.Message = plan;
                    result.Steps.Add(step);
                    continue;
                }

                // 记录可复原信息：原名/原路径与新名/新路径
                var logDoc = new RenameLogDoc
                {
                    BatchId = result.BatchId,
                    ItemId = item.Id.ToString("N"),
                    OldName = step.OriginalName,
                    NewName = target,
                    OldPath = item.Path ?? string.Empty,
                    NewPath = item.Path ?? string.Empty,
                    NameChanged = settings.RenameWriteName
                };

                // 1) 先重命名磁盘文件（可选）。必须在刮削之前完成，否则 MetaTube 仍按旧文件名识别。
                if (doRename && settings.RenamePhysicalFiles)
                {
                    string moveError;
                    string movedPath = string.Empty;
                    try { moveError = RenamePhysicalFile(item, target, out movedPath); }
                    catch (Exception ex) { moveError = "移动文件异常：" + ex.Message; }

                    if (moveError == null)
                    {
                        step.FileRenamed = true;
                        result.FilesRenamed++;
                        logDoc.FileMoved = true;
                        logDoc.NewPath = movedPath;
                    }
                    else
                    {
                        step.Message = moveError;
                        result.Log.Add("「" + step.SourceFileName + "」未改磁盘文件：" + moveError);
                        _logger?.LogWarning("重命名磁盘文件失败 {Name}: {Reason}", item.Name, moveError);
                    }
                }

                // 2) 写入 Jellyfin 名称（Name 是 MetaTube 识别的主要依据）
                if (doRename && settings.RenameWriteName)
                {
                    try
                    {
                        item.Name = target;
                        if (!string.IsNullOrWhiteSpace(item.OriginalTitle)) item.OriginalTitle = target;
                        await _library.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        step.Message = "写入名称失败：" + ex.Message;
                        _logger?.LogWarning("写入名称失败 {Name}: {Message}", item.Name, ex.Message);
                        result.Steps.Add(step);
                        continue;
                    }
                }

                // 3) 重命名完成后才触发刮削（顺序由上面的代码位置保证）
                if (doScrape)
                {
                    try
                    {
                        QueueMetadataRefresh(item);
                        step.ScrapeQueued = true;
                        result.Scraped++;
                    }
                    catch (Exception ex)
                    {
                        step.Message = "触发刮削失败：" + ex.Message;
                        _logger?.LogWarning("触发刮削失败 {Name}: {Message}", item.Name, ex.Message);
                    }
                }

                if (string.IsNullOrEmpty(step.Message))
                {
                    step.Message = "已重命名为「" + target + "」并已排队刮削";
                }
                result.Steps.Add(step);
                result.RenameLogs.Add(logDoc);
            }

            return result;
        }

        /// <summary>重命名磁盘上的媒体文件（同目录移动，避免跨盘问题）。</summary>
        /// <summary>重命名磁盘上的媒体文件。返回 null 表示成功，否则返回失败原因（用于展示给用户）。</summary>
        private string RenamePhysicalFile(BaseItem item, string newName, out string newPath)
        {
            newPath = string.Empty;
            var path = item?.Path;
            if (string.IsNullOrWhiteSpace(path)) return "该条目没有磁盘路径（可能是虚拟条目或仅存在于数据库）";
            if (!File.Exists(path))
            {
                // 路径存在但文件不在：给出更具体的原因，避免静默失败
                var ext0 = Path.GetExtension(path);
                return "磁盘上找不到该文件：" + path + "（可能是远程挂载未就绪、文件已被移走，或扩展名不匹配：" + (string.IsNullOrEmpty(ext0) ? "无" : ext0) + "）";
            }

            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return "所在目录不可访问：" + (dir ?? string.Empty);

            var ext = Path.GetExtension(path);
            var target = Path.Combine(dir, newName + ext);
            if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase)) return "目标与当前路径相同，无需改名";
            if (File.Exists(target)) return "目标文件已存在，未覆盖：" + target;

            try
            {
                File.Move(path, target);
            }
            catch (Exception ex)
            {
                return "移动文件失败：" + ex.GetType().Name + " - " + ex.Message;
            }

            try { item.Path = target; } catch { }
            newPath = target;
            return null;
        }


        /// <summary>
        /// 触发元数据刷新。MetadataRefreshOptions 位于服务端程序集，这里用反射构造，
        /// 以便在 10.9 / 10.11 / 12.4 上都能工作，且强制要求「先重命名后刮削」。
        /// </summary>
        private void QueueMetadataRefresh(BaseItem item)
        {
            var options = TryCreateRefreshOptions();
            if (options == null) throw new InvalidOperationException("无法构造 MetadataRefreshOptions");

            _providers.QueueRefresh(item.Id, options, RefreshPriority.High);
        }

        private MetadataRefreshOptions TryCreateRefreshOptions()
        {
            try
            {
                var asm = typeof(BaseItem).Assembly;
                var type = asm.GetType("MediaBrowser.Controller.Entities.MetadataRefreshOptions")
                           ?? AppDomain.CurrentDomain.GetAssemblies()
                               .Select(a => a.GetType("MediaBrowser.Controller.Entities.MetadataRefreshOptions"))
                               .FirstOrDefault(t => t != null);
                if (type == null) return null;

                var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault();
                var instance = ctor == null ? Activator.CreateInstance(type) : ctor.Invoke(new object[ctor.GetParameters().Length]);
                if (instance == null) return null;

                // 尽量让刷新「全量替换元数据」，保证按新名称重新匹配
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanWrite) continue;
                    var n = prop.Name.ToLowerInvariant();
                    if (prop.PropertyType == typeof(bool) && (n.Contains("replace") || n.Contains("force") || n.Contains("recurse") || n.Contains("all") || n.Contains("full")))
                    {
                        prop.SetValue(instance, true);
                    }
                }
                return instance as MetadataRefreshOptions;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("构造刷新选项失败：{Message}", ex.Message);
                return null;
            }
        }
    }
}
