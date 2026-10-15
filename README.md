# 观影助手 (ViewMate)

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](https://www.gnu.org/licenses/agpl-3.0)

Emby 播放体验增强插件 — **拼音搜索** + **中文搜索** + **片头片尾跳过** + **漏集补打**。

适配 Emby **4.9.5.0 / 4.10.0.9 / 4.10.0.40 / 4.10.1.0**（.NET 6.0 / SDK 10 跨编译）。双 DLL 部署（ViewMate.dll ~820KB + TinyPinyin.dll ~40KB）。

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
- **词组级多音字校正**：通过外部 JSON 文件 `pinyin-overrides.json` 配置，`Lazy<T>` 进程内只加载一次——**改完需重启 Emby 生效**（无热重载路径）。**v1.2.20.0 起可在设置页「拼音搜索」tab 直接编辑**：状态行显示「已存在：<路径>（N 条规则）」或「未找到文件，保存后将创建」，「编辑多音字列表」弹出多行编辑框；保存前做严格校验 + 自动修正（对象/数组尾随逗号、全角 `，：“”` 归一），失败时**不写盘**并给「第 N 行附近：这一行是「<出错行原文>」—— 原因」，成功则原子写入并轮转备份（保留最近 5 个）
- **多音字匹配语义（最长命中）**：从文本当前位置起**取能匹配上的最长键**——单字作默认读音、更长的词组作特例覆盖。例：`"重": "zhong"` 是全字兜底（重量/重要），`"重庆": "chong qing"` 在「重庆」处优先生效；只写单字时该字**每一处**都会用该读音，写单字请谨慎
- 文件缺失时（v1.2.20.0+）插件会在启动时自动写入一份示例模板（**文件已存在则绝不改动**，空文件/非法内容也不碰）
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

#### 手动标记管理（v1.2.20.0+）

自动检测之外，点设置页「片头尾跳过」tab 的「**手动标记校正**」按钮，在弹出的**剧集标记管理弹窗**里管理任意剧集的标记（v1.2.20.0 起由主 tab 内嵌区块改为弹窗，字段、命令链与行为一字未改）：

- **四级递进选择**：媒体库 → 剧集（有异常标记的剧名后带 ⚠ —— 判据与单集状态**同一口径**：该剧下存在「未标记 / 片头 ≥ 5 分钟 / 片尾缺失」的集；**手动锁定与手动清除过的集一律豁免**，不会再让剧集亮 ⚠）→ 季 → **整季各集列表**（每集一行显示 集号 + 标题 + 片头/片尾摘要 + 状态标签；异常行橙色左边条、手动锁定行蓝色左边条）；顶部支持「剧名搜索 / 只看标记异常 / 只看最近播放」筛选
- **编辑弹窗**：三个时间字段接受 `mm:ss` / `hh:mm:ss` / 纯秒数，留空 = 不改写该标记；「**锁定本集**」＝该集不再被自动检测与漏集补打覆盖（默认勾选），**取消勾选并保存 = 交回自动管理**（删除覆盖表记录，`Chapters3` 里的标记值保留不删）；「**应用到整季**」＝把本次的值写入同季**全部**可播放集（**含已锁定的集**——锁定只挡自动逻辑，不拦用户主动操作），整季应用时**只有当前集**会随「锁定本集」勾选状态增删覆盖表记录，其它集的锁定状态不动；另有「清除标记」按钮。保存回执会明示「本集已锁定」或「本集已解锁，交回自动管理」
- **手动覆盖表** `viewmate-intro-overrides.json`：记录手动改过（`locked`）与手动清除过（`suppressed`）的集，作为自动逻辑的避让依据；生效值仍写在 Emby 的 `Chapters3` 表（Emby 只认 `MarkerType`）
- **手动标记自愈（v1.2.20.0+）**：Emby 的章节图片/缩略图刷新会用**媒体文件探测出的章节**整表覆盖 `Chapters3`（`SaveChapters` 是全量替换），插件写入的标记会被抹掉。因此新增自愈服务：启动 150 秒后首轮、其后**每 10 分钟**一轮，按覆盖表校验并补回缺失/漂移的手动标记（日志 `self-heal: restored manual markers for <集名>`），被手动清除的集若被写回则再次清除（`re-suppressed`）；**顺带清理"现存超片长标记"**——自动管理的集里值超出片长的 `#ECS` 标记会被删除（`self-heal: removed out-of-range …`），手动锁定的集**只告警不删**，片长未知（`RunTimeTicks` 为空）的集一律跳过；正常轮次**零写入零日志**，异常只告警不外抛
- **刷新元数据会暂时抹掉标记（预期行为）**：对剧集/单集执行 Emby 的「刷新元数据」（尤其是「替换所有元数据」）会触发媒体重新探测，`Chapters3` 被全量替换，插件的片头/片尾标记可能**暂时消失**；手动标记会在**自愈的下一轮（≤10 分钟）内自动补回**，请勿重复手动补写
- **越界校验**：写入前校验 `start ≥ 0`、`start ≤ end`、`end ≤ 片长（RunTimeTicks）`，越界拒绝并给出中文原因（例如「片尾开始时间超出片长」）

### 4. 漏集补打（IntroBackfill）

启动时自动扫描缺少 Intro 标记的剧集，从同季已有标记的集复制补打。解决库扫描时序竞态导致的漏打问题。

### 5. 社区片头尾数据（v1.2.20.0+）

除本地检测外，可从两个社区库获取/共享片头片尾数据（外网只连 `api.theintrodb.org` 与 `api.introdb.app` 两个域名）：

- **三开关语义**（片头尾跳过 tab）：
  - 「**社区数据增强**」（总开关，默认关）—— 关 = 查询与共享**全部静默**；
  - 「**自动补打**」（获取侧，默认关）—— 刷新元数据时自动查询缺失片段并写入标记；**与社区配置弹窗里的「获取」是同一个字段**，弹窗不重复放置；
  - 「**共享本机检测结果**」（提交侧，在「社区数据配置」弹窗里，默认关）—— 每 12 小时把本地检测到的值提交回社区（本版为 **dry-run**：只打包请求体、不真正提交，凱可在计划任务面板手动触发查看）。
- **每个媒体库还要单独勾选**：库设置 → 元数据下载器里勾选 `ViewMate — TheIntroDB` / `ViewMate — IntroDB`（插件不做按库开关字段）。**未勾选 = 完全不发请求**。
- **只在本地缺片段时才查**（缺片头或缺片尾之一才查那一侧，不会全量重刷）；**手动锁定/清除过的集永久豁免**（不查询、不写社区值）。
- **冲突优先级**：手动锁定（覆盖表）> 社区值 > 内嵌章节映射 > 本地检测值。本地检测写入前若该集已有社区值则跳过。
- **标记体系**（`Chapters3`，按后缀区分来源、可分别清除）：`#ECS` = 手动/本地检测值；`#VMDB` = 社区值；`#VMCH` = 媒体文件内嵌章节名映射（词表含 `intro/opening/片头/オープニング` 与 `credits/ending/片尾/エンディング` 等，识别时不改文件名、不改 Emby 前端）。
- **API Key（可选）**：在 [theintrodb.org](https://theintrodb.org) / [introdb.app](https://introdb.app) 官网申请后填入「社区数据配置」弹窗。**留空也能查询**（匿名额度较低），但**无法提交**。两把 key **都未配置**时，在社区数据配置弹窗点「保存」会**自动把「共享本机检测结果」关掉**（共享任务本来就无法提交，避免主 tab 状态行显示「共享 开」却实际无效）；只配了其中一把 key 时共享开关保持你的选择。
- **旧库回填两条官方路径**（插件不做独立"回填"计划任务）：
  1. **手动刷新元数据**：对单集/整季/整库执行 Emby 的「刷新元数据」（**普通刷新**）；
  2. **库级自动刷新间隔**：库设置里的 `AutomaticRefreshIntervalDays`（30/60/90 天档）与占位记录到期（`PlaceholderMetadataRefreshIntervalDays`），到期自动重跑 provider 链。
- **为什么不推荐「替换所有元数据」**：该操作会调用 Emby 的 ThumbnailGenerator 全量重写 `Chapters3`（**Emby 原生行为，与社区 provider 无关**），插件的 `#ECS`/`#VMDB`/`#VMCH` 标记会被一并抹掉；普通「刷新元数据」不会。

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
Loading ViewMate, Version=1.2.20.0, Culture=neutral, PublicKeyToken=null from /config/plugins/ViewMate.dll
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

文件格式：`{"键": "拼音1 拼音2 ..."}`，键可以是**词组**也可以是**单字**。**改完需重启 Emby 才生效**：v1.2.15.0 起为 `Lazy<T>` 加载，进程内只读取一次、无热重载路径。

**匹配语义（最长命中）**：从文本当前位置起取**能匹配上的最长键**。所以单字是「该字所有出现处的默认读音」、更长词组是「特例覆盖」：

```json
{
  "重庆": "chong qing",
  "会": "hui",
  "银行": "yin hang",
  "会计": "kuai ji"
}
```

上例中「重庆」被词组覆盖为 `chong qing`；`"会": "hui"` 只是演示"键可写单字"（`会` 默认本就读 hui，不改变任何结果）。**不建议**写 `"重": "zhong"` 这类会改变未列举词组读音的单字兜底（会让「重生」「重逢」被误读）。

**v1.2.20.0 起**：可直接在设置页「拼音搜索」tab 编辑（保存有校验/自动修正/行级报错/备份轮转）；文件缺失时启动会自动写入一份示例模板（**已存在则不碰**）。

> 仓库侧模板文件位于 [`assets/pinyin-overrides.json`](./assets/pinyin-overrides.json)，与插件缺失时自动写入的模板**字节一致**（92 字节 / UTF-8 无 BOM / LF，sha256 `e70e26b6…`）；部署目标仍是容器的 `/config/plugins/pinyin-overrides.json`。

## 配置

通过 Emby 插件配置页操作，无需手写 XML。设置页为 **4 个横向 tab**：**观影助手（总览，只读） / 拼音搜索 / 片头尾跳过 / 关于**；每个 tab 保存时先 Reload 磁盘全量配置、只写回本 tab 的字段，不会交叉覆盖：

**「片头尾跳过」tab 第三版结构（自上而下）**：标题 → 顶部说明（与开关隔行）→ ① 启用片头尾跳过检测 → ② 时长配置开关 → ③ 最长片头/片尾（仅 ② 开时显示）→ ④ 启用漏集补打（仅 ① 开时显示）→ ⑤ 社区数据增强 → ⑥ 自动补打（仅 ⑤ 开时显示）→ ⑦ 社区数据配置按钮（仅 ⑤ 开时显示）→ ⑧ 手动标记校正按钮（常在）→ 状态行。**条件显隐**由服务端 `VisibleCondition` 声明、客户端 `genericedit.js` 的 `applyPropertyConditions` 执行；检测关时「时长配置」连同两个时长一起折叠。

**观影助手（总览）tab** 是纯只读的 4 行状态：拼音搜索 / 片头尾跳过（含漏集补打、手动标记校正弹窗）/ **社区数据增强** / 关于，不产生保存或编辑控件。

| 设置 | 默认 | 说明 |
|------|:----:|------|
| 观影助手 | — | 总览页：一览当前开关与生效状态，纯只读（无保存按钮），防误改 |
| 拼音搜索 | 开 | 自动化拼音注入，新入库即时处理 |
| 片头片尾跳过 | 关 | 检测跳转行为写入 IntroSkip 标记（主 tab 第 1 个开关） |
| 时长配置 | 关 | 主 tab 的折叠开关：**仅当片头尾检测开着时显示**，控制下面两个时长字段（T19 起持久化，保存后离开再回来保持原状态；检测关时不改变已保存的状态） |
| 最长片头 (秒) | 150 | 跳转起点超过此值不视为片头（仅「时长配置」开着时显示） |
| 最长片尾 (秒) | 180 | 片尾检测阈值（仅「时长配置」开着时显示） |
| 社区数据增强 | 关 | 从 TheIntroDB / IntroDB 获取并可共享片头片尾数据（总开关，详见功能 §5） |
| 自动补打 | 关 | 刷新元数据时自动查询缺失片段（仅社区数据增强开着时显示） |
| 共享本机检测结果 | 关 | 每 12 小时把本地检测值提交回社区（社区配置弹窗内；本版 dry-run） |
| 拼音排序名 | 关 | 把中文媒体 SortName 更新为拼音首字母，A-Z 索引按拼音排（拼音搜索 tab，详见功能 §2） |
| 漏集补打 | 关 | 启动时补打缺失标记 |
| 版本更新检查 | 关 | 启动 5 分钟后检查 GitHub 最新 Release，最多重试 3 次（关于 tab） |
| 多音字配置 | — | 拼音搜索 tab：状态行 + 「编辑多音字列表」弹窗，直接维护 `pinyin-overrides.json`（v1.2.20.0+） |
| 手动标记校正 | — | 片头尾跳过 tab 的按钮 → **剧集标记管理弹窗**：四级选择 + 整季列表 + 单集编辑（锁定 / 应用到整季含锁定集 / 清除），v1.2.20.0+ |
| 社区数据配置 | — | 片头尾跳过 tab 的按钮 → 弹窗：共享开关 + 两个社区库 API Key + 三开关语义说明，v1.2.20.0+ |

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

### 历史：为什么不用 FTS tokenizer（simple 分词器）

> 仅作历史背景，**不适用于 v1.2.0.0 及以后版本**：当前代码零 `libsimple` 依赖。

旧版 EnhanceChineseSearch 依赖 `libsimple.so` 替换 FTS tokenizer。Emby 官方镜像使用 SQLite 3.49.2，`simple` 分词器已从 FTS5 内置列表中移除；即使在当前连接加载成功，其他连接的 FTS5 查询也会全部崩溃（`no such tokenizer: simple`）。

**v1.2.0.0+ 已完全移除该方案**，改用 TinyPinyin C# 直接写入 FTS 内容表 + Name 字段。

### 特殊情况：条目若被归类为 Season，可能搜不到

**排查线索，不是普遍现象**（来源：本项目早期 STRM 库排障记录；**未在当前库复现**——当前库 246 个季条目全部是真实季目录，无季型 `.strm`）。若遇到「拼音索引里明明有、Emby 搜索却不返回」，先查该条目在库里的真实类型：

```sql
SELECT RowId, Name, Type FROM MediaItems WHERE Name LIKE '%<关键词>%';
```

若该条目是 **Season**（`Type=7`，Path 形如 `.../Season N`）而非 Movie（`Type=5`），则它**可能**不在 Emby 的搜索白名单内，FTS MATCH 命中也会被过滤（该行为未逐条实测，仅记录为排查方向）。常见类型实测：Movie `5` / Series `6` / Season `7` / Episode `8` / Genre `21` / Person `23` / Studio `29` / BoxSet `9`。

### 检测阈值

`DetectJump()` 使用跳转源位置（`jumpSrc`）与 `MaxIntroDurationSeconds` 比较，而非目标位置。移动端用户可能直接拖到片尾（远超过 150s），但跳转起点在片头区域内就应视为片头。

## 致谢

- [xinjiawei/StrmAssistant_less](https://github.com/xinjiawei/StrmAssistant_less) — 上游精简版项目
- [sjtuross/StrmAssistant](https://github.com/sjtuross/StrmAssistant) — 原始神医助手