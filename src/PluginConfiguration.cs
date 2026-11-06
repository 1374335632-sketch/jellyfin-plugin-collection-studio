using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CollectionStudio
{
    /// <summary>插件级配置（界面主要在插件自己的设置页，这里只做保留项）。</summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string Version { get; set; } = "1.0.0";

        public bool FirstRunCompleted { get; set; }
    }
}
