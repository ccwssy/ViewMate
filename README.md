# 观影助手 (ViewMate)

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](https://www.gnu.org/licenses/agpl-3.0)

Emby 播放体验增强插件 — **拼音搜索** + **中文搜索** + **片头片尾跳过** + **漏集补打**。

适配 Emby **4.9.5.0**（.NET 6.0 / SDK 10 跨编译）。双 DLL 部署（ViewMate.dll ~172KB + TinyPinyin.dll ~40KB）。

## 支持平台

| 架构 | 状态 |
|:----:|:----:|
| amd64 (x86_64) | ✅ 主推部署 |
| arm64 | ✅ 支持 |
| Windows (amd64) | ✅ 复制 plugins 目录即可 |

## 功能

### 1. 拼音 & 中文搜索

通过 **FTS5 内容表注入拼音 + 中文单字/双字子串 token**，无需修改 tokenizer、不加载 libsimple.so：

```
用户搜 "gongfu"
  → FTS MATCH:  fts_search9 MATCH 'gongfu'      ✅（拼音搜索）

用户搜 "金刚"
  → FTS MATCH:  fts_search9 MATCH '金刚'          ✅（中文子串搜索，v1.2.11.0+）
```

- 使用 TinyPinyin C# 库，**反射加载**（绕过 Emby 插件 ALC 隔离，不在编译时生成 AssemblyRef）
- 启动时后台分批处理自动扫描新入库的中文媒体注入拼音（不阻塞首页加载），写入 `fts_search9_content.c0`
- c0 格式：`原名称 空格拼音 连写拼音 拼音bigram 单CJK字 CJK双字bigram`
- 单 CJK 字 token（如 `变 形 金 刚`）支持单字搜索
- CJK 双字 bigram token（如 `变形 形金 金刚`）支持中文子串搜索
- SQL 查询用 `c.c0 NOT GLOB '*[a-zA-Z]*'` + `c.c0 GLOB '*[一-龥]*'` 双筛中文（#15 v1.2.13.1 修复 GLOB 使用实际汉字，非 `\\u` 文本字面量）
- 监听 `ItemAdded`/`ItemUpdated` 事件，新入库即时处理
- 默认开启
- **词组级多音字校正**：通过外部 JSON 文件 `pinyin-overrides.json` 配置，`Lazy<T>` 加载，无需重启

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

### 升级安装（覆盖已有插件）

已有 v1.2.x 的老用户，直接覆盖 DLL 即可升级：

```bash
# 1. 下载最新 DLL
curl -L -o ViewMate.dll \
  "https://github.com/ccwssy/ViewMate/releases/latest/download/ViewMate.dll"
curl -L -o TinyPinyin.dll \
  "https://github.com/ccwssy/ViewMate/releases/latest/download/TinyPinyin.dll"

# 2. 覆盖到 Emby 插件目录
docker cp ViewMate.dll embyserver:/config/plugins/ViewMate.dll
docker cp TinyPinyin.dll embyserver:/config/plugins/TinyPinyin.dll

# 3. 重启 Emby（必须重启，覆盖 DLL 后不重启不生效）
docker restart embyserver
```

> ⚠️ 覆盖 DLL 后**必须重启 Emby** 才能加载新版本。只用 `docker restart` 即可，无需 Stop→Start 流程。
> 以上命令中容器名 `embyserver` 为 Docker 默认名。若你的容器名不同（如 `emby`），请自行替换。

### 全新安装

#### 手动下载安装（非命令行）{#manual-install}

如果你不想用命令行，可以通过浏览器手工操作：

1. 打开 [Releases 页面](https://github.com/ccwssy/ViewMate/releases)
2. 找到最新版，展开 Assets
3. 分别点击下载 **ViewMate.dll** 和 **TinyPinyin.dll**

**Docker Emby 用户：**
- 使用 Portainer 或 Synology File Station 等文件管理工具，将两个 `.dll` 文件上传到 Emby 容器的挂载目录下的 `plugins/` 文件夹
- 一般路径：`/path/to/emby/config/plugins/`（具体取决于你的 docker-compose 卷映射）
- 上传后执行：停止容器 → 启动容器（不要用重启）

**非 Docker（裸机/Win/Linux）用户：**
- 将两个 `.dll` 文件复制到 Emby 安装目录下的 `plugins/` 文件夹
- 停止 Emby 服务 → 启动 Emby 服务

> ⚠️ **重要**：全新安装时**必须 Stop 容器/服务，不要用 Restart**。Restart 触发的序列化会覆盖新配置缓存。正确顺序：**停止 → 删 `plugins/configurations/观影助手.json`（如有）→ 替换文件 → 启动**。

首次启动后，PinyinSearch 会在后台自动扫描中文条目注入拼音。首页秒开，后台 30~55 秒跑完。搜索中文名或其拼音即可直达。

### 从 Release 安装（命令行）

> ⚠️ 以下为**全新安装**步骤。已有 v1.2.x 请直接覆盖 DLL 后 `docker restart` 即可。

```bash
# 1. 下载
curl -L -o ViewMate.dll \
  "https://github.com/ccwssy/ViewMate/releases/latest/download/ViewMate.dll"
curl -L -o TinyPinyin.dll \
  "https://github.com/ccwssy/ViewMate/releases/latest/download/TinyPinyin.dll"

# 2. 复制到 Emby 插件目录
docker cp ViewMate.dll embyserver:/config/plugins/ViewMate.dll
docker cp TinyPinyin.dll embyserver:/config/plugins/TinyPinyin.dll

# 3. 重启 Emby
docker restart embyserver
```

### 验证安装

```bash
docker exec embyserver grep "ViewMate" /config/logs/embyserver.txt
```

预期输出：

```
ViewMate, Version=1.2.15.0... from /config/plugins/ViewMate.dll
Entry point completed: ViewMate.Plugin
```

然后在 Emby Web → 插件 → 观影助手 看到配置页面即成功。

### 卸载

```bash
docker exec embyserver rm -f /config/plugins/ViewMate.dll /config/plugins/TinyPinyin.dll
docker restart embyserver
```

已写入的拼音数据保留在 `fts_search9_content` 中；`Chapters3` 标记保留在 DB 中。

### 手动构建

```bash
# 需要 .NET SDK 10
cd ViewMate
dotnet restore
dotnet build -c Release -o build ViewMate/ViewMate.csproj
# 产物: build/ViewMate.dll + build/TinyPinyin.dll
```

### pinyin-overrides.json（多音字校正）

部署可选的词组级多音字校正文件到 `/config/plugins/pinyin-overrides.json`：

```json
{
  "行": "xing",
  "银行": "yin hang",
  "行长": "hang zhang",
  "还": "hai"
}
```

文件格式：`{"词组": "拼音1 拼音2 ..."}`，支持多词组覆盖。无需重启即可生效，下次扫描时自动加载（v1.2.15.0 重构为 `Lazy<T>` 加载）。

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
2. `docker restart embyserver`
3. 已写入的拼音数据保留在 `fts_search9_content` 中；`Chapters3` 标记保留在 DB 中

## 版本

完整版本历史请参阅 [CHANGELOG.md](./CHANGELOG.md)。

## 数据库维护

### 验证拼音是否录入成功

```bash
# 查看 FTS 记录总数
sqlite3 /config/data/library.db "SELECT COUNT(*) FROM fts_search9;"

# 测试拼音搜索（dianying → 电影）
sqlite3 /config/data/library.db \
  "SELECT Name FROM fts_search9 WHERE fts_search9 MATCH 'dianying' LIMIT 5;"

# 查看一条完整拼音记录
sqlite3 /config/data/library.db \
  "SELECT substr(c0,1,120) FROM fts_search9_content LIMIT 1;"
```

预期：count > 0（中文媒体多的可能上万）；MATCH 应有结果；c0 内容格式为 `原名 空格拼音 连写拼音 bigram 单字 token 双字 token`。

### 验证数据库是否损坏

```bash
sqlite3 /config/data/library.db "PRAGMA integrity_check;"
```

输出 `ok` 表示数据库完好。如果出现 `database corruption`、`out of order`、`btreeInitPage() returns error code 11` 等均表示数据库损坏。

### 修复损坏的数据库

> ⚠️ 先备份，再修复。

```bash
# 1. 停 Emby（防止写入冲突）
docker stop emby

# 2. 备份损坏的库
cp /config/data/library.db /config/data/library.db.bak

# 3. 使用 .recover 从损坏的库中提取所有可恢复数据
sqlite3 /config/data/library.db ".recover" > /tmp/recover.sql

# 4. 重建干净的库
sqlite3 /config/data/library-recovered.db < /tmp/recover.sql

# 5. 修复 FTS 虚拟表（.recover 不会重建 FTS5 virtual table）
#    如果修复后 FTS5 无法使用，需删除 shadow tables 并重建空 FTS：
sqlite3 /config/data/library-recovered.db "
DROP TABLE IF EXISTS fts_search9;
DROP TABLE IF EXISTS fts_search9_data;
DROP TABLE IF EXISTS fts_search9_idx;
DROP TABLE IF EXISTS fts_search9_content;
DROP TABLE IF EXISTS fts_search9_docsize;
DROP TABLE IF EXISTS fts_search9_config;
"
sqlite3 /config/data/library-recovered.db "
CREATE VIRTUAL TABLE fts_search9 USING FTS5(
    Name, OriginalTitle, SeriesName, Album,
    tokenize='unicode61 remove_diacritics 2',
    prefix='1 2 3 4'
);
"

# 6. 验证新库
sqlite3 /config/data/library-recovered.db "PRAGMA integrity_check;"
# 应输出: ok

# 7. 替换原库
mv /config/data/library-recovered.db /config/data/library.db

# 8. 启动 Emby
docker start emby
```

启动后插件会自动检测 FTS 为空并全量重建拼音，约 30~60 秒完成。中间首页可正常加载，后台无阻塞。

## 注意事项

### ARM64 Synology DSM 卡死（v1.2.12.0 及之前）

**已修复**（v1.2.13.0）。根因：`ProcessAllPending()` 在 `Plugin.Run()` 中同步执行，使用 `TransactionMode.Immediate` 抢占 SQLite 写锁后一次性处理最多 5000 条并做 FTS rebuild。在 ARM64 慢 CPU 上锁持续 30~55 秒，阻塞 Emby HTTP → Web UI 卡死。修复后后台分批处理，每批 200 条提交释放锁，首页秒开。

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