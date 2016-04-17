using System;
using NLog;

namespace WakaTime
{    
    static class Logger
    {
        private static readonly NLog.Logger Log = LogManager.GetCurrentClassLogger();

        internal static void Debug(string message)
        {
            if (!WakaTime.Debug)
                return;

            Log.Debug(message);            
        }

        internal static void Error(string message, Exception ex = null)
        {
            var exceptionMessage = string.Format("{0}: {1}", message, ex);

            Log.Error(exceptionMessage);
        }

        internal static void Warning(string message)
        {
            Log.Warn(message);
        }

        internal static void Info(string message)
        {
            Log.Info(message);
        }        
    }
}