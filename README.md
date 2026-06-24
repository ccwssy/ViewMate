# 观影助手 (ViewMate)

Emby 播放体验增强插件 — **拼音搜索** + **片头片尾跳过** + **漏集补打**。

适配 Emby **4.9.5.0**（.NET 6.0 / SDK 10 跨编译）。双 DLL 部署（ViewMate.dll + TinyPinyin.dll），~183KB。

## 功能

### 1. 拼音搜索

通过 **FTS5 内容表注入拼音**，无需修改 tokenizer、不加载 libsimple.so：

```
用户搜 "gongfu"
  → FTS MATCH:  fts_search9 MATCH 'gongfu'      ✅（标准类型）
  → （v1.2.4.0+ 不再写 MediaItems.Name，LIKE 回退不适用）
```

- 使用 TinyPinyin C# 库，**反射加载**（绕过 Emby 插件 ALC 隔离，不在编译时生成 AssemblyRef）
- 启动时自动扫描新入库的中文媒体注入拼音，写入 `fts_search9_content.c0`
- SQL 查询用 `c.c0 NOT GLOB '*[a-zA-Z]*'` 避开了 UTF-8 GLOB 多字节范围不匹配 bug
- 监听 `ItemAdded`/`ItemUpdated` 事件，新入库即时处理
- 默认开启

### 2. 片头片尾跳过（IntroSkip）

监控用户播放行为，自动检测并写入 Emby 标准 Chapter 标记（`IntroStart`/`IntroEnd`），支持所有标准 Emby 客户端。

用户在观看时**看到片头直接拖进度条跳到正片**，插件检测到大跳转（≥20s）后：
- 写入 `IntroStart#ECS` / `IntroEnd#ECS` 到 `Chapters3` 表
- 下次播放时客户端自动显示「跳过片头」按钮

### 3. 漏集补打（IntroBackfill）

启动时自动扫描缺少 Intro 标记的剧集，从同季已有标记的集复制补打。解决库扫描时序竞态导致的漏打问题。

## 安装

### 要求
- Emby 4.9.3.0+（.NET 6 容器）

### 手动下载安装（非命令行）{#manual-install}

如果你不想用命令行，可以通过浏览器手工操作：

1. 打开 [Releases 页面](https://github.com/ccwssy/ViewMate/releases)
2. 找到最新的 **v1.2.10.0**，展开 Assets
3. 分别点击下载 **ViewMate.dll** 和 **TinyPinyin.dll**

**Docker Emby 用户：**
- 使用 Portainer 或 Synology File Station 等文件管理工具，将两个 `.dll` 文件上传到 Emby 容器的挂载目录下的 `plugins/` 文件夹
- 一般路径：`/path/to/emby/config/plugins/`（具体取决于你的 docker-compose 卷映射）
- 上传后执行：停止容器 → 启动容器（不要用重启）

**非 Docker（裸机/Win/Linux）用户：**
- 将两个 `.dll` 文件复制到 Emby 安装目录下的 `plugins/` 文件夹
- 停止 Emby 服务 → 启动 Emby 服务

> ⚠️ **重要**：修改插件文件时**必须 Stop 容器/服务，不要用 Restart**。Restart 触发的序列化会覆盖新配置缓存。正确顺序：**停止 → 删 `plugins/configurations/观影助手.json`（如有）→ 替换文件 → 启动**。

首次启动后，PinyinSearch 会自动扫描中文条目注入拼音。稍等 10-30 秒后，搜索中文名或其拼音即可直达。

### 从 Release 安装（命令行）

```bash
# 1. 下载
curl -L -o ViewMate.dll \
  "https://github.com/ccwssy/ViewMate/releases/latest/download/ViewMate.dll"
curl -L -o TinyPinyin.dll \
  "https://github.com/ccwssy/ViewMate/releases/latest/download/TinyPinyin.dll"

# 2. 复制到 Emby 插件目录
docker cp ViewMate.dll emby:/config/plugins/ViewMate.dll
docker cp TinyPinyin.dll emby:/config/plugins/TinyPinyin.dll

# 3. 重启 Emby
docker restart emby
```

### 验证安装

```bash
docker exec emby grep "ViewMate" /config/logs/embyserver.txt
```

预期输出：

```
Loading ViewMate, Version=1.2.10.0... from /config/plugins/ViewMate.dll
Entry point completed: ViewMate.Plugin
```

然后在 Emby Web → 插件 → 观影助手 看到配置页面即成功。

### 卸载

```bash
docker exec emby rm -f /config/plugins/ViewMate.dll /config/plugins/TinyPinyin.dll
docker restart emby
```

已写入的拼音数据保留在 `fts_search9_content` 中；`Chapters3` 标记保留在 DB 中。

### 手动构建

```bash
# 需要 .NET SDK 10
cd ViewMate
dotnet build -c Release
# 产物: bin/Release/net6.0/ViewMate.dll + TinyPinyin.dll
```

> ⚠️ Linux 上 ILRepack 需要 `/lib:` 指向 .NET BCL 目录。不含 TinyPinyin 合并则拼音搜索无法工作。

## 配置

通过 Emby 插件配置页操作，无需手写 XML：

| 设置 | 默认 | 说明 |
|------|:----:|------|
| 拼音搜索 | 开 | 自动化拼音注入，新入库即时处理 |
| 片头片尾跳过 | 关 | 检测跳转行为写入 IntroSkip 标记 |
| 最长片头 (秒) | 150 | 跳转起点超过此值不视为片头 |
| 最长片尾 (秒) | 360 | 片尾检测阈值 |
| 漏集补打 | 关 | 启动时补打缺失标记 |

## 卸载

1. 删除 `/config/plugins/ViewMate.dll` 和 `/config/plugins/TinyPinyin.dll`
2. `docker restart emby`
3. 已写入的拼音数据保留在 `fts_search9_content` 中；`Chapters3` 标记保留在 DB 中

## 版本

| 版本 | 日期 | 说明 |
|------|------|------|
| v1.2.10.0 | 2026-06-24 | 修复 TinyPinyin 加载（反射替代编译引用）；修复 SQL GLOB 中文字符范围 bug |
| v1.2.9.1 | 2026-06-23 | 修复 GetDbConnection（适配 Emby 4.8 PooledDatabaseConnectionManager） |
| v1.2.9.0 | 2026-06-23 | 清理死代码 — 删 Lib.Harmony、scripts/、ITaskManager |
| v1.2.8.0 | 2026-06-23 | IntroSkip 字段回到子 Section，VisibleCondition 隐藏子字段 |
| v1.2.7.0 | 2026-06-23 | 平铺 IntroSkip 字段到 PluginOptions |
| v1.2.6.0 | 2026-06-23 | 主开关 EnableIntroSkip 提到 PluginOptions 顶层 |
| v1.2.5.0 | 2026-06-23 | 修复关于页版本号显示 1.0.0.0+n/a |
| v1.2.4.0 | 2026-06-23 | 拼音搜索不再改 MediaItems.Name + 防抖重建 FTS |
| v1.2.3.0 | 2026-06-23 | 修复累积检测 bug，遥控器连按也可触发 |
| v1.2.2.0 | 2026-06-23 | 清理屎山：删除 Mod/Web/Tokenizer，DLL 从 6.7MB 降到 2.5MB |
| v1.2.1.0 | 2026-06-23 | 精简配置页，隐藏无用模块 |
| v1.2.0.0 | 2026-06-23 | 合并脚本功能：PinyinSearch + IntroBackfill 进 DLL |
| v1.1.1.0 | 2026-06-23 | 新增 PersonFilter、写 MediaItems.Name |
| v1.0.0.1 | 2026-06-23 | 更新图标、清理重复插件 |
| v1.0.0 | 2026-06-21 | 首次发布（仅 IntroSkip + EnhanceChineseSearch） |

## 注意事项

### 卡点：SQLite ≥ 3.45 无 simple tokenizer

旧版 EnhanceChineseSearch 依赖 `libsimple.so` 替换 FTS tokenizer。Emby 官方镜像使用 SQLite 3.49.2，`simple` 分词器已从 FTS5 内置列表中移除。即使在当前连接加载成功，其他连接的 FTS5 查询全部崩溃（`no such tokenizer: simple`）。  

**v1.2.0.0+ 已完全移除该方案**，改用 TinyPinyin C# 直接写入 FTS 内容表 + Name 字段。

### 卡点：Type=3(Season) 媒体无法拼音搜索

STRM 库中的电影可能被归类为 Type=3(Season)，不在 Emby 搜索白名单内，FTS MATCH 命中也被过滤。标准类型（Type=1 Movie / Type=5 Video / Type=8 Episode）无此问题。

### 检测阈值

`DetectJump()` 使用跳转源位置（`jumpSrc`）与 `MaxIntroDurationSeconds` 比较，而非目标位置。移动端用户可能直接拖到片尾（远超过 150s），但跳转起点在片头区域内就应视为片头。

## 致谢

- [xinjiawei/StrmAssistant_less](https://github.com/xinjiawei/StrmAssistant_less) — 上游精简版项目
- [sjtuross/StrmAssistant](https://github.com/sjtuross/StrmAssistant) — 原始神医助手
