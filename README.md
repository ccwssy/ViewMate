# Emby Chinese Search

Emby 中文搜索增强插件。用 SQLite FTS5 `simple` 分词器替换默认 `unicode61`，实现中文模糊搜索和拼音搜索。

适配 Emby **4.9.5.0**（构建时从运行容器提取 DLL 做引用，保证 API 兼容）。

> Forked from [xinjiawei/StrmAssistant_less](https://github.com/xinjiawei/StrmAssistant_less)，仅保留中文搜索增强，去掉代理配置等无关模块。

## 原理

Emby 的搜索依赖 SQLite FTS5（全文索引，表 `fts_search9`）。默认分词器 `unicode61` 只做 Unicode 字符切分，不支持中文词汇边界和拼音。

本插件通过 **Harmony 运行时打补丁**的方式，在 Emby 启动时动态注入 `simple` 分词器（内嵌结巴分词 + 拼音索引），重建全文索引。不修改 Emby 本体 DLL，关闭后重启即可恢复。

### 支持的功能

- ✅ 中文**模糊搜索**——搜"功"匹配"功夫"
- ✅ **拼音搜索**——搜 `gongfu` 匹配"功夫"
- ✅ 可配置搜索范围（电影/剧集/合集/演员等）
- ✅ 不修改 Emby DLL，安全可逆

## 安装

### 从 Release 安装

1. 从 [Releases](../../releases) 下载 `EmbyChineseSearch.dll`
2. 放入 Emby 的 `plugins/` 目录
3. 重启 Emby
4. 进入 Emby 控制台 → 插件 → Emby Chinese Search → 启用「中文搜索增强」→ 保存
5. **再次重启 Emby** 使搜索增强生效

### 手动构建

```bash
# 需要 .NET SDK 6.0+

# 1. 从运行中的 Emby 容器提取参考 DLL
mkdir -p libs/publicized
docker cp emby:/system/Emby.Api.dll libs/publicized/
docker cp emby:/system/Emby.ProcessRun.dll libs/publicized/
docker cp emby:/system/Emby.Providers.dll libs/publicized/
docker cp emby:/system/Emby.Sqlite.dll libs/publicized/
docker cp emby:/system/MediaBrowser.Controller.dll libs/publicized/
docker cp emby:/system/MediaBrowser.Common.dll libs/publicized/
docker cp emby:/system/MediaBrowser.Model.dll libs/publicized/

# 2. 构建
dotnet restore EmbyChineseSearch.sln
dotnet build EmbyChineseSearch.sln

# 3. 合并依赖到单 DLL（可选）
#    Linux 下需要 mono-complete
mono ~/.nuget/packages/ilrepack/2.0.44/tools/ILRepack.exe \
  /out:EmbyChineseSearch.dll \
  EmbyChineseSearch/bin/Debug/net6.0/EmbyChineseSearch.dll \
  EmbyChineseSearch/bin/Debug/net6.0/0Harmony.dll \
  EmbyChineseSearch/bin/Debug/net6.0/ChineseConverter.dll \
  EmbyChineseSearch/bin/Debug/net6.0/TinyPinyin.dll \
  /lib:EmbyChineseSearch/bin/Debug/net6.0/
```

## 卸载

1. 在插件设置中**关闭**「中文搜索增强」，保存
2. 重启 Emby（自动恢复原始 FTS 索引）
3. 到插件管理页面卸载本插件
4. **切勿**直接删 DLL——会导致数据库 FTS 索引残留

## 版本

| 版本 | 日期 | 说明 |
|------|------|------|
| v1.0.0 | 2026-06-21 | 首次发布。基于上游 StrmAssistant_less 4.9.3.0，适配 Emby 4.9.5.0 |

## 致谢

- [xinjiawei/StrmAssistant_less](https://github.com/xinjiawei/StrmAssistant_less) — 上游精简版项目
- [sjtuross/StrmAssistant](https://github.com/sjtuross/StrmAssistant) — 原始神医助手
- [simple 分词器](https://github.com/wangfenjin/simple) — SQLite FTS5 中文分词器
