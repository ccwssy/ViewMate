namespace ViewMate.Common
{
    /// <summary>
    /// 片头/片尾时长上限默认值（单位：秒）的唯一权威定义。
    /// 此前在 PluginConfiguration、IntroSkipOptions、
    /// PlaySessionMonitor 与 PlaySessionData 中各自重复。
    /// </summary>
    public static class IntroSkipDefaults
    {
        public const int MaxIntroDurationSeconds = 150;
        public const int MaxCreditsDurationSeconds = 180;
    }
}
