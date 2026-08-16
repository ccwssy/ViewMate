using System.Text.RegularExpressions;

namespace ViewMate.Common
{
    /// <summary>
    /// Shared text helpers for pinyin processing — extracted from duplicated
    /// implementations in PinyinSearchService and PinyinSortNameService.
    /// </summary>
    public static class TextUtil
    {
        public static readonly Regex ChineseRegex = new Regex(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

        public static string Escape(string s) => s?.Replace("'", "''") ?? "";
    }
}
