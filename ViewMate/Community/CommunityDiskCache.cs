using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using MediaBrowser.Model.Logging;

namespace ViewMate.Community
{
    /// <summary>
    /// 社区查询的 6 小时磁盘缓存：未命中也缓存（负缓存），避免反复打社区接口。
    /// 位置与其它插件数据文件同序探测（/config/plugins/ → plugins/ → ../plugins/），
    /// 原子写（.tmp + File.Move(overwrite)）、UTF-8 无 BOM；任何失败只 Warn，不影响刷新管线。
    /// </summary>
    internal static class CommunityDiskCache
    {
        internal static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

        internal const string FileName = "viewmate-community-cache.json";

        private static readonly string[] ProbePaths =
        {
            "/config/plugins/" + FileName,
            "plugins/" + FileName,
            "../plugins/" + FileName,
        };

        private static readonly object Gate = new object();

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };

        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private class CacheFile
        {
            public int Version { get; set; } = 1;

            public Dictionary<string, CacheEntry> Entries { get; set; } = new Dictionary<string, CacheEntry>();
        }

        private class CacheEntry
        {
            public string At { get; set; }

            public CommunityLookupResult Result { get; set; }
        }

        /// <summary>探测当前生效的缓存文件路径；候选目录都不存在时回退到 /config/plugins/。</summary>
        internal static string ResolvePath()
        {
            foreach (var path in ProbePaths)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    return path;
                }
            }

            return ProbePaths[0];
        }

        /// <summary>取缓存；未命中或已过期（&gt;6h）返回 null。</summary>
        internal static CommunityLookupResult Get(string cacheKey, ILogger logger = null)
        {
            if (string.IsNullOrEmpty(cacheKey))
            {
                return null;
            }

            lock (Gate)
            {
                var file = ReadFile(logger);
                if (file?.Entries == null || !file.Entries.TryGetValue(cacheKey, out var entry) || entry?.Result == null)
                {
                    return null;
                }

                if (!DateTime.TryParse(entry.At, out var at) || DateTime.UtcNow - at.ToUniversalTime() > Ttl)
                {
                    return null;
                }

                return entry.Result;
            }
        }

        /// <summary>写缓存（含未命中结果）；失败只 Warn。</summary>
        internal static void Set(string cacheKey, CommunityLookupResult result, ILogger logger = null)
        {
            if (string.IsNullOrEmpty(cacheKey) || result == null)
            {
                return;
            }

            lock (Gate)
            {
                var file = ReadFile(logger) ?? new CacheFile();
                file.Entries ??= new Dictionary<string, CacheEntry>();
                file.Entries[cacheKey] = new CacheEntry
                {
                    At = DateTime.UtcNow.ToString("o"),
                    Result = result,
                };

                // 顺手清理过期条目，避免缓存文件无限增长。
                var stale = new List<string>();
                foreach (var pair in file.Entries)
                {
                    if (!DateTime.TryParse(pair.Value?.At, out var at) || DateTime.UtcNow - at.ToUniversalTime() > Ttl)
                    {
                        stale.Add(pair.Key);
                    }
                }

                foreach (var key in stale)
                {
                    file.Entries.Remove(key);
                }

                WriteFile(file, logger);
            }
        }

        /// <summary>缓存条目数（供校验用）。</summary>
        internal static int Count(ILogger logger = null)
        {
            lock (Gate)
            {
                return ReadFile(logger)?.Entries?.Count ?? 0;
            }
        }

        private static CacheFile ReadFile(ILogger logger)
        {
            var path = ResolvePath();
            try
            {
                if (!File.Exists(path))
                {
                    return new CacheFile();
                }

                var raw = File.ReadAllText(path, Encoding.UTF8);
                return string.IsNullOrWhiteSpace(raw)
                    ? new CacheFile()
                    : JsonSerializer.Deserialize<CacheFile>(raw, ReadOptions) ?? new CacheFile();
            }
            catch (Exception ex)
            {
                logger?.Warn("[Community] 读取缓存失败（按空缓存继续）：{0}", ex.Message);
                return new CacheFile();
            }
        }

        private static void WriteFile(CacheFile file, ILogger logger)
        {
            var path = ResolvePath();
            var tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, WriteOptions), new UTF8Encoding(false));
                File.Move(tmp, path, true);
            }
            catch (Exception ex)
            {
                logger?.Warn("[Community] 写入缓存失败（忽略）：{0}", ex.Message);
                try
                {
                    if (File.Exists(tmp))
                    {
                        File.Delete(tmp);
                    }
                }
                catch
                {
                    // 清理失败无关紧要。
                }
            }
        }
    }
}
