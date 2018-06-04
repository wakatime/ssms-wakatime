using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using EnvDTE;
using EnvDTE80;
using Extensibility;
using Microsoft.VisualStudio.CommandBars;
using NLog;
using NLog.Config;
using WakaTime.Forms;
using Debugger = System.Diagnostics.Debugger;

namespace WakaTime
{
    /// <summary>The object for implementing an Add-in.</summary>
    /// <seealso class='IDTExtensibility2' />
    public class WakaTime : IDTExtensibility2, IDTCommandTarget
    {
        private static string _version = string.Empty;
        private static string _editorVersion = string.Empty;

        private DTE2 _applicationObject;
        private AddIn _addInInstance;
        private DocumentEvents _docEvents;
        private WindowEvents _windowsEvents;

        private static WakaTimeConfigFile _wakaTimeConfigFile;

        public static bool Debug;
        public static string ApiKey;
        static readonly PythonCliParameters PythonCliParameters = new PythonCliParameters();
        private static string _lastFile;
        DateTime _lastHeartbeat = DateTime.UtcNow.AddMinutes(-3);
        private static readonly object ThreadLock = new object();

        /// <summary>Implements the constructor for the Add-in object. Place your initialization code within this method.</summary>
        public WakaTime()
        {
            var assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);            
            LogManager.Configuration = new XmlLoggingConfiguration(Path.Combine(assemblyFolder, "Nlog.config"), true);

            if (Debugger.IsAttached)
                LogManager.ThrowExceptions = true;                     

            _version = string.Format("{0}.{1}.{2}", CoreAssembly.Version.Major, CoreAssembly.Version.Minor, CoreAssembly.Version.Build);
            _wakaTimeConfigFile = new WakaTimeConfigFile();
        }

        /// <summary>Implements the OnConnection method of the IDTExtensibility2 interface. Receives notification that the Add-in is being loaded.</summary>
        /// <param term='application'>Root object of the host application.</param>
        /// <param term='connectMode'>Describes how the Add-in is being loaded.</param>
        /// <param term='addInInst'>Object representing this Add-in.</param>
        /// <seealso class='IDTExtensibility2' />
        public void OnConnection(object application, ext_ConnectMode connectMode, object addInInst, ref Array custom)
        {
            Logger.Info(string.Format("Initializing WakaTime v{0}", _version));

            try
            {

                //Use TLS 1.2 for connections to secure resources
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;

                _applicationObject = (DTE2)application;
                _addInInstance = (AddIn)addInInst;

                _editorVersion = _applicationObject.Version;
                _docEvents = _applicationObject.Events.DocumentEvents;
                _docEvents.DocumentOpened += DocEventsOnDocumentOpened;
                _docEvents.DocumentSaved += DocEventsOnDocumentSaved;
                _windowsEvents = _applicationObject.Events.WindowEvents;
                _windowsEvents.WindowActivated += WindowsEventsOnWindowActivated;

                if (connectMode == ext_ConnectMode.ext_cm_UISetup)
                {
                    var contextGuids = new object[] { };
                    var commands = (Commands2)_applicationObject.Commands;
                    const string toolsMenuName = "Tools";

                    //Place the command on the tools menu.
                    //Find the MenuBar command bar, which is the top-level command bar holding all the main menu items:
                    var menuBarCommandBar = ((CommandBars)_applicationObject.CommandBars)["MenuBar"];

                    //Find the Tools command bar on the MenuBar command bar:
                    var toolsControl = menuBarCommandBar.Controls[toolsMenuName];
                    var toolsPopup = (CommandBarPopup)toolsControl;

                    //This try/catch block can be duplicated if you wish to add multiple commands to be handled by your Add-in,
                    //  just make sure you also update the QueryStatus/Exec method to include the new command names.
                    try
                    {
                        //Add a command to the Commands collection:
                        var command = commands.AddNamedCommand2(_addInInstance, "WakaTime", "WakaTime", "WakaTime Settings", true, 59, ref contextGuids, (int)vsCommandStatus.vsCommandStatusSupported + (int)vsCommandStatus.vsCommandStatusEnabled, (int)vsCommandStyle.vsCommandStylePictAndText, vsCommandControlType.vsCommandControlTypeButton);

                        //Add a control for the command to the tools menu:
                        if ((command != null) && (toolsPopup != null))
                        {
                            command.AddControl(toolsPopup.CommandBar, 1);
                        }
                    }
                    catch (ArgumentException)
                    {
                        //If we are here, then the exception is probably because a command with that name
                        //  already exists. If so there is no need to recreate the command and we can 
                        //  safely ignore the exception.
                    }
                }

                // Make sure python is installed
                if (!PythonManager.IsPythonInstalled())
                {
                    var dialogResult = MessageBox.Show(@"Let's download and install Python now?", @"WakaTime requires Python", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (dialogResult == DialogResult.Yes)
                    {
                        var url = PythonManager.PythonDownloadUrl;
                        Downloader.DownloadPython(url, WakaTimeConstants.UserConfigDir);
                    }
                    else
                        MessageBox.Show(
                            @"Please install Python (https://www.python.org/downloads/) and restart SQL Server Management Studio to enable the WakaTime plugin.",
                            @"WakaTime", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                if (!DoesCliExist() || !IsCliLatestVersion())
                {
                    try
                    {
                        Directory.Delete(string.Format("{0}\\wakatime-master", WakaTimeConstants.UserConfigDir), true);
                    }
                    catch { /* ignored */ }

                    Downloader.DownloadCli(WakaTimeConstants.CliUrl, WakaTimeConstants.UserConfigDir);
                }

                GetSettings();

                if (string.IsNullOrEmpty(ApiKey))
                    PromptApiKey();

                Logger.Info(string.Format("Finished initializing WakaTime v{0}", _version));
            }
            catch (Exception ex)
            {
                Logger.Error("Error initializing Wakatime", ex);
            }
        }

        private void WindowsEventsOnWindowActivated(Window gotFocus, Window lostFocus)
        {
            try
            {
                if (gotFocus.Document != null)
                    HandleActivity(gotFocus.Document.FullName, false);
            }
            catch (Exception ex)
            {
                Logger.Error("WindowsEventsOnWindowActivated", ex);
            }
        }

        private void DocEventsOnDocumentOpened(Document document)
        {
            try
            {
                HandleActivity(document.FullName, false);
            }
            catch (Exception ex)
            {
                Logger.Error("DocEventsOnDocumentOpened", ex);
            }
        }

        private void DocEventsOnDocumentSaved(Document document)
        {
            try
            {
                HandleActivity(document.FullName, true);
            }
            catch (Exception ex)
            {
                Logger.Error("DocEventsOnDocumentSaved", ex);
            }
        }

        /// <summary>Implements the OnDisconnection method of the IDTExtensibility2 interface. Receives notification that the Add-in is being unloaded.</summary>
        /// <param term='disconnectMode'>Describes how the Add-in is being unloaded.</param>
        /// <param term='custom'>Array of parameters that are host application specific.</param>
        /// <seealso class='IDTExtensibility2' />
        public void OnDisconnection(ext_DisconnectMode disconnectMode, ref Array custom)
        {
        }

        /// <summary>Implements the OnAddInsUpdate method of the IDTExtensibility2 interface. Receives notification when the collection of Add-ins has changed.</summary>
        /// <param term='custom'>Array of parameters that are host application specific.</param>
        /// <seealso class='IDTExtensibility2' />		
        public void OnAddInsUpdate(ref Array custom)
        {
        }

        /// <summary>Implements the OnStartupComplete method of the IDTExtensibility2 interface. Receives notification that the host application has completed loading.</summary>
        /// <param term='custom'>Array of parameters that are host application specific.</param>
        /// <seealso class='IDTExtensibility2' />
        public void OnStartupComplete(ref Array custom)
        {
        }

        /// <summary>Implements the OnBeginShutdown method of the IDTExtensibility2 interface. Receives notification that the host application is being unloaded.</summary>
        /// <param term='custom'>Array of parameters that are host application specific.</param>
        /// <seealso class='IDTExtensibility2' />
        public void OnBeginShutdown(ref Array custom)
        {
        }

        /// <summary>Implements the QueryStatus method of the IDTCommandTarget interface. This is called when the command's availability is updated</summary>
        /// <param term='commandName'>The name of the command to determine state for.</param>
        /// <param term='neededText'>Text that is needed for the command.</param>
        /// <param term='status'>The state of the command in the user interface.</param>
        /// <param term='commandText'>Text requested by the neededText parameter.</param>
        /// <seealso class='Exec' />
        public void QueryStatus(string commandName, vsCommandStatusTextWanted neededText, ref vsCommandStatus status, ref object commandText)
        {
            if (neededText == vsCommandStatusTextWanted.vsCommandStatusTextWantedNone)
            {
                if (commandName == "WakaTime")
                {
                    status = vsCommandStatus.vsCommandStatusSupported | vsCommandStatus.vsCommandStatusEnabled;
                }
            }
        }

        /// <summary>Implements the Exec method of the IDTCommandTarget interface. This is called when the command is invoked.</summary>
        /// <param term='commandName'>The name of the command to execute.</param>
        /// <param term='executeOption'>Describes how the command should be run.</param>
        /// <param term='varIn'>Parameters passed from the caller to the command handler.</param>
        /// <param term='varOut'>Parameters passed from the command handler to the caller.</param>
        /// <param term='handled'>Informs the caller if the command was handled or not.</param>
        /// <seealso class='Exec' />
        public void Exec(string commandName, vsCommandExecOption executeOption, ref object varIn, ref object varOut, ref bool handled)
        {
            handled = false;
            if (executeOption == vsCommandExecOption.vsCommandExecOptionDoDefault)
            {
                if (commandName == "WakaTime")
                    handled = true;
            }
        }

        private static void PromptApiKey()
        {
            var form = new ApiKeyForm();
            form.ShowDialog();
        }

        static bool DoesCliExist()
        {
            return File.Exists(PythonCliParameters.Cli);
        }

        static bool IsCliLatestVersion()
        {
            var process = new RunProcess(PythonManager.GetPython(), PythonCliParameters.Cli, "--version");
            process.Run();

            var wakatimeVersion = WakaTimeConstants.CurrentWakaTimeCliVersion();

            return process.Success && process.Error.Equals(wakatimeVersion);
        }

        private static void GetSettings()
        {
            ApiKey = _wakaTimeConfigFile.ApiKey;
            Debug = _wakaTimeConfigFile.Debug;
        }

        private void HandleActivity(string currentFile, bool isWrite)
        {
            if (currentFile == null) return;

            Task.Factory.StartNew(() =>
            {
                lock (ThreadLock)
                {
                    if (!isWrite && _lastFile != null && !EnoughTimePassed() && currentFile.Equals(_lastFile))
                        return;

                    SendHeartbeat(currentFile, isWrite);
                    _lastFile = currentFile;
                    _lastHeartbeat = DateTime.UtcNow;
                }
            });
        }

        private bool EnoughTimePassed()
        {
            return _lastHeartbeat < DateTime.UtcNow.AddMinutes(-1);
        }

        public static void SendHeartbeat(string fileName, bool isWrite)
        {
            PythonCliParameters.Key = ApiKey;
            PythonCliParameters.File = fileName;
            PythonCliParameters.Plugin = string.Format("{0}/{1} {2}/{3}", WakaTimeConstants.EditorName, _editorVersion, WakaTimeConstants.PluginName, _version);
            PythonCliParameters.IsWrite = isWrite;

            var pythonBinary = PythonManager.GetPython();
            if (pythonBinary != null)
            {
                var process = new RunProcess(pythonBinary, PythonCliParameters.ToArray());
                if (Debug)
                {
                    Logger.Debug(string.Format("[\"{0}\", \"{1}\"]", pythonBinary, string.Join("\", \"", PythonCliParameters.ToArray(true))));
                    process.Run();
                    Logger.Debug(string.Format("CLI STDOUT: {0}", process.Output));
                    Logger.Debug(string.Format("CLI STDERR: {0}", process.Error));
                }
                else
                    process.RunInBackground();

                if (!process.Success)
                    Logger.Error(string.Format("Could not send heartbeat: {0}", process.Error));
            }
            else
                Logger.Error("Could not send heartbeat because python is not installed");
        }
    }

    static class CoreAssembly
    {
        static readonly Assembly Reference = typeof(CoreAssembly).Assembly;
        public static readonly Version Version = Reference.GetName().Version;
    }
}