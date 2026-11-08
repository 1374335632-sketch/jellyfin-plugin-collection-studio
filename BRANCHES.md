# 分支说明

本仓库按 Jellyfin 版本线划分分支。**不同分支只发布对应产品线的安装包与清单**，源码与工具在 `main`。

| 分支 | 适用 Jellyfin | 清单文件名 | 清单地址 |
|---|---|---|---|
| `main` | 全部（三份清单 + 全部版本包） | `manifest-12.json` / `manifest-10.json` / `manifest-109.json` | 见下 |
| `jellyfin-12` | 12.0 ~ 12.4 | `manifest.json` | `https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/jellyfin-12/manifest.json` |
| `jellyfin-10` | 10.10 / 10.11 | `manifest.json` | `https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/jellyfin-10/manifest.json` |
| `jellyfin-109` | 10.9 | `manifest.json` | `https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/jellyfin-109/manifest.json` |

## main 分支的三份清单

| Jellyfin 版本 | 清单地址 |
|---|---|
| 12.4 | `https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/main/manifest-12.json` |
| 10.10 / 10.11 | `https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/main/manifest-10.json` |
| 10.9 | `https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/main/manifest-109.json` |

## 该用哪个

**一般情况用 `main`** —— 一份清单含该产品线全部版本，升级和回退都在同一个存储库里完成。

**用专用分支的场景**：

- 想给存储库起个更清晰的名字（如「合集工坊 12.x」）
- 服务器上只需要一个产品线，不想让清单里出现其他版本的包
- 用的是 `jellyfin-12` 这类分支，清单文件名统一为 `manifest.json`，URL 更短

## 缓存

gh-proxy 与 Cloudflare 各有一层 60 秒缓存。刷新后看不到新版本时，URL 末尾加时间戳：

```
...?v=1794046000
```

或使用插件自带的清单代理（1.8 以上）：

```
http://你的服务器:8096/CollectionStudio/Manifest
```

它每次自动带时间戳，并走服务器本地网络。

## 版本号与 targetAbi 映射

固定规则，三个产品线独立编号：

| 产品线 | 目标框架 | targetAbi | 当前版本 |
|---|---|---|---|
| Jellyfin 12.x | `net10.0` | `12.4.0.0` | 5.1.0.2 |
| Jellyfin 10.10 / 10.11 | `net9.0` | `10.11.0.0` | 5.1.0.1 |
| Jellyfin 10.9 | `net8.0` | `10.9.0.0` | 5.1.0.0 |

第三段数字与产品线绑定（`.2`=12.x、`.1`=10.11、`.0`=10.9），便于一眼识别。

## 其他文档

- [README](../README.md) — 总览、安装、功能、故障排查
- [docs/VERSIONS.md](../docs/VERSIONS.md) — 逐个版本的更新日志与能力差异
- [docs/INSTALL.md](../docs/INSTALL.md) — 安装与配置详解、Docker 示例
- [docs/API_CONTRACT.md](../docs/API_CONTRACT.md) — 接口约定
