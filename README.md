# 观影助手

Emby 播放体验增强插件 — **中文搜索增强** + **片头片尾自动跳过**。

适配 Emby **4.9.5.0**（构建时从运行容器提取 DLL 做引用，保证 API 兼容）。

> Forked from [xinjiawei/StrmAssistant_less](https://github.com/xinjiawei/StrmAssistant_less)，原仅保留中文搜索增强。`dev` 分支追加 IntroSkip 功能。

## 功能

### 1. 中文搜索增强

Emby 的搜索依赖 SQLite FTS5（全文索引，表 `fts_search9`）。默认分词器 `unicode61` 只做 Unicode 字符切分，不支持中文词汇边界和拼音。

通过 **Harmony 运行时打补丁**的方式，在 Emby 启动时动态注入 `simple` 分词器（内嵌结巴分词 + 拼音索引），重建全文索引。不修改 Emby 本体 DLL，关闭后重启即可恢复。

- ✅ 中文**模糊搜索**——搜"功"匹配"功夫"
- ✅ **拼音搜索**——搜 `gongfu` 匹配"功夫"
- ✅ 可配置搜索范围（电影/剧集/合集/演员等）
- ✅ 不修改 Emby DLL，安全可逆

### 2. 片头片尾跳过（dev 分支）

通过监控用户播放行为，自动检测并写入 Emby 标准 Chapter 标记（`IntroStart`/`IntroEnd`/`CreditsStart`），支持所有标准 Emby 客户端（含 Yamby、Infuse、SenPlayer 等）。

- ✅ **用户行为检测** — 识别手动拖进度条跳过片头的模式
- ✅ **教学式标记** — NoDetectionButReset 模式（暂停-恢复确定边界）
- ✅ **同季回填** — 检测到一集片头后自动标记同季其他集
- ✅ 纯服务器端工作，不修改客户端

## 架构支持

| 架构 | 平台 | 支持 |
|------|------|------|
| **x64**（AMD/Intel） | Linux / Windows / macOS | ✅ |
| **arm64**（树莓派、飞腾等） | Linux | ✅ |
| arm32 / x86 | 任意 | ❌ |

`libsimple.so` 分词器扩展库内置 `linux_x64` 和 `linux_arm64` 两套二进制，启动时自动选择。

## 资源开销

近乎零。

| 资源 | 开销 | 说明 |
|------|------|------|
| **CPU** | 偶发 | 启动时重建 FTS 表（一次，根据媒体库大小几秒到几十秒），搜索时 `simple` 分词解析（微秒级） |
| **内存** | ~10MB | `libsimple.so`（846KB）+ 结巴词典加载 |
| **磁盘** | 846KB | `libsimple.so` 文件 |
| **后台线程** | 无 | 无轮询/定时任务/监控循环 |

唯一有感开销是**首次启用时重建 FTS 索引**，完成后日常搜索和正常 Emby 无差别。

## 安装

### 从 Release 安装

1. 从 [Releases](../../releases) 下载 `ViewMate.dll`
2. 放入 Emby 的 `plugins/` 目录
3. 重启 Emby
4. 进入 Emby 控制台 → 插件 → Emby Chinese Search → 启用「中文搜索增强」→ 保存
5. **再次重启 Emby** 使搜索增强生效

### 手动构建

```bash
# 需要 .NET SDK 6.0+ 和 dotnet-ILRepack

# 1. 构建
dotnet restore ViewMate.sln
dotnet build -c Release --nologo

# 2. 复制 ILRepack 依赖（SQLitePCLRawEx.core 不在 NuGet 中，需从 Emby 容器提取）
docker cp emby:/system/SQLitePCLRawEx.core.dll ViewMate/bin/Release/net6.0/

# 3. 合并依赖到单 DLL（必须，否则运行时缺少 0Harmony/TinyPinyin 等）
export PATH="$HOME/.dotnet/tools:$PATH"
cd ViewMate
ilrepack \
  /lib:bin/Release/net6.0 \
  /out:bin/Release/net6.0/ViewMate_merged.dll \
  /target:library /parallel \
  bin/Release/net6.0/ViewMate.dll \
  bin/Release/net6.0/0Harmony.dll \
  bin/Release/net6.0/TinyPinyin.dll \
  bin/Release/net6.0/ChineseConverter.dll \
  bin/Release/net6.0/SQLitePCLRawEx.core.dll

# 4. 部署
docker cp bin/Release/net6.0/ViewMate_merged.dll emby:/config/plugins/ViewMate.dll
docker restart emby
```

> ⚠️ `SQLitePCLRawEx.core.dll` 在 Emby 的 `/system/` 目录下，不通过 NuGet 分发。ILRepack 合并时必须从运行中的 Emby 容器复制。
> ⚠️ ILRepack 合并 4 个依赖 DLL 后约 6.8MB，放入 `/config/plugins/` 时保持文件名 `ViewMate.dll`。
> ✅ 编译零外部依赖——所有 API 类型来自 NuGet 的 `mediabrowser.server.core` 包，不需要提前从 Emby 容器提取任何 DLL。

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
