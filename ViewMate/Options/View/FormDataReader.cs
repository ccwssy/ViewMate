using System.Text.Json;

namespace ViewMate.Options.View
{
    /// <summary>
    /// 客户端提交的表单 JSON 读取辅助：字段缺失或类型不符时退回原值。
    /// 框架在 RunCommand 前会用提交的 Data 重建 ContentData，这里再做一次显式回写，
    /// 保证"用户已提交的值"一定进入选项对象（否则 postback 后选择丢失）。
    /// </summary>
    internal static class FormDataReader
    {
        internal static string ReadString(JsonElement root, string name, string fallback)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                return fallback;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            return fallback;
        }

        internal static bool ReadBool(JsonElement root, string name, bool fallback)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                return fallback;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.String:
                    var text = value.GetString();
                    if (string.Equals(text, "true", System.StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(text, "false", System.StringComparison.OrdinalIgnoreCase)) return false;
                    return fallback;
                default:
                    return fallback;
            }
        }
    }
}
