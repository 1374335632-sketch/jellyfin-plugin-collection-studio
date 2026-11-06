# 分支说明

本仓库按 Jellyfin 版本线划分分支，**不同分支只发布对应产品线的安装包与清单**：

| 分支 | 适用 Jellyfin | 清单地址 |
|---|---|---|
| `main` | 全部（含三个清单与全部版本包） | 见仓库根 README |
| `jellyfin-12` | 12.0 ~ 12.4 | `https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/jellyfin-12/manifest.json` |
| `jellyfin-10` | 10.10 / 10.11 | `https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/jellyfin-10/manifest.json` |
| `jellyfin-109` | 10.9 | `https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/jellyfin-109/manifest.json` |

各分支的清单文件名统一为 `manifest.json`，只包含该产品线的版本列表。
源码在 `src/`，构建脚本在 `tools/`，与 `main` 一致。
