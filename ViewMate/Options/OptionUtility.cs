using System;

namespace ViewMate.Options
{
    public static class Utility
    {
        public static readonly Version AppVer = Plugin.Instance.ApplicationHost.ApplicationVersion;
        public static readonly Version VerTarget = new Version("4.9.5.0");
    }
}
