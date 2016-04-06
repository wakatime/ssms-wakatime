using System;
using System.IO;
using System.Text;

namespace WakaTime
{
    internal enum LogLevel
    {
        Debug = 1,
        Info,
        Warning,
        HandledException
    };

    static class Logger
    {
        static StringBuilder sb = new StringBuilder();
        internal static void Debug(string message)
        {
            if (!WakaTime.Debug)
                return;

            Log(LogLevel.Debug, message);
        }

        internal static void Error(string message, Exception ex = null)
        {
            var exceptionMessage = string.Format("{0}: {1}", message, ex);

            Log(LogLevel.HandledException, exceptionMessage);
        }

        internal static void Warning(string message)
        {
            Log(LogLevel.Warning, message);
        }

        internal static void Info(string message)
        {
            Log(LogLevel.Info, message);
        }

        private static void Log(LogLevel level, string message)
        {
            sb.Append(level + ":: " + message);
            // flush every 20 seconds as you do it
            File.AppendAllText("./logFiles/log" + DateTime.Today.ToString() +".txt", sb.ToString());
            sb.Clear();
        }
    }
}