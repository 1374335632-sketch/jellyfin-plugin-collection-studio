using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public override void OnUninstalling()
        {
            try
            {
                if (Directory.Exists(DataFolder)) Directory.Delete(DataFolder, true);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning("清理插件数据目录失败：{Message}", ex.Message);
            }
            base.OnUninstalling();
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
