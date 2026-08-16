namespace ViewMate.Common
{
    /// <summary>
    /// Single source of truth for the default intro/credits duration limits
    /// (in seconds). Previously duplicated across PluginConfiguration,
    /// IntroSkipOptions, PlaySessionMonitor and PlaySessionData.
    /// </summary>
    public static class IntroSkipDefaults
    {
        public const int MaxIntroDurationSeconds = 150;
        public const int MaxCreditsDurationSeconds = 180;
    }
}
