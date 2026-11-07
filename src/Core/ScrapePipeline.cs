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
        public async Task<RunResult> RunAsync(CsSettings settings, IEnumerable<BaseItem> items, bool dryRun, CancellationToken ct)
        {
            var result = new RunResult { BatchId = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") };
            var rules = settings?.RenameRules ?? new List<RenameRule>();

            if (settings == null || !settings.RenameBeforeScrape)
            {
                result.Log.Add("未启用「刮削前重命名」，已跳过。");
                return result;
            }

            if (rules.Count == 0)
            {
                result.Log.Add("没有配置源名称替换规则，已跳过。");
                return result;
            }

            foreach (var item in items ?? Enumerable.Empty<BaseItem>())
            {
                ct.ThrowIfCancellationRequested();
                if (item == null) continue;
                result.Scanned++;

                var step = new StepResult { ItemId = item.Id.ToString("N"), OriginalName = item.Name ?? string.Empty };
                // 只依据「源名称」重命名，不看文件路径
                var (target, changed, rule) = RenameEngine.Apply(item.Name, rules);
                step.RenamedTo = target;
                step.AppliedRule = rule;
                step.Renamed = changed && !string.IsNullOrWhiteSpace(target) && !string.Equals(target, item.Name, StringComparison.Ordinal);

                if (!step.Renamed)
                {
                    step.Message = "名称未命中任何规则";
                    result.Steps.Add(step);
                    continue;
                }

                result.Renamed++;

                if (dryRun)
                {
                    step.Message = "预演：将改名为「" + target + "」" + (settings.RenamePhysicalFiles ? "，并重命名磁盘文件" : string.Empty);
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
                if (settings.RenamePhysicalFiles)
                {
                    try
                    {
                        step.FileRenamed = RenamePhysicalFile(item, target);
                        if (step.FileRenamed)
                        {
                            result.FilesRenamed++;
                            logDoc.FileMoved = true;
                            logDoc.NewPath = item.Path ?? string.Empty;
                        }
                    }
                    catch (Exception ex)
                    {
                        step.Message = "重命名文件失败：" + ex.Message;
                        _logger?.LogWarning("重命名文件失败 {Name}: {Message}", item.Name, ex.Message);
                    }
                }

                // 2) 写入 Jellyfin 名称（Name 是 MetaTube 识别的主要依据）
                if (settings.RenameWriteName)
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
        private bool RenamePhysicalFile(BaseItem item, string newName)
        {
            var path = item?.Path;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

            var dir = Path.GetDirectoryName(path);
            var ext = Path.GetExtension(path);
            var target = Path.Combine(dir, newName + ext);
            if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase)) return false;
            if (File.Exists(target)) return false;

            // 同目录移动：用 File.Move 即可（DirectoryService 需要宿主注入的 IFileSystem，这里不强依赖）
            File.Move(path, target);

            item.Path = target;
            return true;
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
