using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Jellyfin.Plugin.CollectionStudio.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CollectionStudio
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly IApplicationPaths _paths;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _paths = applicationPaths;
            Logger = logger;
            Xml = xmlSerializer;

            // 手动安装（直接解压 dll）时目录里可能没有 meta.json，
            // 而 Jellyfin 缺少 manifest 就不会加载、也无法禁用/卸载该插件。
            // 这里在加载成功后补写一份，保证插件在控制台里始终可管理。
            EnsureManifest();
        }

        private void EnsureManifest()
        {
            try
            {
                var folder = Path.GetDirectoryName(AssemblyFilePath ?? string.Empty);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { return; }
                var manifestPath = Path.Combine(folder, "meta.json");
                if (File.Exists(manifestPath)) { return; }

                var payload = new
                {
                    category = "General",
                    changelog = "自动补写的清单文件。",
                    description = Description,
                    guid = Id.ToString("N"),
                    name = Name,
                    overview = "Collection Studio for Jellyfin",
                    owner = "合集工坊",
                    targetAbi = "0.0.0.0",
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    version = Version.ToString(),
                    status = "Active",
                    autoUpdate = false,
                    imagePath = "",
                    assemblies = Array.Empty<string>()
                };
                File.WriteAllText(manifestPath, System.Text.Json.JsonSerializer.Serialize(payload,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Logger?.LogInformation("已补写插件清单：{Path}", manifestPath);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning("补写插件清单失败：{Message}", ex.Message);
            }
        }

        public static Plugin? Instance { get; private set; }

        public static ILogger? Logger { get; private set; }

        public static IXmlSerializer? Xml { get; private set; }

        public override string Name => "合集工坊";

        public override Guid Id => new Guid("c7d1a2b4-9e53-4f18-8a2c-6b3d5e7f9041");

        public override string Description => "按元数据自动生成合集：演员/导演写真封面、高级条件规则、改名对照表、备份还原，全中文界面。";

        public string DataFolder => Path.Combine(_paths.PluginConfigurationsPath, "CollectionStudio");

        public string DbPath
        {
            get
            {
                Directory.CreateDirectory(DataFolder);
                return Path.Combine(DataFolder, "collection-studio.db");
            }
        }

        public string ExportFolder
        {
            get
            {
                var dir = Path.Combine(DataFolder, "exports");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        internal static Store OpenStore() => new Store(Instance?.DbPath ?? Path.Combine(Path.GetTempPath(), "collection-studio.db"));

        /// <summary>
        /// 卸载时清理数据目录。注意：此时 LiteDB 可能仍持有数据库文件句柄，
        /// 因此先释放存储、失败也绝不抛出——否则 Jellyfin 的卸载流程会被中断。
        /// </summary>
        public override void OnUninstalling()
        {
            try
            {
                Store.CloseAll();
            }
            catch (Exception ex)
            {
                Logger?.LogDebug("关闭数据库失败：{Message}", ex.Message);
            }

            try
            {
                if (Directory.Exists(DataFolder))
                {
                    // 数据库文件可能被占用：先删除其余文件，再尽力删除数据库
                    foreach (var file in Directory.GetFiles(DataFolder, "*", SearchOption.AllDirectories))
                    {
                        try { File.Delete(file); } catch { /* 单个文件占用不应中断卸载 */ }
                    }
                    try
                    {
                        Directory.Delete(DataFolder, true);
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning("数据目录未能完全删除（可能仍被占用）：{Message}", ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning("清理插件数据目录失败：{Message}", ex.Message);
            }

            try { base.OnUninstalling(); }
            catch (Exception ex) { Logger?.LogWarning("基类卸载回调失败：{Message}", ex.Message); }
        }

        public IEnumerable<PluginPageInfo> GetPages() => new[]
        {
            new PluginPageInfo
            {
                Name = "CollectionStudio",
                EmbeddedResourcePath = GetType().Namespace + ".web.page.html",
                DisplayName = "合集工坊"
            }
        };
    }
}
