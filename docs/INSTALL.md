# 安装与配置详解

---

## 一、在线安装（推荐）

### 1. 添加存储库

控制台 → 插件 → 管理存储库 → 点「添加」：

| 字段 | 填写 |
|---|---|
| 名称 | Collection Studio |
| URL | 见下表 |

| Jellyfin 版本 | 存储库 URL |
|---|---|
| 12.4 | `https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/main/manifest-12.json` |
| 10.10 / 10.11 | `https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/main/manifest-10.json` |
| 10.9 | `https://gh-proxy.com/https://raw.githubusercontent.com/1374335632-sketch/jellyfin-plugin-collection-studio/main/manifest-109.json` |

### 2. 安装插件

保存后进入「目录」页，找到 **Collection Studio**，点「安装」选择版本。

版本下拉会列出该产品线所有版本（当前 15 个）。推荐 **5.1.0.2**（12.4）。

### 3. 重启 Jellyfin

安装完成后必须重启，插件才会加载。

### 4. 打开设置页

插件列表 → Collection Studio → 点「设置」。

页面顶部会显示一行状态：

- 绿色「初始化成功 · 已绑定按钮 N 个」= 一切正常
- 红色「初始化失败：…」= 有错误，看提示

---

## 二、为什么用 gh-proxy 前缀

`raw.githubusercontent.com` 在国内网络常被阻断，Jellyfin 服务器用 .NET `HttpClient` 请求时会直接报：

```
System.Net.Http.HttpRequestException: The SSL connection could not be established
```

`gh-proxy.com` 是可达的镜像前缀。如果你的服务器能直连 GitHub，把前缀去掉也能用，内容完全一致。

### 缓存问题

gh-proxy 和 Cloudflare 各有一层 60 秒缓存。刷新后看不到新版本时：

1. 在 URL 末尾加时间戳：`...manifest-12.json?v=1794046000`
2. 或删掉存储库重新添加
3. 或使用插件自带的清单代理（1.8 以上）

**清单代理地址**：

```
http://你的服务器:8096/CollectionStudio/Manifest
```

它每次请求自动带时间戳绕过缓存，且走服务器本地网络。

---

## 三、手动安装

### 下载对应包

从 [dist/](../dist) 目录：

| 文件 | Jellyfin |
|---|---|
| `Jellyfin.Plugin.CollectionStudio-5.1.0.0-net8.0.zip` | 10.9 |
| `Jellyfin.Plugin.CollectionStudio-5.1.0.1-net9.0.zip` | 10.10 / 10.11 |
| `Jellyfin.Plugin.CollectionStudio-5.1.0.2-net10.0.zip` | 12.4 |

### 解压到插件目录

```
<Jellyfin data>/plugins/Collection Studio_5.1.0.2/
├── Jellyfin.Plugin.CollectionStudio.dll
├── LiteDB.dll
└── meta.json
```

Windows 默认：
`C:\ProgramData\Jellyfin\Server\data\plugins\`

Docker 默认（容器内）：
`/config/plugins/` → 宿主机对应 `<你的配置目录>/plugins/`

### 必须包含 meta.json

缺了它 Jellyfin **不会加载插件**，而且控制台里根本看不到这个插件，自然也无法禁用或卸载。

### 重启

```sh
# systemd
systemctl restart jellyfin

# Docker
docker restart jellyfin
```

### 确认加载

日志里应有：

```
Loaded plugin: "Collection Studio" "5.1.0.2"
```

---

## 四、首次配置

### 1. MetaTube 地址

设置页 → MetaTube 与基础设置：

| 字段 | 说明 |
|---|---|
| MetaTube 地址 | 如 `http://192.168.1.2:8080` |
| MetaTube Token | 如无鉴权可留空 |
| 演员写真来源 Provider | GFriends 或 MetaTube |
| 超时（秒） | 默认 20 |

填完点「测试连接」，显示可达才继续。

**关键提醒**：这个地址必须能被 **Jellyfin 服务器**访问，不是浏览器所在机器。如果 Jellyfin 在 Docker 里，`127.0.0.1` 指的是容器自己，不是宿主机。

### 2. 自助刮削源（可选）

设置页 → 自定义刮削源 → 「一键添加」：

粘贴网址即可，如：

```
https://example.com/data/
```

系统会自动：

- 从 URL 路径推断源名称（也可手填）
- 若网址缺 `{id}` 占位符，按「目录/{id}.json」补全并提示
- 自动设为当前写真来源

### 3. 元数据保存位置（重要）

控制台 → 媒体库 → 选中库 → 编辑：

**取消勾选「将图片保存到媒体文件夹」**

这样刮削下载的封面、背景图存到 Jellyfin 配置目录（`/config/metadata/`），不写到媒体文件夹。

**「元数据路径」** 填本地可写目录，如 `/config/metadata`。

**「元数据保存器」** 里若不需要 NFO 文件也取消勾选，否则 `.nfo` 仍会生成在影片旁边。

**为什么重要**：如果媒体在 CIFS/SMB 网络共享上，往那里写元数据既占共享空间，又可能因权限或锁失败。

### 4. 转码路径

控制台 → 播放 → 转码 → 转码路径填本地可写目录，如 `/cache/transcodes`。

Docker 部署时建议把缓存也指到大容量分区：

```sh
uci set jellyfin.@jellyfin[0].cache_path='/mnt/sdb1/jellyfin-cache'
uci commit jellyfin
```

---

## 五、磁盘改名的前置条件

「同时重命名磁盘文件」会真的调 `File.Move`。以下任一不满足都会失败。

### 1. 目录必须可写

先在插件里点 **「检查写权限」**，看结论：

| 结论 | 处理 |
|---|---|
| 只读挂载 | 见下方「容器挂载」 |
| 无写权限 | `chown -R 1000:1000 <宿主机目录>` |
| 目录不存在 | 媒体路径已变，更新媒体库路径 |

### 2. 容器挂载：嵌套挂载不会自动传递

如果你的媒体在网络共享上（CIFS/SMB），典型配置是：

```yaml
volumes:
  - /:/media          # 整根挂进容器
```

**问题**：Docker 的 bind 挂载用 `rprivate` 传播模式，**只挂载该挂载点本身，不传递它下面的嵌套挂载点**。

所以宿主机上 `/yingshi`（一个独立的 CIFS 挂载点）**不会**出现在容器的 `/media/yingshi`，那里落在只读的 squashfs 层，报 `Read-only file system`。

**修法**：显式再挂一行：

```yaml
volumes:
  - /dev/dri:/dev/dri
  - /mnt/sdb1/jellyfin-config:/config
  - /mnt/sdb1/jellyfin-cache:/cache
  - /:/media
  - /yingshi:/media/yingshi:rw      # ← 新增
```

Docker 按目标路径深度排序，`/media/yingshi` 会覆盖在 `/media` 之上。

**这样 Jellyfin 不需要重扫媒体库** —— 影片路径仍是 `/media/yingshi/xxx`。

### 3. CIFS 挂载要加 nobrl

```sh
# /etc/config/fstab
nobrl,username=...,password=...,rw,vers=3.1.1,iocharset=utf8,soft,_netdev
```

`nobrl` 关闭字节范围锁请求。缺了它，即使目录可写，`File.Move` 也会因文件锁失败（尤其是正在播放的文件）。

### 4. 验证

```sh
# 挂载点是否独立
docker exec jellyfin cat /proc/mounts | grep yingshi

# 写测试
docker exec jellyfin sh -c 'touch /media/yingshi/.t && rm /media/yingshi/.t && echo WRITABLE'

# 改名测试（和插件的 File.Move 是同一操作）
docker exec jellyfin sh -c 'cd /media/yingshi && f=$(ls *.mp4 *.mkv 2>/dev/null | head -1); mv "$f" "t-$f" && mv "t-$f" "$f" && echo RENAME-OK'
```

两条都通过后，插件里的磁盘改名就能工作。

---

## 六、升级

### 在线升级

控制台 → 插件 → 目录 → 找到 Collection Studio → 选择新版本 → 安装 → 重启。

**注意**：不要手动删除旧目录，Jellyfin 会自动处理。

### 升级后界面仍是旧版

iframe 缓存导致。浏览器打开：

```
http://你的服务器:8096/CollectionStudio/Full
```

按 `Ctrl+F5` 强制刷新，再回插件设置页。

### 确认版本

控制台 → 插件 → Collection Studio 详情页 → 「版本」字段。

应与所选版本一致。如果不一致（比如显示 1.0.0.0），说明装的是旧包或 DLL 版本未同步，重装即可。

---

## 七、卸载

控制台 → 插件 → Collection Studio → 卸载。

**注意**：Jellyfin 在**运行时**无法删除插件目录（DLL 被加载锁定），会标记为 `deleteOnStartup`。所以卸载后目录还在，**重启一次**才真正删除。

这是 Jellyfin 自身机制，不是插件问题。

### 彻底清理（含数据）

卸载后如需清空数据：

```sh
# 插件数据（规则、合集记录、备份、重命名日志）
rm -rf <data>/plugins/configurations/CollectionStudio

# 若手动装过且目录残留
rm -rf <data>/plugins/Collection Studio_*
```

---

## 八、Docker 完整配置示例

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin:latest
    container_name: jellyfin
    restart: unless-stopped
    network_mode: host
    environment:
      - TZ=Asia/Shanghai
    devices:
      - /dev/dri:/dev/dri
    volumes:
      - /mnt/sdb1/jellyfin-config:/config
      - /mnt/sdb1/jellyfin-cache:/cache
      - /mnt/sdb1/jellyfin-cache/transcodes:/cache/transcodes
      # 媒体：网络共享必须显式挂载，不能只靠 /:/media 传递
      - /yingshi:/media/yingshi:rw
      # 若还有其他媒体目录，逐个列出
      # - /mnt/sdb1/media:/media/local:rw
```

**不要**把整个 `/` 挂进容器当媒体目录 —— 只读层会导致改名失败，且扩大暴露面。

---

## 九、故障速查

| 现象 | 原因 | 处理 |
|---|---|---|
| 控制台看不到插件 | 缺 `meta.json`，或 DLL 与 Jellyfin 版本不匹配 | 确认目录内三个文件齐全；确认 net8/net9/net10 对应正确 |
| 插件显示 `NotSupported` | meta.json 的 `status` 异常 | 重置为 `"Active"` 后重装 |
| 设置页按钮点不动 | 12.4 注入页不执行脚本 | 升级到 1.7.0.2 以上 |
| 页面文字看不清 | 早期版本缺背景层 | 升级到 4.1.0.2 以上 |
| 规则执行 0 条 | 索引空 / 媒体库错配 / 缺分组字段 | 点规则行「诊断」看哪层归零 |
| 预演返回 400 | 4.1 之前的模型绑定问题 | 升级到 4.2.0.2 以上 |
| 刮削报 MetadataRefreshOptions | 4.8 之前的命名空间与构造错误 | 升级到 4.8.0.2 以上 |
| 磁盘改名报只读 | 嵌套挂载未传入容器 | 显式挂载 + 检查写权限 |
| 磁盘改名报 Permission denied | 容器用户无写权限 | `chown -R 1000:1000` |
| 看不到新版本 | CDN 60 秒缓存 | URL 加 `?v=` 时间戳 |

---

## 十、日志位置

| 部署方式 | 日志路径 |
|---|---|
| Windows | `C:\ProgramData\Jellyfin\Server\log\` |
| Linux/Docker | `<data>/log/` 或 `/config/log/` |

搜关键字：

```sh
grep -i "Collection Studio" <log>/*.log | tail -20
```

插件相关问题都会打上 `[Collection Studio]` 前缀。
