using System.Text.RegularExpressions;

namespace ViewMate.Common
{
    /// <summary>
    /// 拼音处理共用的文本辅助方法 —— 从 PinyinSearchService 与
    /// PinyinSortNameService 里重复的实现中抽取而来。
    /// </summary>
    public static class TextUtil
    {
        public static readonly Regex ChineseRegex = new Regex(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

        public static string Escape(string s) => s?.Replace("'", "''") ?? "";
    }
}
