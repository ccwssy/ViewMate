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
    /// 共用的拼音引擎（PinyinSearchService 的第二阶段拆分）：
    /// 通过 Lazy 反射加载 TinyPinyin.dll（ExecutionAndPublication，
    /// 延迟到首次使用 —— 绝不在任何构造函数中加载），缓存词组
    /// 多音字覆盖表，并生成拼音 token。
    /// 用于替代原先重复的 LoadPinyinFunc/拼音加载 实现，那些实现曾
    /// 同时存在于 PinyinSearchService 与 PinyinSortNameService 中。
    /// </summary>
    public static class TinyPinyinLoader
    {
        // 供 Lazy 初始化器使用的静态 logger —— 由 PinyinSearchService 的构造函数设置。
        private static ILogger _staticLogger = null!;

        public static void SetStaticLogger(ILogger logger)
        {
            _staticLogger = logger;
        }

        // ── 静态缓存（Lazy<T>，线程安全） ──
        // 延迟加载：无法保证 Emby 的程序集扫描器在构造时就扫到
        // TinyPinyin.dll，因此这两个 Lazy 都只会在真正用到拼音时
        // 才被强制求值。
        private static readonly Lazy<Dictionary<string, string>> _phraseOverridesLazy =
            new Lazy<Dictionary<string, string>>(LoadPhraseOverrides, LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Lazy<Func<char, string>> _getPinyinLazy =
            new Lazy<Func<char, string>>(LoadPinyinFunc, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// TinyPinyin 的 GetPinyin(char) 委托。强制求值此属性会加载程序集；
        /// 不可用时会抛异常（FileNotFoundException 等）—— 不能失败的调用方
        /// （例如 PinyinSortNameService）需用 try/catch 包住访问。
        /// </summary>
        public static Func<char, string> GetPinyinFunc => _getPinyinLazy.Value;

        public static (string spaced, string connected, string bigrams, string singleChars, string cjkBigrams, string initials, string initialsSuffixes) GeneratePinyin(string text)
        {
            if (string.IsNullOrEmpty(text)) return (null!, null!, null!, null!, null!, null!, null!);

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

            if (!hasChinese) return (null!, null!, null!, null!, null!, null!, null!);

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

            // 首字母：每个汉字音节的首字母，转大写后
            // 不加分隔符直接拼接（音节已应用 pinyin-overrides 的词组
            // 校正，因此 重庆 → CQ）。非中文输入不会走到这个列表。
            var sbInitials = new StringBuilder();
            foreach (var syllable in syllables)
            {
                if (!string.IsNullOrEmpty(syllable))
                    sbInitials.Append(char.ToUpperInvariant(syllable[0]));
            }
            string initials = sbInitials.ToString();

            // 首字母整串的全部后缀（"ALTZDTS" →
            // "LTZDTS TZDTS ZDTS DTS TS"），长度为 1 的后缀省略。
            // 任意长度、任意位置的缩写都是某个后缀的前缀，配合 Emby 的
            // 前缀查询即可命中（如 "zdts" 命中后缀 "ZDTS"）。
            var sbInitialSuffix = new StringBuilder();
            for (int i = 1; i + 1 < initials.Length; i++)
                sbInitialSuffix.Append(initials.Substring(i)).Append(' ');

            return (sbSpaced.ToString().TrimEnd(), sbConnected.ToString(),
                    sbBigram.ToString().TrimEnd(),
                    sbSingle.ToString().TrimEnd(), sbCjkBigram.ToString().TrimEnd(),
                    initials, sbInitialSuffix.ToString().TrimEnd());
        }

        public static bool IsCjkItem(BaseItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.Name)) return false;
            if (item.IsDisplayedAsFolder && !(item is Series)) return false;
            return TextUtil.ChineseRegex.IsMatch(item.Name);
        }

        // ── 静态 Lazy 初始化器 ──
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
