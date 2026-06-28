# 更新日志

| 版本 | 日期 | 说明 |
|------|------|------|
| **v1.2.15.0** | 2026-06-28 | **基于 v1.2.14.4 重构 — 重写 PinyinSearchService** — 全量重构 PinyinSearchService（365行插入/350行删除）；`_disposed` 改为 `int` + `Interlocked` 原子操作；连接管理器反射缓存重构（Create/OpenRead/OpenWrite 统一管理）；词组多音字校正重构为 `Lazy<T>` 加载；合并 v1.2.14.5 连接泄漏修复。 |
| **v1.2.14.4** | 2026-06-27 | **代码洁癖 + 每小时 orphan 清理** — 提取 `Escape()`、`BuildFtsInsertSql()`、`UpdateLastScanId()` 消除 4 处 INSERT/2 处 MAX(id)/6 处 escape 重复；修正 ProcessBatch 预读路径二次 escape bug；清理死注释；新增 `CleanOrphanedFtsEntries()` 每小时删除已删除视频的残留 FTS 行。 |
| **v1.2.14.3** | 2026-06-27 | **四路径拼音录取 + 防锁增强** — 新增 `GetMissingMediaItemsCount()` 兜底 `MaxPendingTotal` cap 漏掉的条目；`MaxPendingTotal=5000`→`100000`；`PendingQuery()` 回退原始 JOIN（无 UNION），避免大表 LEFT JOIN 挂死；新增 `GetFtsTotalCount()==0`→`ProcessFullReindex()` 空 FTS 回退。 |
| **v1.2.14.2** | 2026-06-27 | **空 FTS 全量重建** — `ProcessAllPendingBatched()` 检测 `SELECT COUNT(*) FROM fts_search9`=0 时切到 `ProcessFullReindex()`，直接从 MediaItems 扫中文名条目生成拼音；`_lastScanId` 保证后续增量走索引。 |
| **v1.2.14.1** | 2026-06-27 | **修复 Emby 4.9.5.0 兼容 + 增量扫描** — 事件处理器可能静默失败时，后台线程 60s 后首次全量扫描补缺拼音，之后每 5 分钟增量扫描（`WHERE c.id > _lastScanId`，走 rowid 索引，不扫旧行不抢锁）。修复 v1 版误入 30s 热路径卡死和 v2 版全表扫与库扫描争锁的问题。 |
| **v1.2.14.0** | 2026-06-26 | **修复扫库首页卡死** — 事件处理零SQL（仅入队不操作数据库）；`GetDbConnection()` 全部加 `using` 修复连接池泄漏；启动批处理每批间加 500ms 间隙让路首页读请求；`item.Id` → `item.InternalId` 修复 GUID→long 兼容。 |
| **v1.2.13.2** | 2026-06-25 | **添加 SQLite WAL checkpoint** — PinyinScan 完成 FTS 重建后执行 `PRAGMA wal_checkpoint(TRUNCATE)`，防止 WAL 持续膨胀导致首页搜索卡死；GLOB 查询继承 `[一-龥]` 修复（v1.2.13.0）。 |
| **v1.2.13.0** | 2026-06-24 | **修复 ARM64 Synology DSM 卡死** — ProcessAllPending 改为后台分批执行，每批 200 条释放写锁，首页秒开；新增词组级多音字校正（pinyin-overrides.json） |
| v1.2.12.0 | 2026-06-24 | 词组级多音字校正：外部 JSON 配置，支持词组跳过 TinyPinyin |
| v1.2.11.0 | 2026-06-24 | 新增中文子串搜索：FTS c0 中注入单 CJK 字 + CJK 双字 bigram token，搜"金刚"能找到"变形金刚" |
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
