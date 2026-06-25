# 更新日志

| 版本 | 日期 | 说明 |
|------|------|------|
| **v1.2.13.2** | 2026-06-25 | **README 修正** — 容器名 `emby`→`embyserver`（Docker 默认名），插件大小 ~1.9MB→152KB+40KB（双 DLL 实际），移除 ILRepack 误导段。 |
| **v1.2.13.1** | 2026-06-24 | **修复 SQL GLOB `\\u4e00` 文本字面量问题（#15）** — C# verbatim 字符串中 `\\u4e00` 被 SQLite 当 6 ASCII 字符处理，只命中 4 项。改回实际汉字 `[一-龥]`，正确匹配 31+ 项。统一线上线下源。 |
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
