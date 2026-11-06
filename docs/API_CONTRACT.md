# 合集工坊 API 契约（冻结版）

前端只允许调用下列接口。基址：`ApiClient.getUrl('CollectionStudio/...')`；
若拿不到 ApiClient，则用 `{serverBase}/CollectionStudio/...` 并附 `?api_key=<token>`。
鉴权：请求头 `X-Emby-Token: <token>` 或查询参数 `api_key=<token>`。

所有响应为 JSON（除导出接口返回二进制）。时间均为服务器本地时间字符串 `yyyy-MM-dd HH:mm:ss`。

## 1. GET /Status
```json
{ "pluginVersion":"1.0.0","jellyfinVersion":"10.11.11","serverTime":"2026-11-06 12:00:00",
  "indexedItems":1234,"personCount":567,"lastIndexedAt":"2026-11-06 11:00:00",
  "metaTubeConfigured":true,"metaTubeReachable":true,"dbPath":"...","backupCount":3 }
```

## 2. GET /Settings  ·  POST /Settings
```json
{ "metaTubeBaseUrl":"http://192.168.1.2:8080","metaTubeToken":"",
  "metaTubeActorProvider":"GFriends","metaTubeTimeoutSeconds":20,
  "customSources":[{"id":"src1","name":"我的刮削源","urlTemplate":"https://x/api?q={name}",
    "imagesPath":"data.images[*]","imageBaseUrl":"",
    "headers":[{"name":"Authorization","value":"Bearer xxx"}]}],
  "defaultPageSize":50, "autoBackupBeforeApply":true }
```
POST 返回 `{"ok":true,"settings":{...}}`。

## 3. GET /Fields
数组，元素：
```json
{"key":"actor","label":"演员","kind":"person|text|number|date|list|bool",
 "ops":["eq","ne","contains","notcontains","gt","lt","ge","le","startswith"],
 "groupable":true,"multiValue":true}
```
固定字段集合（key → 中文名）：
actor 演员 / director 导演 / studio 厂商(片商) / name 标题 / num 品番 /
year 发行年份 / premiereDate 发行日期 / genre 类型 / tag 标签 /
rating 评分 / runtime 时长(分钟) / officialRating 官方分级 / itemType 媒体类型 /
path 路径 / providerId 外部ID。

## 4. 规则 Rules
`GET /Rules` → `[{Rule}]`；`POST /Rules` 新建或更新（带 id 为更新）→ `{Rule}`；
`DELETE /Rules/{id}` → `{"ok":true}`。

Rule：
```json
{ "id":"...","name":"按演员建合集","enabled":true,
  "mode":"person|studio|genre|year|dateRange|custom",
  "personType":"Actor|Director",
  "targetName":"高级规则目标合集名",
  "titleTemplate":"{value} 合集",
  "conditions":[{"field":"genre","op":"contains","value":"剧情","conjunction":"and"}],
  "sortBy":"premiereDate|productionYear|name|dateCreated|runtime",
  "sortDesc":false,
  "coverMode":"firstItem|scraped|url|none",
  "coverUrl":"","coverProvider":"GFriends",
  "minItems":1,"maxCollections":0,"refresh":true,"createdAt":"...","updatedAt":"..."
}
```

## 5. 规则预演与执行
`POST /Rules/{id}/Preview` → 
```json
{"totalItems":80,"groups":[{"name":"某演员","count":12,
   "sample":[{"itemId":"guid","name":"ABC-123 标题","path":"/media/.."}]}]}
```
`POST /Rules/{id}/Apply` → 
```json
{"created":3,"updated":1,"skipped":0,"deleted":0,
 "collections":[{"id":"guid","name":"某演员","itemCount":12}],
 "log":["已创建合集 …"]}
```

## 6. 合集 Collections
`GET /Collections` → 
```json
[{"id":"guid","name":"某演员","ruleId":"r1","itemCount":12,"createdAt":"...",
  "updatedAt":"...","coverUrl":"","backupCount":1,"managedBy":"按演员建合集"}]
```
`POST /Collections/{id}/Delete` → `{"ok":true,"backupId":"b1"}`
`POST /Collections/{id}/Cover` body `{"url":"https://..."}` → `{"ok":true}`
`GET /Collections/{id}/Items` → `{"name":"...","items":[{"itemId":"","name":"","path":""}]}`

## 7. 封面库（演员/导演写真）
`GET /Covers/Search?name=某演员&provider=GFriends&source=custom:src1` →
```json
{"name":"某演员","matchedName":"某演员","source":"MetaTube/GFriends",
 "candidates":[{"url":"https://...jpg","thumbUrl":"/CollectionStudio/Covers/Proxy?url=...","width":0,"height":0}]}
```
`GET /Covers/Proxy?url=<urlencoded>` → 图片字节流（image/*）。
错误：`{"error":"中文错误信息"}` + 相应 HTTP 状态码（400/404/500/502）。

## 8. 改名对照 Rename
`GET /Rename?page=1&pageSize=50&filter=&sortBy=name&sortDesc=false` →
```json
{"total":1234,"page":1,"pageSize":50,
 "rows":[{"itemId":"guid","path":"/media/a/ABC-123.mp4","originalTitle":"ABC-123",
   "jellyfinTitle":"ABC-123 中文标题","changed":true,"kind":"Movie",
   "year":2020,"premiereDate":"2020-01-01","collection":"某演员"}]}
```
filter 为对「服务器重定义名称」的模糊筛选。
`GET /Rename/Export?destination=client|server&scope=page|all&page=&pageSize=&filter=&sortBy=&sortDesc=`
- destination=client：返回 `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`，附件名 `改名对照_20261106_120000.xlsx`
- destination=server：返回 `{"ok":true,"path":"C:\\...\改名对照_....xlsx","fileName":"..."}`

## 9. 备份与还原
`GET /Backups` → 
```json
[{"id":"b1","name":"合集工坊_backup_20261106_120000_123","createdAt":"2026-11-06 12:00:00.123",
  "kind":"manual|auto|predelete","note":"","sizeBytes":12345,"rules":3,"collections":5,"renames":0}]
```
`POST /Backups` body `{"note":"手动备份"}` → `{Backup}`
`POST /Backups/Restore` body `{"id":"b1"}` → `{"ok":true,"restoredRules":3,"restoredCollections":5}`
`DELETE /Backups/{id}` → `{"ok":true}`

## 10. 定时任务
`GET /Tasks` → `[{"id":"scan","name":"扫描媒体库并应用规则","lastRun":"...","state":"idle|running"}]`
`POST /Tasks/{id}/Run` → `{"ok":true}`
`GET /Tasks/{id}/Log?lines=200` → `{"lines":["..."]}`

## 通用错误
HTTP 非 2xx 时返回 `{"error":"中文错误信息"}`，前端直接弹出该文案。
