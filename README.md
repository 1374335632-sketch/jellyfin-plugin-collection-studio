# 合集工坊 · Collection Studio

Jellyfin 插件：按元数据（演员 / 导演 / 厂商 / 类型 / 年份 / 自定义条件）自动生成合集，
支持从 MetaTube 或自定义刮削源搜刮演员/导演写真做封面，并提供改名对照表、Excel 导出与备份还原。全中文界面。

## 在线安装（推荐）

### Jellyfin 12.x

控制台 → 插件 → 右上「管理存储库」→ 添加：

```
名称：合集工坊
URL ：https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/main/manifest-12.json
```

保存 → 到「目录」页 → 找到「合集工坊」→ 安装 → 重启 Jellyfin。

### Jellyfin 10.10 / 10.11

```
名称：合集工坊（10.x）
URL ：https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/main/manifest-10.json
```

### Jellyfin 10.9

```
名称：合集工坊（10.9）
URL ：https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/main/manifest-109.json
```

## 手动安装

到 [Releases](https://github.com/1374335632-sketch/jellyfin-plugin-collection-studio/releases) 下载对应 zip，
解压到 `<Jellyfin 配置目录>/plugins/CollectionStudio/`（Docker 通常是 `/config/plugins/CollectionStudio/`），重启 Jellyfin。

| Jellyfin | 包名 |
|---|---|
| 12.0 ~ 12.4 | `Jellyfin.Plugin.CollectionStudio-1.0.0.2-net10.0.zip` |
| 10.10 / 10.11 | `Jellyfin.Plugin.CollectionStudio-1.0.0.1-net9.0.zip` |
| 10.9 | `Jellyfin.Plugin.CollectionStudio-1.0.0.0-net8.0.zip` |

## 功能

1. **分类规则**：按演员 / 导演 / 厂商 / 类型 / 年份 / 高级自定义条件生成合集，每个值单独一个合集，可调排序与升降序。
2. **写真封面**：从 MetaTube（GFriends / XsList）或任意自定义刮削源搜刮演员/导演写真，每个合集可自由挑选封面。
3. **高级规则**：16 个元数据字段 × 8 种运算符（等于/不等于/包含/不包含/大于/小于/大于等于/小于等于/开头是），多条件与/或组合。
4. **改名对照表**：路径 / 原始名称 / Jellyfin 名称 / 类型 / 年份 / 所属合集，每 50 条分页，按名称筛选，导出 Excel（下载到电脑或保存到服务器）。
5. **备份与还原**：全部操作落库（LiteDB），删除自动备份，还原时按时间段挑选备份并二次确认。

## 配置

控制台 → 插件 → 合集工坊 → 设置：

- **MetaTube 地址**：形如 `http://192.168.1.2:8080`（不要带 `/v1`）
- **MetaTube Token**：后端开了鉴权才需要
- **演员写真来源 Provider**：默认 `GFriends`
- **自定义刮削源**：URL 模板含 `{name}`，图片字段路径如 `data.images[*]`

> 地址必须是 **Jellyfin 服务器视角** 可达的地址，不是浏览器里的 localhost。

## 兼容性说明

- Jellyfin **12.4 默认关闭 legacy 授权**（`X-Emby-Token` / `api_key` 失效）。本插件前端与图片代理已改用
  `Authorization: MediaBrowser Token="..."` 与 `ApiKey` 查询参数，在 10.x 与 12.x 上均可用。
- 三个包共用同一套源码，仅按目标框架分别编译。

## 从源码构建

```bash
# 需要 .NET SDK 10 与 Node.js
node tools/build-zip.mjs net10.0   # Jellyfin 12.x
node tools/build-zip.mjs net9.0    # Jellyfin 10.10/10.11
node tools/build-zip.mjs net8.0    # Jellyfin 10.9
```

产物在 `dist/`。

## 许可

MIT

## 为什么仓库 URL 用 gh-proxy

Jellyfin 服务器取 `raw.githubusercontent.com` 在国内网络与部分路由器/容器环境里常被阻断
（服务端会报 `The SSL connection could not be established`）。本清单的 URL 统一用
`gh-proxy.com` 前缀，服务器与浏览器都能取到；安装包地址同样走该前缀。

如果你的服务器能直连 GitHub，也可以把 URL 换成：

```
https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/main/manifest-12.json
```

两者内容一致，只是取件通道不同。
