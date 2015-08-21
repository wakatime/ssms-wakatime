using System.Windows.Forms;

namespace WakaTime
{
    internal static class WakaTimeConstants
    {
        internal const string CurrentWakaTimeCliVersion = "4.1.0"; // https://github.com/wakatime/wakatime/blob/master/HISTORY.rst
        internal const string CliUrl = "https://github.com/wakatime/wakatime/archive/master.zip";
        internal const string PluginName = "ssms-wakatime";
        internal const string EditorName = "ssms";
        internal const string CliFolder = @"wakatime-master\wakatime\cli.py";
        internal static string UserConfigDir = Application.UserAppDataPath;
        //internal static Func<string> UserConfigDir = () =>
        //{
        //    var path = Directory.GetParent(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)).FullName;
        //    if (Environment.OSVersion.Version.Major >= 6)
        //    {
        //        path = Directory.GetParent(path).ToString();
        //    }

        //    return path;
        //};
    }
}