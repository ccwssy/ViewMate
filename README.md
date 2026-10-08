# 观影助手 (ViewMate)

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](https://www.gnu.org/licenses/agpl-3.0)

Emby 播放体验增强插件 — **拼音搜索** + **中文搜索** + **片头片尾跳过** + **漏集补打**。

适配 Emby **4.9.5.0 / 4.10.0.9 / 4.10.0.40 / 4.10.1.0**（.NET 6.0 / SDK 10 跨编译）。双 DLL 部署（ViewMate.dll ~193KB + TinyPinyin.dll ~40KB）。

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
- c0 格式：`原名称 空格拼音 连写拼音 拼音bigram 单CJK字 CJK双字bigram 首字母连写 首字母后缀族`
- **拼音首字母 token（v1.2.19.0+）**：`initials` 为每个 CJK 音节首字母大写连写（「功夫女足」→ `GFNZ`），继承 `pinyin-overrides.json` 词组校正（重庆 → `CQ`），非 CJK 字符不进 initials；其后附 **initials 的全部后缀**（`initialsSuffixes`，长度 n-1 → 2，单字母后缀省略）——「阿丽塔：战斗天使」→ initials `ALTZDTS` → 后缀 `LTZDTS TZDTS ZDTS DTS TS`。任意长度、任意位置的缩写都是某个后缀的前缀，配合前缀查询即可命中：整串 `altzdts`、只打头 `al`、中段 `zdts` 都能搜到。旧方案的相邻双字母滑窗（`AL LT TZ ZD DT TS`）只覆盖整串与相邻两字母组合，中段多字母缩写（如 `zdts`）搜不到，故 v1.2.19.0 起替换（索引净增约 1MB）
- 单 CJK 字 token（如 `变 形 金 刚`）支持单字搜索
- CJK 双字 bigram token（如 `变形 形金 金刚`）支持中文子串搜索
- SQL 侧「待注入」判据（`FtsIndexWriter.cs` 四条查询统一，v1.2.16.22 起）：`c.c0 = mi.Name`（c0 与原名逐字相等才需注入）+ `mi.Name GLOB '*[一-龥]*'`（原名含 CJK）+ `mi.Name NOT GLOB '*Season*'`（排除季名）——旧判据 `c0 NOT GLOB '*[a-zA-Z]*'` 会把 `UCP`、`G.O.A.T`、`Walter Hendrix III` 这类**纯 ASCII 片名**永久判为 stale（#15 v1.2.13.1 修复 GLOB 使用实际汉字，非 `\\u` 文本字面量）
- 监听 `ItemAdded`/`ItemUpdated` 事件，新入库即时处理
- 默认开启
- **词组级多音字校正**：通过外部 JSON 文件 `pinyin-overrides.json` 配置，`Lazy<T>` 进程内只加载一次——**改完需重启 Emby 生效**（无热重载路径，配置页保存不会重新读取）。
### 2. 拼音排序名（SortName）

Emby 对中文条目的 `SortName` 默认取汉字本身，A-Z 索引里中文会整堆落到「#」。开启后插件改为**直连 SQL `UPDATE MediaItems.SortName`**：逐字取拼音首字母大写连写，「功夫」→ `GF`、「阿丽塔：战斗天使」→ `ALTZDTS`，字母索引/侧边栏即按拼音归位。**无 Harmony 依赖、不改 Emby 二进制、不写媒体文件与 NFO**，只动 `library.db`。

- 生成规则：遍历 `Name`，汉字取其拼音首字母（大写），**字母与数字原样保留**，其余符号丢弃（`A-阿丽塔：战斗天使` → `AALTZDTS`）；整名无汉字则跳过不动
- 词组级校正 `pinyin-overrides.json` 同样生效（重庆 → `CQ`）
- **可回滚**：覆盖前先把原始 `SortName` 备份到同库 `PinyinSortNameBackup` 表；在配置页把「拼音排序名」关掉时自动还原并清空备份表（日志 `[PinyinSortName] Restore complete: N items restored`）。首次开启若发现尚无备份（从旧版本升上来），会先把现存 `SortName` 重置为 `Name`（即 Emby 默认）再生成，避免旧残留
- **默认关闭**（配置页「拼音搜索」tab）。开启后启动延时 60 秒开始回填（存量 1.2 万条量级，200 条/事务、批间 300ms），回填完做一次 `wal_checkpoint(TRUNCATE)`；之后 `ItemAdded`/`ItemUpdated` 由 30 秒批量队列（50 条/批）同步
- 新入库事件路径会跳过：`SortName` 已锁定的条目、剧集/季（`IHasSeries`）、不支持字母排序的条目；存量回填走 SQL 扫描（`Name` 含汉字且未备份过），不检查锁定标记
- ⚠️ **卸载前先关开关**：还原只在「配置页关闭」时触发，直接删 DLL 不会还原；要彻底恢复就先关开关、确认日志出现 `Restore complete`，再卸载

### 3. 片头片尾跳过（IntroSkip）

监控用户播放行为，自动检测并写入 Emby 标准 Chapter 标记（`IntroStart`/`IntroEnd`），支持所有标准 Emby 客户端。

用户在观看时**看到片头直接拖进度条跳到正片**，插件检测到大跳转（≥20s）后：
- 写入 `IntroStart#ECS` / `IntroEnd#ECS` 到 `Chapters3` 表
- 下次播放时客户端自动显示「跳过片头」按钮

#### 客户端上报间隔对检测精度的影响

IntroSkip 依赖客户端进度事件来确定跳转起止点。不同客户端的上报行为差异显著：

| 客户端 | 首次上报间隔 | 可靠性 | 说明 |
|--------|------------|--------|------|
| **Hills**（第三方） | ~5s | ✅ | 事件密集，跳转源/终点位置准确 |
| **Emby 原生安卓** | ~5s | ✅ | 同上 |
| **Yamby**（安卓） | ~22s | ⚠️ | 首次进度事件滞后，自然播放位置污染 pre-FF 数据 |

**工作机理：** 插件通过三种算法自动适配：

1. **unreportedGap 检测** — 比较 `FirstJumpPositionTicks` 与 `PlaybackStartTicks` 的差值。≤10s（Hills/原生）用 `FirstJumpTargetTicks` 作为片头终点；>10s（Yamby）用 `skipDistance` 推算净跳转距离
2. **回退修正** — 检测到 `FirstJumpTarget > LastJumpPosition` 时（用户跳过头又拉回），改用最近一次 FF 终点
3. **起点强制归零** — 当 `PlaybackStartTicks=0`（用户从视频开头播放）时，片头起点强制从 0s 开始

**实际效果：** Hills/原生客户端首次即可准确检测。Yamby 用户首次播放时终点可能有 5-10s 误差，但后续播放时缩略图跳转行为会自动修正（auto-healing）。

**日志关键词：**
```
[IntroSkip] Intro detected: 0s → 45s (src=0s)    # 正常检测
Big jump tracked: 5s → 45s (elapsed=0.6s)          # 跳转原始数据
Seek detected: 00:00:05 → 00:00:45 (jump=40s elapsed=0.6s)  # 累计跳转
```

### 4. 漏集补打（IntroBackfill）

启动时自动扫描缺少 Intro 标记的剧集，从同季已有标记的集复制补打。解决库扫描时序竞态导致的漏打问题。

## 安装

### 要求
- Emby 4.9.5.0+（.NET 6 容器；4.9.5.0 为**实测下限**，更低版本未验证）
- **4.9.5.0 / 4.10.0.40 / 4.10.0.9 / 4.10.1.0 已实测适配**（4.10.0.40 首次实测 2026-09-09 / v1.2.17.0）：插件加载 / EntryPoint / 拼音搜索（FTS5 注入）/ 拼音排序名（SQL 直连）/ 片头片尾跳过 / 漏集补打全部正常。4.10 仅迁移了授权相关内部类（`HardwareAccelerationRequiresPremiere` → `ApplicationHost` 等），ViewMate 不依赖这些 API，无需为 4.10 单独出包
- **Emby 4.10 系搜索已消费 `fts_search9`**：反编译确认 Items 搜索走 `join fts_search9 ... MATCH @SearchTerm`。旧 skill/wiki 中「Emby 不查 FTS」是 4.9.5.0 时代 `Search/Hints` API 失效的误诊，Items 搜索路径两版都在

### 升级安装（覆盖已有插件）

已有 v1.2.x 的老用户，覆盖 DLL 即可升级——但**必须「停止 → 换文件 → 启动」，不要用 `docker restart`**（Restart 时插件可能在文件/配置写入中途被加载，导致新版本不生效或配置被覆盖）：

```bash
# 1. 下载最新 DLL
curl -L -o ViewMate.dll \
  "https://github.com/ccwssy/ViewMate/releases/latest/download/ViewMate.dll"
curl -L -o TinyPinyin.dll \
  "https://github.com/ccwssy/ViewMate/releases/latest/download/TinyPinyin.dll"

# 2. 覆盖到 Emby 插件目录
docker cp ViewMate.dll embyserver:/config/plugins/ViewMate.dll
docker cp TinyPinyin.dll embyserver:/config/plugins/TinyPinyin.dll

# 3. 停止 → 启动 Emby（Stop→Start，不要用 restart）
docker stop embyserver
docker start embyserver
```

> ⚠️ 覆盖 DLL 后**必须重启 Emby** 才能加载新版本，且必须用 **Stop→Start**：`docker restart` 可能在插件/配置写入中途加载（见「全新安装」的说明）。
> 以上命令中容器名 `embyserver` 为 Docker 默认名。若你的容器名不同（如 `emby`），请自行替换。
>
> ⚠️ **升级到 v1.2.19.0 必读（一次性）**：token 布局变更后必须删除回填游标 `/config/plugins/pinyin-backfill-cursor.txt`，否则存量回填按 `id > cursor` 从表尾续跑、已过时的 c0 永不重写（回填查询不判断 c0 是否过时）；删掉游标才会从头重跑全量。

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

> ⚠️ **重要**：全新安装时**必须 Stop 容器/服务，不要用 Restart**。Restart 会在插件/配置写入过程中重新加载，可能覆盖新配置缓存。正确顺序：**停止 → 删 `plugins/configurations/ViewMate.xml`（插件配置，仅从旧版本迁移时需要）→ 替换文件 → 启动**。

首次启动后，PinyinSearch 会在后台**分批**注入拼音：每轮 1000 行、5 分钟一轮，不停机也不阻塞首页；存量 1.5 万条目量级实测约 **75 分钟**补完（16 轮），期间搜索/播放正常，新入库条目由事件即时处理。搜索中文名或其拼音即可直达。

### 从 Release 安装（命令行）

> ⚠️ 以下为**全新安装**步骤。已有 v1.2.x 请按「停止容器 → 覆盖 DLL → 启动容器」升级（不要用 `docker restart`）。

```bash
# 1. 下载
curl -L -o ViewMate.dll \
  "https://github.com/ccwssy/ViewMate/releases/latest/download/ViewMate.dll"
curl -L -o TinyPinyin.dll \
  "https://github.com/ccwssy/ViewMate/releases/latest/download/TinyPinyin.dll"

# 2. 复制到 Emby 插件目录
docker cp ViewMate.dll embyserver:/config/plugins/ViewMate.dll
docker cp TinyPinyin.dll embyserver:/config/plugins/TinyPinyin.dll

# 3. 停止 → 启动 Emby（Stop→Start，不要用 restart）
docker stop embyserver
docker start embyserver
```

### 验证安装

```bash
docker exec embyserver grep "ViewMate" /config/logs/embyserver.txt
```

预期输出：

```
Loading ViewMate, Version=1.2.19.0, Culture=neutral, PublicKeyToken=null from /config/plugins/ViewMate.dll
Entry point completed: ViewMate.Plugin
```

然后在 Emby Web → 插件 → 观影助手 看到配置页面即成功。

### 卸载

```bash
docker exec embyserver rm -f /config/plugins/ViewMate.dll /config/plugins/TinyPinyin.dll
docker stop embyserver && docker start embyserver   # Stop→Start，不要用 restart
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

文件格式：`{"词组": "拼音1 拼音2 ..."}`，支持多词组覆盖。**改完需重启 Emby 才生效**：v1.2.15.0 起为 `Lazy<T>` 加载，进程内只读取一次、无热重载路径。

## 配置

通过 Emby 插件配置页操作，无需手写 XML。设置页为 **4 个横向 tab**：**观影助手（总览，只读） / 拼音搜索 / 片头尾跳过 / 关于**；每个 tab 保存时先 Reload 磁盘全量配置、只写回本 tab 的字段，不会交叉覆盖：

| 设置 | 默认 | 说明 |
|------|:----:|------|
| 观影助手 | — | 总览页：一览当前开关与生效状态，纯只读（无保存按钮），防误改 |
| 拼音搜索 | 开 | 自动化拼音注入，新入库即时处理 |
| 片头片尾跳过 | 关 | 检测跳转行为写入 IntroSkip 标记 |
| 最长片头 (秒) | 150 | 跳转起点超过此值不视为片头 |
| 最长片尾 (秒) | 180 | 片尾检测阈值 |
| 拼音排序名 | 关 | 把中文媒体 SortName 更新为拼音首字母，A-Z 索引按拼音排（拼音搜索 tab，详见功能 §2） |
| 漏集补打 | 关 | 启动时补打缺失标记 |
| 版本更新检查 | 关 | 启动 5 分钟后检查 GitHub 最新 Release，最多重试 3 次（关于 tab） |

## 卸载

1. 删除 `/config/plugins/ViewMate.dll` 和 `/config/plugins/TinyPinyin.dll`
2. `docker stop embyserver` → `docker start embyserver`（Stop→Start，不要用 restart）
3. 已写入的拼音数据保留在 `fts_search9_content` 中；`Chapters3` 标记保留在 DB 中

## 版本

完整版本历史请参阅 [CHANGELOG.md](./CHANGELOG.md)。

## 数据库维护

Emby 的数据库位于容器内部 `/config/data/library.db`，但官方镜像**不含 sqlite3 客户端**。需要先找到宿主机挂载路径，用宿主机的 sqlite3 直接操作。

### 前置：查找宿主机路径

```bash
docker inspect embyserver --format '{{range .Mounts}}{{.Source}} → {{.Destination}}{{"\n"}}{{end}}' | grep config
```

取输出中 `→` 左侧的路径，记为 `<config路径>`。后续所有命令都使用该路径。

例：输出为 `/mnt/data/emby-config → /config`，则 `<config路径>` = `/mnt/data/emby-config`。

### 验证拼音是否录入成功

```bash
# 查看 FTS 记录总数
sqlite3 <config路径>/data/library.db "SELECT COUNT(*) FROM fts_search9;"

# 测试拼音搜索（dianying → 电影）
sqlite3 <config路径>/data/library.db \
  "SELECT Name FROM fts_search9 WHERE fts_search9 MATCH 'dianying' LIMIT 5;"

# 查看一条完整拼音记录
sqlite3 <config路径>/data/library.db \
  "SELECT substr(c0,1,120) FROM fts_search9_content LIMIT 1;"
```

预期：count > 0（中文媒体多的可能上万）；MATCH 应有结果；c0 内容格式为 `原名 空格拼音 连写拼音 bigram 单字 token 双字 token 首字母连写 首字母后缀族`。

### 验证数据库是否损坏

```bash
sqlite3 <config路径>/data/library.db "PRAGMA integrity_check;"
```

输出 `ok` 表示完好。出现 `database corruption`、`btreeInitPage() returns error code 11` 等表示损坏。

### 修复损坏的数据库

> ⚠️ 先备份，再修复。

```bash
# 1. 停 Emby（防止写入冲突）
docker stop embyserver

# 2. 备份
cp <config路径>/data/library.db <config路径>/data/library.db.bak

# 3. 使用 .recover 提取可恢复数据
sqlite3 <config路径>/data/library.db ".recover" > /tmp/recover.sql

# 4. 重建干净库
sqlite3 <config路径>/data/library-recovered.db < /tmp/recover.sql

# 5. 重建 FTS 虚拟表（.recover 不会重建 FTS5）
sqlite3 <config路径>/data/library-recovered.db "
DROP TABLE IF EXISTS fts_search9;
DROP TABLE IF EXISTS fts_search9_data;
DROP TABLE IF EXISTS fts_search9_idx;
DROP TABLE IF EXISTS fts_search9_content;
DROP TABLE IF EXISTS fts_search9_docsize;
DROP TABLE IF EXISTS fts_search9_config;
"
sqlite3 <config路径>/data/library-recovered.db "
CREATE VIRTUAL TABLE fts_search9 USING FTS5(
    Name, OriginalTitle, SeriesName, Album,
    tokenize='unicode61 remove_diacritics 2',
    prefix='1 2 3 4'
);
"

# 6. 验证新库
sqlite3 <config路径>/data/library-recovered.db "PRAGMA integrity_check;"
# 应输出: ok

# 7. 替换
mv <config路径>/data/library-recovered.db <config路径>/data/library.db

# 8. 启动 Emby
docker start embyserver
```

启动后插件会自动检测 FTS 为空并全量重建拼音：后台分批（每轮 1000 行、5 分钟一轮），1.5 万条目量级实测约 **75 分钟**，首页正常加载、搜索不受阻塞。

## 注意事项

### 卡点：SQLite ≥ 3.45 无 simple tokenizer

旧版 EnhanceChineseSearch 依赖 `libsimple.so` 替换 FTS tokenizer。Emby 官方镜像使用 SQLite 3.49.2，`simple` 分词器已从 FTS5 内置列表中移除。即使在当前连接加载成功，其他连接的 FTS5 查询全部崩溃（`no such tokenizer: simple`）。

**v1.2.0.0+ 已完全移除该方案**，改用 TinyPinyin C# 直接写入 FTS 内容表 + Name 字段。

### 卡点：被归类为 Season 的媒体无法拼音搜索

STRM 库中的电影可能被归类为 **Season**（真库实测 `Type=7`，Path 形如 `.../Season N`）而非 Movie，不在 Emby 搜索白名单内，FTS MATCH 命中也被过滤。常见可搜类型（真库实测）：Movie `Type=5` / Episode `Type=8` / Series `Type=6` / Person `Type=23` / BoxSet `Type=9` / Genre `Type=21` / Studio `Type=29`。

### 检测阈值

`DetectJump()` 使用跳转源位置（`jumpSrc`）与 `MaxIntroDurationSeconds` 比较，而非目标位置。移动端用户可能直接拖到片尾（远超过 150s），但跳转起点在片头区域内就应视为片头。

## 致谢

- [xinjiawei/StrmAssistant_less](https://github.com/xinjiawei/StrmAssistant_less) — 上游精简版项目
- [sjtuross/StrmAssistant](https://github.com/sjtuross/StrmAssistant) — 原始神医助手