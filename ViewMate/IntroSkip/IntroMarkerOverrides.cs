using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace ViewMate.IntroSkip
{
    /// <summary>
    /// 手动标记覆盖表（viewmate-intro-overrides.json）的唯一读写入口。
    ///
    /// 覆盖表只记录"用户手动改过哪些集"，是自动逻辑的避让依据：
    /// 生效值始终写在 Emby 的 Chapters3（Emby 只认 MarkerType，往章节表塞墓碑会被读取方当成真实标记）。
    /// locked = 手动写入过，自动检测/漏集补打都不得覆盖；suppressed = 手动清除过，自动逻辑不得填回。
    /// 解析失败只告警并视为空表 —— 设置页不能因为一个坏文件而崩。
    /// </summary>
    public static class IntroMarkerOverrides
    {
        internal const string FileName = "viewmate-intro-overrides.json";
        internal const int SchemaVersion = 1;

        // 路径探测顺序与插件其它数据文件一致：取第一个存在的目录，写回实际用到的那个。
        private static readonly string[] ProbePaths =
        {
            "/config/plugins/" + FileName,
            "plugins/" + FileName,
            "../plugins/" + FileName,
        };

        private static readonly object Sync = new object();

        // 写盘格式：2 空格缩进 + 不转义中文 + 省略空值，便于人工查看与比对。
        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private static Dictionary<string, EpisodeOverride> _episodes;

        /// <summary>单集的手动标记记录。</summary>
        public sealed class EpisodeOverride
        {
            [JsonPropertyName("startTicks")]
            public long? StartTicks { get; set; }

            [JsonPropertyName("endTicks")]
            public long? EndTicks { get; set; }

            [JsonPropertyName("creditsTicks")]
            public long? CreditsTicks { get; set; }

            [JsonPropertyName("locked")]
            public bool Locked { get; set; }

            [JsonPropertyName("suppressed")]
            public bool Suppressed { get; set; }

            [JsonPropertyName("savedAt")]
            public string SavedAt { get; set; }
        }

        private sealed class OverridesFile
        {
            [JsonPropertyName("version")]
            public int Version { get; set; } = SchemaVersion;

            [JsonPropertyName("episodes")]
            public Dictionary<string, EpisodeOverride> Episodes { get; set; }
        }

        /// <summary>探测当前生效的文件路径；候选目录都不存在时回退到 /config/plugins/。</summary>
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

        /// <summary>该集是否被手动接管（locked 或 suppressed 都算）。</summary>
        public static bool IsProtected(long itemId)
        {
            var entry = Get(itemId);
            return entry != null && (entry.Locked || entry.Suppressed);
        }

        /// <summary>取该集的手动记录；不存在返回 null。</summary>
        public static EpisodeOverride Get(long itemId)
        {
            var key = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            lock (Sync)
            {
                var entries = Current();
                return entries.TryGetValue(key, out var entry) ? entry : null;
            }
        }

        /// <summary>记录一次手动写入（locked = true），覆盖该集旧记录。</summary>
        public static void MarkLocked(long itemId, long? startTicks, long? endTicks, long? creditsTicks)
        {
            var key = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Mutate(entries =>
            {
                entries[key] = new EpisodeOverride
                {
                    StartTicks = startTicks,
                    EndTicks = endTicks,
                    CreditsTicks = creditsTicks,
                    Locked = true,
                    Suppressed = false,
                    SavedAt = DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture),
                };
            });
        }

        /// <summary>记录一次手动清除（墓碑：禁止自动逻辑填回）。</summary>
        public static void MarkSuppressed(long itemId)
        {
            var key = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Mutate(entries =>
            {
                entries[key] = new EpisodeOverride
                {
                    Suppressed = true,
                    Locked = false,
                    SavedAt = DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture),
                };
            });
        }

        /// <summary>解除接管：删除该键后恢复受自动逻辑管理。键不存在时是空操作。</summary>
        public static void Remove(long itemId)
        {
            var key = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Mutate(entries =>
            {
                if (entries.ContainsKey(key))
                {
                    entries.Remove(key);
                }
            });
        }

        /// <summary>强制重新加载（自动逻辑改写覆盖表后，测试与排障用）。</summary>
        internal static void Reload()
        {
            lock (Sync)
            {
                _episodes = Load();
            }
        }

        /// <summary>
        /// 自愈每轮开工前的只读刷新。与 <see cref="Reload"/> 的差别是
        /// **解析失败时保留上次成功加载的缓存**并返回 false（由调用方决定本轮行为，
        /// 自愈选择整轮跳过、不做任何修补）—— 一次坏文件读取不会把共享缓存清空，
        /// 设置页与随后的保存路径不会因此丢条目。
        /// </summary>
        public static bool TryRefresh()
        {
            lock (Sync)
            {
                var loaded = LoadCore(out var ok);
                if (ok)
                {
                    _episodes = loaded;
                }

                return ok;
            }
        }

        /// <summary>
        /// 覆盖表全部条目的只读快照（按 itemId 升序），供自愈服务逐条校验。
        /// 只做浅拷贝：记录对象在写入侧是"整体替换"而非原地修改，拿到即安全。
        /// 非法/非数字的键会被跳过（Load 已把非对象值滤掉）。
        /// </summary>
        public static List<KeyValuePair<long, EpisodeOverride>> Snapshot()
        {
            lock (Sync)
            {
                return SnapshotLocked();
            }
        }

        private static List<KeyValuePair<long, EpisodeOverride>> SnapshotLocked()
        {
            var result = new List<KeyValuePair<long, EpisodeOverride>>();

            foreach (var pair in Current())
            {
                if (long.TryParse(pair.Key, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var itemId))
                {
                    result.Add(new KeyValuePair<long, EpisodeOverride>(itemId, pair.Value));
                }
            }

            result.Sort((a, b) => a.Key.CompareTo(b.Key));
            return result;
        }

        private static Dictionary<string, EpisodeOverride> Current()
        {
            if (_episodes == null)
            {
                _episodes = Load();
            }

            return _episodes;
        }

        private static void Mutate(Action<Dictionary<string, EpisodeOverride>> change)
        {
            lock (Sync)
            {
                var entries = Current();
                var before = JsonSerializer.Serialize(entries, WriteOptions);
                change(entries);
                var after = JsonSerializer.Serialize(entries, WriteOptions);
                if (before == after)
                {
                    return;
                }

                Save(entries);
            }
        }

        private static Dictionary<string, EpisodeOverride> Load()
        {
            return LoadCore(out _);
        }

        /// <summary>
        /// 解析覆盖表。<paramref name="ok"/> = false 表示文件存在但无法解析
        /// （此时返回空表，调用方自行决定"视为空表"还是"保留旧缓存并跳过"）。
        /// </summary>
        private static Dictionary<string, EpisodeOverride> LoadCore(out bool ok)
        {
            var result = new Dictionary<string, EpisodeOverride>();
            var path = ResolvePath();
            ok = false;

            try
            {
                if (!File.Exists(path))
                {
                    ok = true;   // 文件不存在 = 正常的空表
                    return result;
                }

                using (var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object
                        || !document.RootElement.TryGetProperty("episodes", out var episodes)
                        || episodes.ValueKind != JsonValueKind.Object)
                    {
                        ok = true;   // 合法 JSON 但不是覆盖表结构 = 空表
                        return result;
                    }

                    foreach (var property in episodes.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        result[property.Name] = new EpisodeOverride
                        {
                            StartTicks = ReadLong(property.Value, "startTicks"),
                            EndTicks = ReadLong(property.Value, "endTicks"),
                            CreditsTicks = ReadLong(property.Value, "creditsTicks"),
                            Locked = ReadBool(property.Value, "locked"),
                            Suppressed = ReadBool(property.Value, "suppressed"),
                            SavedAt = ReadString(property.Value, "savedAt"),
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Warn("读取 {0} 失败：{1}（视为空表）", path, ex.Message);
                return new Dictionary<string, EpisodeOverride>();
            }

            ok = true;
            return result;
        }

        private static void Save(Dictionary<string, EpisodeOverride> entries)
        {
            var path = ResolvePath();
            var tmpPath = path + ".tmp";

            try
            {
                var payload = new OverridesFile { Version = SchemaVersion, Episodes = entries };
                var text = JsonSerializer.Serialize(payload, WriteOptions).Replace("\r\n", "\n") + "\n";
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(tmpPath, text, new UTF8Encoding(false));
                File.Move(tmpPath, path, true);
            }
            catch (Exception ex)
            {
                Warn("写入 {0} 失败：{1}", path, ex.Message);
                try
                {
                    if (File.Exists(tmpPath))
                    {
                        File.Delete(tmpPath);
                    }
                }
                catch (Exception cleanupEx)
                {
                    Warn("清理 {0} 失败：{1}", tmpPath, cleanupEx.Message);
                }
            }
        }

        private static long? ReadLong(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            return value.TryGetInt64(out var ticks) ? ticks : (long?)null;
        }

        private static bool ReadBool(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value)
                && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                && value.GetBoolean();
        }

        private static string ReadString(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return value.GetString();
        }

        private static void Warn(string message, params object[] args)
        {
            try
            {
                Plugin.Instance?.Logger?.Warn("[IntroMarkerOverrides] " + message, args);
            }
            catch (Exception)
            {
                // 日志失败不影响功能。
            }
        }
    }
}
