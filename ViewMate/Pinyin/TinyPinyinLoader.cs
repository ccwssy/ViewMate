using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using ViewMate.Common;

#nullable enable
namespace ViewMate.Pinyin
{
    /// <summary>
    /// Shared pinyin engine (phase-2 split of PinyinSearchService):
    /// reflection-loads TinyPinyin.dll via Lazy (ExecutionAndPublication, deferred
    /// until first use — never in any constructor), caches the phrase
    /// multi-pronunciation override table, and generates pinyin tokens.
    /// Replaces the duplicated LoadPinyinFunc/拼音加载 implementations that used
    /// to live in both PinyinSearchService and PinyinSortNameService.
    /// </summary>
    public static class TinyPinyinLoader
    {
        // Static logger for Lazy initializers — set by PinyinSearchService's ctor.
        private static ILogger _staticLogger = null!;

        public static void SetStaticLogger(ILogger logger)
        {
            _staticLogger = logger;
        }

        // ── Static caches (Lazy<T>, thread-safe) ──
        // Deferred loading: TinyPinyin.dll isn't guaranteed to be scanned by
        // Emby's assembly scanner at construction time, so both lazies are only
        // forced on first actual pinyin use.
        private static readonly Lazy<Dictionary<string, string>> _phraseOverridesLazy =
            new Lazy<Dictionary<string, string>>(LoadPhraseOverrides, LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Lazy<Func<char, string>> _getPinyinLazy =
            new Lazy<Func<char, string>>(LoadPinyinFunc, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// The TinyPinyin GetPinyin(char) delegate. Forcing this value loads the
        /// assembly; throws (FileNotFoundException etc.) when unavailable — callers
        /// that must not fail (e.g. PinyinSortNameService) wrap access in try/catch.
        /// </summary>
        public static Func<char, string> GetPinyinFunc => _getPinyinLazy.Value;

        public static (string spaced, string connected, string bigrams, string singleChars, string cjkBigrams) GeneratePinyin(string text)
        {
            if (string.IsNullOrEmpty(text)) return (null!, null!, null!, null!, null!);

            var sbSpaced = new StringBuilder();
            var sbConnected = new StringBuilder();
            var syllables = new List<string>();
            var cjkChars = new List<char>();
            bool hasChinese = false;

            for (int i = 0; i < text.Length; )
            {
                char ch = text[i];
                if (ch >= 0x4e00 && ch <= 0x9fff)
                {
                    string? matchedPhrase = null;
                    int matchLen = 0;
                    var overrides = _phraseOverridesLazy.Value;
                    foreach (var kvp in overrides)
                    {
                        if (i + kvp.Key.Length <= text.Length &&
                            text.Substring(i, kvp.Key.Length) == kvp.Key &&
                            kvp.Key.Length > matchLen)
                        {
                            matchedPhrase = kvp.Key;
                            matchLen = kvp.Key.Length;
                        }
                    }

                    if (matchedPhrase != null)
                    {
                        var overrideSegs = overrides[matchedPhrase].Split(' ');
                        foreach (var seg in overrideSegs)
                        {
                            sbSpaced.Append(seg);
                            sbSpaced.Append(' ');
                            sbConnected.Append(seg);
                            syllables.Add(seg);
                        }
                        foreach (char pc in matchedPhrase)
                            cjkChars.Add(pc);
                        i += matchLen;
                        hasChinese = true;
                        continue;
                    }

                    try
                    {
                        var p = _getPinyinLazy.Value(ch);
                        if (!string.IsNullOrEmpty(p))
                        {
                            sbSpaced.Append(p);
                            sbSpaced.Append(' ');
                            sbConnected.Append(p);
                            syllables.Add(p);
                            cjkChars.Add(ch);
                            hasChinese = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _staticLogger?.Warn("[PinyinSearch] TinyPinyin failed for char '{0}': {1}", ch, ex.Message);
                    }
                }
                i++;
            }

            if (!hasChinese) return (null!, null!, null!, null!, null!);

            var sbBigram = new StringBuilder();
            for (int i = 0; i + 1 < syllables.Count; i++)
            {
                sbBigram.Append(syllables[i]);
                sbBigram.Append(syllables[i + 1]);
                sbBigram.Append(' ');
            }

            var sbSingle = new StringBuilder();
            foreach (var ch in cjkChars)
                sbSingle.Append(ch).Append(' ');

            var sbCjkBigram = new StringBuilder();
            for (int i = 0; i + 1 < cjkChars.Count; i++)
                sbCjkBigram.Append(cjkChars[i]).Append(cjkChars[i + 1]).Append(' ');

            return (sbSpaced.ToString().TrimEnd(), sbConnected.ToString(),
                    sbBigram.ToString().TrimEnd(),
                    sbSingle.ToString().TrimEnd(), sbCjkBigram.ToString().TrimEnd());
        }

        public static bool IsCjkItem(BaseItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.Name)) return false;
            if (item.IsDisplayedAsFolder && !(item is Series)) return false;
            return TextUtil.ChineseRegex.IsMatch(item.Name);
        }

        // ── Static Lazy initializers ──
        private static Dictionary<string, string> LoadPhraseOverrides()
        {
            var dict = new Dictionary<string, string>();
            string[] probePaths =
            {
                "/config/plugins/pinyin-overrides.json",
                "plugins/pinyin-overrides.json",
                "../plugins/pinyin-overrides.json",
            };

            foreach (var path in probePaths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        var text = File.ReadAllText(path);
                        var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(text);
                        if (parsed != null)
                        {
                            dict = parsed;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _staticLogger?.Warn("[PinyinSearch] Failed to load pinyin overrides from {0}: {1}", path, ex.Message);
                    }
                }
            }
            return dict;
        }

        private static Func<char, string> LoadPinyinFunc()
        {
            string[] probePaths =
            {
                "/config/plugins/TinyPinyin.dll",
                "/system/TinyPinyin.dll",
                "plugins/TinyPinyin.dll",
                "../plugins/TinyPinyin.dll",
            };

            Assembly? asm = null;
            foreach (var path in probePaths)
            {
                if (File.Exists(path))
                {
                    asm = Assembly.Load(File.ReadAllBytes(path));
                    break;
                }
            }

            if (asm == null)
                throw new FileNotFoundException("TinyPinyin.dll not found in any probe path");

            var helperType = asm.GetType("TinyPinyin.PinyinHelper");
            if (helperType == null)
                throw new FileNotFoundException("TinyPinyin.PinyinHelper type not found");

            var method = helperType.GetMethod("GetPinyin", new[] { typeof(char) });
            if (method == null)
                throw new MissingMethodException("GetPinyin method not found");

            return (Func<char, string>)Delegate.CreateDelegate(
                typeof(Func<char, string>), null, method);
        }
    }
}
