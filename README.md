# 观影助手 (ViewMate)

Emby 播放体验增强插件 — **片头片尾跳过** + **中文搜索增强**。

适配 Emby **4.9.5.0**（.NET 6.0 / SDK 10 跨编译）。

> Forked from [xinjiawei/StrmAssistant_less](https://github.com/xinjiawei/StrmAssistant_less)

## 功能

### 1. 片头片尾跳过（IntroSkip）

通过监控用户播放行为，自动检测并写入 Emby 标准 Chapter 标记（`IntroStart`/`IntroEnd`），支持所有标准 Emby 客户端。

用户在观看时**看到片头直接拖进度条跳到正片**，插件检测到大跳转（≥20s）后：

- 写入 `IntroStart#ECS` / `IntroEnd#ECS` 到 `Chapters3` 表
- **同季所有剧集自动写入相同标记**（`FetchEpisodesInSeason`，即时生效）
- 下次播放该集时客户端自动显示「跳过片头」按钮
- 之后任意一集重新跳转，全季自动更新（自动修复错误位置）

### 2. 中文搜索增强

通过 **FTS5 内容表直接注入拼音**方案，绕过 tokenizer 替换，不依赖任何外部扩展库：

```
Emby MATCH 'gongfu'
  → FTS5 索引里已有 "功夫 gong fu gongfu"
  → unicode61 分词器直接命中
  → 返回结果
```

- 不改 tokenizer、不加载 libsimple.so、无多连接崩溃风险
- 拼音通过 cron 自动注入（30 分钟/次，静默执行）
- 配合 `prefix='1 2 3 4'` 支持连写拼音部分匹配（`gon` → 功夫）

## 部署

### 要求

- Emby 容器（官方或 ccwssy/embyserver:latest）
- .NET SDK 10（仅构建需要，运行不需要）

### 从 Release 安装

1. 从 [Releases](../../releases) 下载 `ViewMate.dll`
2. `docker cp ViewMate.dll emby:/config/plugins/ViewMate.dll`
3. 同时复制 `zh/`、`zh-hant/` 卫星资源（如果不需要中文界面可跳过）
4. `docker restart emby`

### 手动构建

```bash
# 需要 .NET SDK 10 + mono（运行 ILRepack）

# 1. 构建
dotnet restore ViewMate.sln
dotnet publish ViewMate/ViewMate.csproj -c Release -f net6.0 -o /tmp/viewmate-publish

# 2. ILRepack 合并依赖到单 DLL
ILREPACK=$(find ~/.nuget/packages/ilrepack -name "ILRepack.exe" | head -1)
BCL_DIR=$(find /usr -path "*/shared/Microsoft.NETCore.App/*" -type d | head -1)

mono "$ILREPACK" \
  /out:/tmp/viewmate-publish/ViewMate.dll \
  /tmp/viewmate-publish/ViewMate.dll \
  /tmp/viewmate-publish/0Harmony.dll \
  /tmp/viewmate-publish/TinyPinyin.dll \
  /tmp/viewmate-publish/ChineseConverter.dll \
  /tmp/viewmate-publish/SQLitePCL.pretty.dll \
  /tmp/viewmate-publish/SQLitePCLRaw.core.dll \
  /lib:/tmp/viewmate-publish \
  /lib:"$BCL_DIR"

# 3. 部署
docker cp /tmp/viewmate-publish/ViewMate.dll emby:/config/plugins/
docker cp /tmp/viewmate-publish/zh/ emby:/config/plugins/zh/
docker cp /tmp/viewmate-publish/zh-hant/ emby:/config/plugins/zh-hant/
docker restart emby
```

> ⚠️ ILRepack 合并后约 6.8MB，部署后 Emby 日志显示 `Version=1.x.x.x`。
> ⚠️ Linux 上 ILRepack 需要 `/lib:` 指向 .NET BCL 目录才能解析 `System.Text.Json`。

## 注意事项

### 小到大跳检测阈值

`DetectJump()` 和 `OnPlaybackStopped()` 中跳转源位置（`jumpSrc`）与 `MaxIntroDurationSeconds` 比较，而非目标位置。因为移动端用户可能直接拖到片尾（远超过 150s），但跳转起点在片头区域内就应视为片头。

### 已知限制

- `GetEpisodes()` 在新加库扫描未完成时可能漏掉部分剧集（Library 扫描时序竞态）。典型表现为中间几集（如 E02-E05）缺少标记。解决方法：再次触发检测或手动补写 `Chapters3`。
- Harmony 补丁在 ILRepack 后失效（PatchUnpatch 返回 false），不影响已用 unicode61 替代的方案。

## 配置

插件配置位于 `/config/plugins/configurations/ViewMate.xml`：

```xml
<PluginConfiguration>
  <EnableIntroSkip>true</EnableIntroSkip>
  <MaxIntroDurationSeconds>150</MaxIntroDurationSeconds>
  <MaxCreditsDurationSeconds>360</MaxCreditsDurationSeconds>
  <MinOpeningPlotDurationSeconds>30</MinOpeningPlotDurationSeconds>
</PluginConfiguration>
```

## 卸载

1. 删除 `/config/plugins/ViewMate.dll`
2. `docker restart emby`
3. 已写入的 `Chapters3` 标记 **保留在 DB 中**，客户端依然识别

如需清除所有标记：
```sql
DELETE FROM Chapters3 WHERE MarkerType IN (1, 2) AND Name LIKE '%#ECS%';
```

## 版本

| 版本 | 日期 | 说明 |
|------|------|------|
| v1.0.0.1 | 2026-06-23 | 更新图标、清理重复插件、修复加载日志 |
| v1.0.0 | 2026-06-21 | 首次发布。适配 Emby 4.9.5.0 |

## 致谢

- [xinjiawei/StrmAssistant_less](https://github.com/xinjiawei/StrmAssistant_less) — 上游精简版项目
- [sjtuross/StrmAssistant](https://github.com/sjtuross/StrmAssistant) — 原始神医助手
