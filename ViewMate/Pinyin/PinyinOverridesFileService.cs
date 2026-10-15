using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ViewMate.Properties;

namespace ViewMate.Pinyin
{
    /// <summary>
    /// pinyin-overrides.json 的定位、校验、容错修复、护栏、备份与原子写入。
    /// 所有失败都以错误文案返回，不向框架层抛异常。
    /// </summary>
    internal class PinyinOverridesFileService
    {
        /// <summary>文件缺失时弹窗预填的空对象模板。</summary>
        internal const string EmptyTemplate = "{\n  \n}";

        /// <summary>
        /// 文件缺失时自动落盘的示例模板（T14）。必须是合法 JSON 对象（键值均为字符串）：
        /// 解析链路为 JsonDocument.Parse（严格）+ JsonSerializer.Deserialize，
        /// **不支持 `//` 注释**，所以示例语义只能由规则本身承载。
        /// 选这 4 条是为了「零副作用」——不得改变任何未预期位置的读音：
        /// 「会」默认读音本就是 hui，只示范单字写法；
        /// 「重庆/银行/会计」示范词组特例覆盖（重默认 zhong、行默认 xing、会默认 hui）。
        /// 刻意不放「重」→ zhong 这类单字兜底，否则「重生」「重逢」会被误读。
        /// </summary>
        internal const string SampleTemplate =
            "{\n" +
            "  \"重庆\": \"chong qing\",\n" +
            "  \"会\": \"hui\",\n" +
            "  \"银行\": \"yin hang\",\n" +
            "  \"会计\": \"kuai ji\"\n" +
            "}\n";


        /// <summary>备份保留数量：按时间戳从新到旧计数，多出来的删掉。</summary>
        internal const int MaxBackups = 5;

        /// <summary>规则条数上限，防畸形输入把拼音索引撑爆。</summary>
        internal const int MaxRules = 500;

        /// <summary>键长度上限（字符）。</summary>
        internal const int MaxKeyLength = 32;

        /// <summary>值长度上限（字符）。</summary>
        internal const int MaxValueLength = 64;

        /// <summary>错误文案里回显的行原文长度上限；超出按「前半…后半」截断。</summary>
        internal const int LineContextMaxLength = 60;

        // 键过长时在文案里展示的前缀长度。
        private const int KeyPreviewLength = 8;

        // 值允许的声调字符（大小写成对，拼音串只会由字母、数字、空格与这些符号组成）。
        private const string PinyinAccents = "āáǎàēéěèīíǐìōóǒòūúǔùǖǘǚǜüǹĀÁǍÀĒÉĚÈĪÍǏÌŌÓǑÒŪÚǓÙǕǗǙǛÜǸ";

        // 路径探测顺序与 TinyPinyinLoader.LoadPhraseOverrides 一致，取第一个「目录存在」的候选。
        private static readonly string[] ProbePaths =
        {
            "/config/plugins/pinyin-overrides.json",
            "plugins/pinyin-overrides.json",
            "../plugins/pinyin-overrides.json",
        };

        // 写盘格式：2 空格缩进、不转义中文，与宿主现有文件的排版保持一致。
        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };

        private readonly ILogger _logger;

        public PinyinOverridesFileService(ILogger logger)
        {
            _logger = logger;
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

        /// <summary>
        /// 把示例模板写到 <paramref name="path"/>：复用 Save() 的原子三步
        /// （&lt;path&gt;.tmp + File.Move(overwrite: true)）、UTF-8 无 BOM 编码，
        /// 并回读校验落盘内容与模板逐字节一致。成功返回规则条数，失败返回 -1。
        /// 只写不抛异常 —— 插件启动路径绝不能因为一个数据文件而失败。
        /// </summary>
        internal static int WriteSampleTemplate(string path, ILogger logger)
        {
            var tmpPath = path + ".tmp";
            try
            {
                File.WriteAllText(tmpPath, SampleTemplate, new UTF8Encoding(false));
                File.Move(tmpPath, path, true);

                var bytes = File.ReadAllBytes(path);
                if (!bytes.SequenceEqual(StringToUtf8Bytes(SampleTemplate)))
                {
                    logger?.Warn("[PinyinOverrides] 写入示例模板后回读校验不一致：{0}", path);
                    return -1;
                }

                return CountRules(SampleTemplate);
            }
            catch (Exception ex)
            {
                logger?.Warn("[PinyinOverrides] 写入示例模板 {0} 失败：{1}", path, ex.Message);
                try
                {
                    if (File.Exists(tmpPath))
                    {
                        File.Delete(tmpPath);
                    }
                }
                catch (Exception cleanupEx)
                {
                    logger?.Warn("[PinyinOverrides] 清理 {0} 的临时文件失败：{1}", path, cleanupEx.Message);
                }

                return -1;
            }
        }

        private static byte[] StringToUtf8Bytes(string text)
        {
            return new UTF8Encoding(false).GetBytes(text);
        }

        // 模板是自己写的常量，条数直接数顶层键；解析失败按 0 计（日志仍照记）。
        private static int CountRules(string content)
        {
            try
            {
                using (var document = JsonDocument.Parse(content))
                {
                    return document.RootElement.ValueKind == JsonValueKind.Object
                        ? document.RootElement.EnumerateObject().Count()
                        : 0;
                }
            }
            catch (JsonException)
            {
                return 0;
            }
        }

        /// <summary>规则条数；文件缺失或不可解析时返回 -1。状态行每次渲染都会调用，保持轻量。</summary>
        internal static int TryGetRuleCount()
        {
            try
            {
                var path = ResolvePath();
                if (!File.Exists(path))
                {
                    return -1;
                }

                using (var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)))
                {
                    return document.RootElement.ValueKind == JsonValueKind.Object
                        ? document.RootElement.EnumerateObject().Count()
                        : -1;
                }
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>状态行文案：已存在（N 条规则）/ 未找到文件（保存后创建）。</summary>
        internal static string DescribeStatus()
        {
            var path = ResolvePath();
            var ruleCount = TryGetRuleCount();
            return ruleCount >= 0
                ? string.Format(UiText("PinyinOverrides_StatusExists"), path, ruleCount)
                : string.Format(UiText("PinyinOverrides_StatusMissing"), path);
        }

        /// <summary>
        /// 启动时兜底：覆盖文件缺失就落一份示例模板，让用户打开弹窗即有可照抄的规则。
        /// 前置条件与红线：
        /// ① 只做一次 File.Exists 判断 + 至多一次写入，不做多余 IO，也不做定时检查；
        /// ② 文件已存在 → 一律不动（内容为空、非法、只有 1 条规则都不碰），且不记任何日志；
        /// ③ 目标目录不存在 → 只 Warn 跳过，绝不新建目录；
        /// ④ 任何失败只 Warn，不抛异常、不影响插件加载。
        /// </summary>
        /// <returns>写入的规则条数；跳过或失败时返回 -1。</returns>
        internal static int EnsureSampleTemplateIfMissing()
        {
            return EnsureSampleTemplateIfMissing(Plugin.Instance?.Logger);
        }

        /// <summary>上面那条的 logger 显式入口（自证脚本反射调用此重载，日志与线上同源）。</summary>
        internal static int EnsureSampleTemplateIfMissing(ILogger logger)
        {
            string path;
            try
            {
                path = ResolvePath();
            }
            catch (Exception ex)
            {
                logger?.Warn("[PinyinOverrides] 解析覆盖文件路径失败：{0}", ex.Message);
                return -1;
            }

            if (string.IsNullOrEmpty(path))
            {
                logger?.Warn("[PinyinOverrides] 覆盖文件路径为空，跳过示例模板写入");
                return -1;
            }

            if (File.Exists(path))
            {
                // 红线：已存在就绝不触碰 —— 也绝不记日志。
                return -1;
            }

            string directory;
            try
            {
                directory = Path.GetDirectoryName(path);
            }
            catch (Exception ex)
            {
                logger?.Warn("[PinyinOverrides] 解析 {0} 的目录失败：{1}", path, ex.Message);
                return -1;
            }

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                // 不建目录：宿主布局异常时宁可不写，也不在磁盘上乱造路径。
                logger?.Warn("[PinyinOverrides] 未找到 {0}，且目录 {1} 不存在，跳过示例模板写入", path, directory);
                return -1;
            }

            var ruleCount = WriteSampleTemplate(path, logger);
            if (ruleCount < 0)
            {
                return -1;
            }

            logger?.Info("[PinyinOverrides] 未找到 {0}，已写入示例模板（{1} 条规则）", path, ruleCount);
            return ruleCount;
        }

        /// <summary>本地化文案读取：统一按插件 UI 语言取值。</summary>
        internal static string UiText(string key)
        {
            return Resources.ResourceManager.GetString(key, Plugin.Instance?.DefaultUICulture) ?? string.Empty;
        }

        /// <summary>读取文件全文；缺失或读取失败时返回空模板。不做任何改写，用户磁盘上的原文保持原样。</summary>
        internal string ReadAll()
        {
            var path = ResolvePath();
            if (!File.Exists(path))
            {
                return EmptyTemplate;
            }

            try
            {
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger?.Warn("[PinyinOverrides] 读取 {0} 失败：{1}", path, ex.Message);
                return EmptyTemplate;
            }
        }

        /// <summary>严格校验（不修复）：JSON 可解析、顶层为对象、值均为字符串；通过时返回 null。</summary>
        internal static string Validate(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return UiText("PinyinOverrides_ErrorInvalidJson");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(content);
            }
            catch (JsonException)
            {
                return UiText("PinyinOverrides_ErrorInvalidJson");
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return UiText("PinyinOverrides_ErrorNotObject");
                }

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return string.Format(UiText("PinyinOverrides_ErrorValueNotString"), property.Name);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 保存路径专用：先严格解析，失败时按「行尾多余逗号 → 全角符号」顺序尝试自动修正，
        /// 再做键值护栏检查。成功时 rules 为最终规则表、repairNote 非空表示发生过自动修正；
        /// 失败时 error 为具体原因（行列 + 中文说明），repairNote 为 null。
        /// </summary>
        internal static bool TryParseForSave(string raw, out Dictionary<string, string> rules, out string repairNote, out string error)
        {
            rules = null;
            repairNote = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = UiText("PinyinOverrides_ErrorInvalidJson");
                return false;
            }

            // 阶段 1（严格）：原文能过就绝不动它。
            if (TryParseStrict(raw, out rules, out error))
            {
                error = CheckLimits(rules);
                return error == null;
            }

            var strictError = error;

            // 阶段 2a：删掉对象/数组末尾多余的逗号后重解析。
            var trimmed = RemoveTrailingCommas(raw, out var trailingCommaRepaired);
            if (trailingCommaRepaired && TryParseStrict(trimmed, out var trimmedRules, out _))
            {
                error = CheckLimits(trimmedRules);
                if (error != null)
                {
                    return false;
                }

                rules = trimmedRules;
                repairNote = UiText("PinyinOverrides_RepairedTrailingComma");
                return true;
            }

            // 阶段 2b：在 2a 的结果上把全角 ，："" 换回半角后重解析。
            var normalized = NormalizeFullWidth(trimmed, out var fullWidthRepaired);
            if (fullWidthRepaired && TryParseStrict(normalized, out var normalizedRules, out _))
            {
                error = CheckLimits(normalizedRules);
                if (error != null)
                {
                    return false;
                }

                rules = normalizedRules;
                repairNote = UiText("PinyinOverrides_RepairedFullWidth");
                return true;
            }

            // 修复后仍失败：回传原文的解析错误，行列号才对得上用户输入。
            error = strictError;
            return false;
        }

        // 严格解析：JSON 可解析 + 顶层为对象 + 值全为字符串；失败时给出具体原因。
        private static bool TryParseStrict(string text, out Dictionary<string, string> rules, out string error)
        {
            rules = null;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(text);
            }
            catch (JsonException ex)
            {
                error = DescribeJsonError(ex, text);
                return false;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = UiText("PinyinOverrides_ErrorNotObject");
                    return false;
                }

                var parsed = new Dictionary<string, string>();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        error = string.Format(UiText("PinyinOverrides_ErrorValueNotString"), property.Name);
                        return false;
                    }

                    parsed[property.Name] = property.Value.GetString() ?? string.Empty;
                }

                rules = parsed;
            }

            error = null;
            return true;
        }

        // JsonException → 「第 N 行附近：这一行是「<该行原文>」—— <中文原因>」。
        // 编辑框（Emby [EditMultiline] 渲染的 <textarea>）没有行号栏，框架也不提供带行号的编辑器类型，
        // 所以必须把出错行的原文一并给出，用户才能在框里搜到那行（T11 §2.6）。
        // T15 §2.2（F2）：JsonDocument.Parse 报的是"读到哪个 token 才发现不对"——缺逗号类错误的
        // 坐标落在**下一个 token**（下一行的行首键）处，真实错误在上一行行尾。
        // 修复规则（确定性映射，非猜测）：原因类别 = 缺逗号 且 报错列号落在报错行行首
        // → 行号 -1、行原文取上一行、原因换成「行尾缺少逗号」的说法。其余错误一律按原位置。
        private static string DescribeJsonError(JsonException ex, string text)
        {
            var reason = ClassifyJsonError(ex.Message);
            var lineIndex = ex.LineNumber.HasValue ? (int)ex.LineNumber.Value : -1;

            if (reason == JsonErrorReason.MissingComma
                && lineIndex > 0
                && IsAtLineStart(text, lineIndex, ex.BytePositionInLine))
            {
                var target = lineIndex - 1;
                return string.Format(
                    UiText("PinyinOverrides_ErrorDetailFormat"),
                    target + 1,
                    WrapLineContext(DescribeLineAt(text, target)),
                    UiText("PinyinOverrides_ErrorMissingCommaAtEndOfLine"));
            }

            return string.Format(
                UiText("PinyinOverrides_ErrorDetailFormat"),
                lineIndex < 0 ? 1 : lineIndex + 1,
                WrapLineContext(DescribeLineAt(text, lineIndex)),
                DescribeJsonErrorReason(reason, ex.Message));
        }

        // 列号是否落在行首：BytePositionInLine 是**UTF-8 字节**偏移，
        // 阈值 = 行首首个非空白字符的字节位置 + 2（容差 2 字节，覆盖解析器把 token 起点
        // 前后挪一两个字节的情形；行首全角缩进按其字节数计入）。S7 这类"同一行内漏逗号"
        // 的列号落在行中，因此不会误跳到上一行。
        private static bool IsAtLineStart(string text, int lineIndex, long? bytePositionInLine)
        {
            if (!bytePositionInLine.HasValue) return false;

            var lines = SplitLines(text);
            if (lineIndex < 0 || lineIndex >= lines.Length) return false;

            var line = lines[lineIndex];
            var firstNonWhitespace = 0;
            while (firstNonWhitespace < line.Length && char.IsWhiteSpace(line[firstNonWhitespace]))
            {
                firstNonWhitespace++;
            }

            var startBytes = Encoding.UTF8.GetByteCount(line.Substring(0, firstNonWhitespace));
            return bytePositionInLine.Value <= startBytes + 2;
        }

        // 出错行原文的包装（资源键 PinyinOverrides_ErrorLineContext，4 件套已同步）。
        // 这里用「资源优先、代码兜底」：即便某个语言分支的卫星资源没带上该键，
        // 也保证用户能看到「这一行是「…」」而不是一行裸文本。
        private static readonly string FallbackLineContextFormat = "这一行是「{0}」";

        private static string WrapLineContext(string line)
        {
            var format = UiText("PinyinOverrides_ErrorLineContext");
            if (string.IsNullOrEmpty(format) || format.IndexOf("{0}", StringComparison.Ordinal) < 0)
            {
                format = FallbackLineContextFormat;
            }

            return string.Format(format, line);
        }

        // 取指定行（0 基）的原文；越界（含行号缺失时的 -1）回退到最相关的一行
        // （有内容的最后一行），保证任何行内定位类错误都带出可搜索的原文。
        private static string DescribeLineAt(string text, int index)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var lines = SplitLines(text);
            if (index < 0 || index >= lines.Length)
            {
                index = LastNonEmptyLine(lines);
            }

            if (index < 0)
            {
                return string.Empty;
            }

            return ShortenLine(lines[index].Trim());
        }

        // 行拆分口径：CRLF / CR 一律按 LF 处理（DescribeLineAt 与 IsAtLineStart 共用）。
        private static string[] SplitLines(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        // 行原文超过 LineContextMaxLength 时截断为「前半…后半」，保留首尾。
        private static string ShortenLine(string line)
        {
            if (line.Length <= LineContextMaxLength)
            {
                return line;
            }

            const int head = LineContextMaxLength / 2;
            var tail = LineContextMaxLength - head - 1;
            return line.Substring(0, head) + "…" + line.Substring(line.Length - tail);
        }

        private static int LastNonEmptyLine(string[] lines)
        {
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                {
                    return i;
                }
            }

            return lines.Length - 1;
        }

        /// <summary>解析失败的原因类别；只有 <see cref="JsonErrorReason.MissingComma"/> 参与 §2.2 的"行号 -1"映射。</summary>
        private enum JsonErrorReason
        {
            MissingComma,
            MissingColon,
            MissingQuote,
            Unclosed,
            Other,
        }

        // 英文 message → 原因类别；认不出来的按原文附上，避免用户只看到「格式无效」。
        // T15 §2.2 实证（探针 + 容器内真实链路，System.Text.Json 6/10 行为一致）：
        //   · 两个键值对之间漏逗号 → `'"' is invalid after a value. Expected either ',', '}', or ']'.`
        //     （报错 token 是**下一个键**的引号，坐标因此在下一行行首）；
        //   · 漏逗号且报错行以非 ASCII 开头 → `'0xE3' is an invalid start of a property name. Expected a '"'.`；
        //   · 键值之间漏冒号 → `'"' is invalid after a property name. Expected a ':'.`
        // 缺冒号的 message 同时含 "invalid after a property name"，故**先判缺冒号**再判缺逗号，
        // 否则 §2.2 S2 的"缺冒号"会被误报成"缺逗号"。
        private static JsonErrorReason ClassifyJsonError(string message)
        {
            var text = message ?? string.Empty;

            if (Contains(text, "Expected a ':'"))
            {
                return JsonErrorReason.MissingColon;
            }

            if (Contains(text, "Expected a ','")
                || Contains(text, "Expected either")
                || Contains(text, "Expected a delimiter")
                || Contains(text, "invalid after a value")
                || Contains(text, "invalid start of a property name")
                || Contains(text, "invalid after a property name"))
            {
                return JsonErrorReason.MissingComma;
            }

            if (Contains(text, "invalid start of a value"))
            {
                return JsonErrorReason.MissingQuote;
            }

            if (Contains(text, "end of data")
                || Contains(text, "end of the JSON")
                || Contains(text, "should be closed")
                || Contains(text, "significant depth")
                || Contains(text, "not closed")
                || Contains(text, "Unexpected end")
                || Contains(text, "end of string"))
            {
                return JsonErrorReason.Unclosed;
            }

            return JsonErrorReason.Other;
        }

        private static string DescribeJsonErrorReason(JsonErrorReason reason, string rawMessage)
        {
            switch (reason)
            {
                case JsonErrorReason.MissingComma:
                    return UiText("PinyinOverrides_ErrorMissingComma");
                case JsonErrorReason.MissingColon:
                    return UiText("PinyinOverrides_ErrorMissingColon");
                case JsonErrorReason.MissingQuote:
                    return UiText("PinyinOverrides_ErrorMissingQuote");
                case JsonErrorReason.Unclosed:
                    return UiText("PinyinOverrides_ErrorUnclosed");
                default:
                    return rawMessage ?? string.Empty;
            }
        }

        private static bool Contains(string text, string token)
        {
            return text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // 删除对象/数组末尾多余逗号：只处理字符串字面量之外、且后面（跳过空白）紧跟 } 或 ] 的逗号。
        internal static string RemoveTrailingCommas(string text, out bool repaired)
        {
            var builder = new StringBuilder(text.Length);
            var inString = false;
            var escaped = false;
            repaired = false;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];

                if (inString)
                {
                    builder.Append(ch);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (ch == '\\')
                    {
                        escaped = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    builder.Append(ch);
                    continue;
                }

                if (ch == ',' && IsTrailingComma(text, i))
                {
                    repaired = true;
                    continue;
                }

                builder.Append(ch);
            }

            return builder.ToString();
        }

        // 逗号之后（跳过空白）是 } 或 ]，或者只剩空白到文件尾 —— 都是 JSON 不接受的写法。
        private static bool IsTrailingComma(string text, int index)
        {
            for (var i = index + 1; i < text.Length; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    return text[i] == '}' || text[i] == ']';
                }
            }

            return true;
        }

        // 全角标点回归半角：，→, ：→: “ ”→"（同样只处理字符串字面量之外）。
        internal static string NormalizeFullWidth(string text, out bool repaired)
        {
            var builder = new StringBuilder(text.Length);
            var inString = false;
            var escaped = false;
            repaired = false;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];

                if (inString)
                {
                    builder.Append(ch);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (ch == '\\')
                    {
                        escaped = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    builder.Append(ch);
                    continue;
                }

                switch (ch)
                {
                    case '，':
                        builder.Append(',');
                        repaired = true;
                        break;
                    case '：':
                        builder.Append(':');
                        repaired = true;
                        break;
                    case '“':
                    case '”':
                        builder.Append('"');
                        repaired = true;
                        break;
                    default:
                        builder.Append(ch);
                        break;
                }
            }

            return builder.ToString();
        }

        // 护栏：只作用于新保存的内容，磁盘上已存在的历史文件不受影响（ReadAll 不拦截）。通过时返回 null。
        internal static string CheckLimits(Dictionary<string, string> rules)
        {
            if (rules == null)
            {
                return UiText("PinyinOverrides_ErrorNotObject");
            }

            if (rules.Count > MaxRules)
            {
                return string.Format(UiText("PinyinOverrides_ErrorTooManyRules"), rules.Count, MaxRules);
            }

            var index = 0;
            foreach (var pair in rules)
            {
                index++;

                if (pair.Key.Length > MaxKeyLength)
                {
                    return string.Format(UiText("PinyinOverrides_ErrorKeyTooLong"), index, Shorten(pair.Key), MaxKeyLength);
                }

                if (!IsValidKey(pair.Key))
                {
                    return string.Format(UiText("PinyinOverrides_ErrorBadKey"), index);
                }

                var value = pair.Value ?? string.Empty;
                if (value.Length > MaxValueLength)
                {
                    return string.Format(UiText("PinyinOverrides_ErrorValueTooLong"), index, pair.Key, MaxValueLength);
                }

                foreach (var ch in value)
                {
                    if (!IsAllowedValueChar(ch))
                    {
                        return string.Format(UiText("PinyinOverrides_ErrorValueCharset"), index, pair.Key, ch);
                    }
                }
            }

            return null;
        }

        // 键：非空、不含空白、不含 " 与 \（会破坏 JSON 结构或造成歧义）。
        private static bool IsValidKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            foreach (var ch in key)
            {
                if (char.IsWhiteSpace(ch) || ch == '"' || ch == '\\')
                {
                    return false;
                }
            }

            return true;
        }

        // 值：ASCII 字母数字 + 空格 + 声调字符。
        private static bool IsAllowedValueChar(char ch)
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == ' ')
            {
                return true;
            }

            return PinyinAccents.IndexOf(ch) >= 0;
        }

        private static string Shorten(string key)
        {
            return key.Length > KeyPreviewLength ? key.Substring(0, KeyPreviewLength) + "…" : key;
        }

        /// <summary>
        /// 保存：宽松解析 + 自动修正 + 护栏 → 备份旧文件 → 清理历史备份 → 原子写入。
        /// 成功返回 null 并回传修复说明（无修复为 null）；失败返回可展示的错误文案。
        /// </summary>
        internal string Save(string content, out string repairNote)
        {
            repairNote = null;

            if (!TryParseForSave(content, out var rules, out var note, out var error))
            {
                return error;
            }

            var path = ResolvePath();
            var tmpPath = path + ".tmp";
            try
            {
                var text = Serialize(rules);
                Backup(path);
                TrimBackups(path);

                File.WriteAllText(tmpPath, text, new UTF8Encoding(false));
                File.Move(tmpPath, path, true);
                repairNote = note;
                if (!string.IsNullOrEmpty(note))
                {
                    _logger?.Info("[PinyinOverrides] 保存时已自动修正：{0}", note);
                }

                _logger?.Info("[PinyinOverrides] 已写入 {0}（{1} 条规则，{2} 字节）", path, rules.Count, Encoding.UTF8.GetByteCount(text));
                return null;
            }
            catch (Exception ex)
            {
                _logger?.Error("[PinyinOverrides] 写入 {0} 失败：{1}", path, ex.Message);
                TryDeleteTemp(tmpPath, path);
                return string.Format(UiText("PinyinOverrides_ErrorSaveFailed"), ex.Message);
            }
        }

        // 序列化：2 空格缩进 + LF 行尾 + 末尾换行，与宿主现有文件逐字节一致。
        private static string Serialize(Dictionary<string, string> rules)
        {
            return JsonSerializer.Serialize(rules, WriteOptions).Replace("\r\n", "\n") + "\n";
        }

        // 备份为 <file>.bak.yyyymmddHHMMSS；文件不存在时跳过。
        private void Backup(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            var backupPath = path + ".bak." + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Copy(path, backupPath, true);
            _logger?.Info("[PinyinOverrides] 已备份 {0} → {1}", path, backupPath);
        }

        /// <summary>只保留最新的 MaxBackups 个 <file>.bak.* 备份（按文件名即时间戳降序）；删除失败只告警不抛。</summary>
        internal void TrimBackups(string path)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(directory))
                {
                    directory = ".";
                }

                var backups = Directory.GetFiles(directory, Path.GetFileName(path) + ".bak.*");
                if (backups.Length <= MaxBackups)
                {
                    return;
                }

                Array.Sort(backups, StringComparer.Ordinal);
                Array.Reverse(backups);

                var removed = 0;
                for (var i = MaxBackups; i < backups.Length; i++)
                {
                    try
                    {
                        File.Delete(backups[i]);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn("[PinyinOverrides] 删除历史备份 {0} 失败：{1}", backups[i], ex.Message);
                    }
                }

                if (removed > 0)
                {
                    _logger?.Info("[PinyinOverrides] 已清理 {0} 个历史备份，保留 {1} 个", removed, MaxBackups);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[PinyinOverrides] 清理 {0} 的历史备份失败：{1}", path, ex.Message);
            }
        }

        // 写入失败时清理残留的临时文件，避免污染插件目录。
        private void TryDeleteTemp(string tmpPath, string path)
        {
            try
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[PinyinOverrides] 清理 {0} 的临时文件失败：{1}", path, ex.Message);
            }
        }
    }
}
