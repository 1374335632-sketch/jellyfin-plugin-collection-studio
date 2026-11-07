using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CollectionStudio.Core;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CollectionStudio
{
    /// <summary>注册插件服务（控制器与任务单例）。</summary>
    public sealed class ServiceRegistrator : MediaBrowser.Controller.Plugins.IPluginServiceRegistrator
    {
        public void RegisterServices(Microsoft.Extensions.DependencyInjection.IServiceCollection serviceCollection,
            MediaBrowser.Controller.IServerApplicationHost applicationHost)
        {
            // 控制器通过 ApiController 属性自动发现；这里只保证存储工厂可用。
        }
    }

    /// <summary>定时任务：扫描媒体库 + 应用所有启用的规则。</summary>
    public sealed class CollectionStudioTasks : IScheduledTask
    {
        private const int MaxLogLines = 500;
        private readonly ConcurrentQueue<string> _log = new ConcurrentQueue<string>();
        private readonly Func<Store> _storeFactory;

        public CollectionStudioTasks(ILibraryManager library, ICollectionManager collections,
            IProviderManager providers, IItemRepository repo, ILogger<CollectionStudioTasks> logger)
        {
            Library = library;
            Collections = collections;
            Providers = providers;
            Repo = repo;
            Logger = logger;
            _storeFactory = () => Plugin.OpenStore();
            Instance = this;
        }

        public static CollectionStudioTasks? Instance { get; private set; }

        public ILibraryManager Library { get; }
        public ICollectionManager Collections { get; }
        public IProviderManager Providers { get; }
        public IItemRepository Repo { get; }
        public ILogger Logger { get; }

        public volatile bool IsRunning;
        public DateTime? LastRunAt;

        public string Name => "Collection Studio：扫描媒体库并应用规则";
        public string Key => "CollectionStudioScan";
        public string Description => "扫描媒体库索引，并按已启用的分类规则生成/更新合集。";
        public string Category => "Collection Studio";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
            => RunNowAsync(true, progress, cancellationToken);

        public async Task RunNowAsync(bool scheduled, IProgress<double> progress = null, CancellationToken cancellationToken = default)
        {
            if (IsRunning) return;
            IsRunning = true;
            try
            {
                AddLog(scheduled ? "定时任务开始执行。" : "手动触发任务开始执行。");
                using var store = _storeFactory();
                var svc = new CollectionService(Library, Collections, Providers, Repo, Logger, _storeFactory);
                var scanned = svc.ScanAll(progress != null ? new Progress<double>(p => progress.Report(p * 0.4)) : null, cancellationToken);
                AddLog("扫描完成：影片 " + scanned.scanned + " 部，人物条目 " + scanned.people + " 条。");

                var rules = store.Rules.FindAll().Where(r => r.Enabled).ToList();
                AddLog("共 " + rules.Count + " 条启用规则。");
                var idx = 0;
                foreach (var rule in rules)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var result = await svc.ApplyAsync(rule, store, cancellationToken).ConfigureAwait(false);
                        foreach (var line in result.Log) AddLog("[" + rule.Name + "] " + line);
                        AddLog("[" + rule.Name + "] 完成：新建 " + result.Created + "，更新 " + result.Updated + "，跳过 " + result.Skipped + "。");
                    }
                    catch (Exception ex)
                    {
                        AddLog("[" + rule.Name + "] 执行失败：" + ex.Message);
                        Logger.LogError(ex, "规则 {Rule} 执行失败", rule.Name);
                    }
                    idx++;
                    progress?.Report(40 + 60.0 * idx / Math.Max(1, rules.Count));
                }
                LastRunAt = DateTime.Now;
                AddLog("任务结束，共用时见日志时间。");
            }
            catch (OperationCanceledException)
            {
                AddLog("任务被取消。");
            }
            catch (Exception ex)
            {
                AddLog("任务失败：" + ex.Message);
                Logger.LogError(ex, "Collection Studio 任务失败");
            }
            finally
            {
                IsRunning = false;
            }
        }

        private void AddLog(string line)
        {
            _log.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + line);
            while (_log.Count > MaxLogLines) _log.TryDequeue(out _);
        }

        public string[] RecentLog(int take)
        {
            var items = _log.ToArray();
            var start = Math.Max(0, items.Length - take);
            return items.Skip(start).ToArray();
        }
    }
}
