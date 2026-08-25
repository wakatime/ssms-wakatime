using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Timers = System.Timers;
using System.Threading;
using WakaTime.ExtensionUtils;
using WakaTime.Forms;
using WakaTime.Shared.ExtensionUtils;
using Task = System.Threading.Tasks.Task;

namespace WakaTime
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [Guid(GuidList.GuidWakaTimePkgString)]
    [PackageRegistration(UseManagedResourcesOnly = true)]
    [ProvideService(typeof(WakaTimePackage))]
    [ProvideAutoLoad(GuidList.GuidWakaTimeUIString)]
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    public sealed class WakaTimePackage : Package
    {
        private DTE _dte;
        private DTEEvents _dteEvents;
        private DocumentEvents _docEvents;
        private WindowEvents _windowEvents;
        private SolutionEvents _solutionEvents;
        private DebuggerEvents _debuggerEvents;
        private BuildEvents _buildEvents;
        private TextEditorEvents _textEditorEvents;
        private Shared.ExtensionUtils.WakaTime _wakatime;
        private ILogger _logger;
        private SettingsForm _settingsForm;
        private bool _isBuildRunning;
        private string _solutionName;

        protected override void Initialize()
        {
            base.Initialize();

            AddSkipLoading();

            _dte = (DTE)GetService(typeof(DTE));
            _dteEvents = _dte.Events.DTEEvents;
            _dteEvents.OnStartupComplete += OnOnStartupComplete;

            var metadata = new Metadata
            {
                EditorName = "ssms",
                PluginName = "ssms-wakatime",
                EditorVersion = _dte == null ? string.Empty : _dte.Version,
                PluginVersion = Constants.PluginVersion
            };

            _logger = new Logger(Dependencies.GetConfigFilePath());
            _wakatime = new Shared.ExtensionUtils.WakaTime(metadata, _logger);

            _logger.Debug("It will load WakaTime extension");

            Task.Run(() =>
            {
                InitializeAsync();
            });
        }

        private void InitializeAsync()
        {
            try
            {
                Task.Run(async () => await _wakatime.InitializeAsync()).Wait();

                // Visual Studio Events              
                _docEvents = _dte.Events.DocumentEvents;
                _windowEvents = _dte.Events.WindowEvents;
                _solutionEvents = _dte.Events.SolutionEvents;
                _debuggerEvents = _dte.Events.DebuggerEvents;
                _buildEvents = _dte.Events.BuildEvents;
                _textEditorEvents = _dte.Events.TextEditorEvents;

                // Settings Form
                _settingsForm = new SettingsForm(_wakatime.Config, _logger);

                // Add our command handlers for menu (commands must exist in the .vsct file)
                if (GetService(typeof(IMenuCommandService)) is OleMenuCommandService mcs)
                {
                    // Create the command for the menu item.
                    var menuCommandId = new CommandID(new Guid(GuidList.GuidWakaTimeCmdSetString), 0x100);
                    var menuItem = new MenuCommand(MenuItemCallback, menuCommandId);
                    mcs.AddCommand(menuItem);
                }

                // setup event handlers
                _docEvents.DocumentOpened += DocEventsOnDocumentOpened;
                _docEvents.DocumentSaved += DocEventsOnDocumentSaved;
                _windowEvents.WindowActivated += WindowEventsOnWindowActivated;
                _solutionEvents.Opened += SolutionEventsOnOpened;
                _solutionEvents.Renamed += SolutionEventsOnRenamed;
                _solutionEvents.AfterClosing += SolutionEventsOnAfterClosing;
                _debuggerEvents.OnEnterRunMode += DebuggerEventsOnEnterRunMode;
                _debuggerEvents.OnEnterDesignMode += DebuggerEventsOnEnterDesignMode;
                _debuggerEvents.OnEnterBreakMode += DebuggerEventsOnEnterBreakMode;
                _buildEvents.OnBuildProjConfigBegin += BuildEventsOnBuildProjConfigBegin;
                _buildEvents.OnBuildProjConfigDone += BuildEventsOnBuildProjConfigDone;
                _textEditorEvents.LineChanged += TextEditorEventsLineChanged;
            }
            catch (Exception ex)
            {
                _logger.Error("Error Initializing WakaTime", ex);
            }
        }

        // Call this method from the Initialize method
        // to add the SkipLoading value back to the registry
        // 2 seconds after it’s removed by SSMS
        private void AddSkipLoading()
        {
            var timer = new Timers.Timer(2000);
            timer.Elapsed += (sender, args) =>
            {
                timer.Stop();

                var myPackage = UserRegistryRoot.CreateSubKey($@"Packages\{{{GuidList.GuidWakaTimePkgString}}}");
                myPackage?.SetValue("SkipLoading", 1);
            };
            timer.Start();
        }

        private void PromptApiKey()
        {
            _logger.Debug("It will ask for user to input its api key");

            var form = new ApiKeyForm(_wakatime.Config, _logger);

            form.ShowDialog();
        }

        private void OnOnStartupComplete()
        {
            // Prompt for api key if not already set
            if (string.IsNullOrEmpty(_wakatime.Config.GetSetting("api_key")))
                PromptApiKey();
        }

        private string GetProjectName()
        {
            return !string.IsNullOrEmpty(_solutionName)
                ? Path.GetFileNameWithoutExtension(_solutionName)
                : _dte.Solution != null && !string.IsNullOrEmpty(_dte.Solution.FullName)
                    ? Path.GetFileNameWithoutExtension(_dte.Solution.FullName)
                    : string.Empty;
        }

        private string GetCurrentProjectOutputForCurrentConfiguration()
        {
            try
            {
                var activeProjects = (object[])_dte.ActiveSolutionProjects;
                if (_dte.Solution == null || activeProjects.Length < 1)
                    return null;

                var project = (Project)((object[])_dte.ActiveSolutionProjects)[0];
                var config = project.ConfigurationManager.ActiveConfiguration;
                var outputPath = config.Properties.Item("OutputPath");
                var outputFileName = project.Properties.Item("OutputFileName");
                var projectPath = project.Properties.Item("FullPath");

                return $"{projectPath.Value}{outputPath.Value}{outputFileName.Value}";
            }
            catch (Exception ex)
            {
                _logger.Debug($"Could not get current project output file: {ex.Message}");
                return null;
            }
        }

        private string GetProjectOutputForConfiguration(string projectName, string platform, string projectConfig)
        {
            try
            {
                var project = _dte.Solution.Projects.Cast<Project>()
                                .FirstOrDefault(proj => proj.UniqueName == projectName);

                var config = project.ConfigurationManager.Cast<Configuration>()
                                .FirstOrDefault(conf => conf.PlatformName == platform && conf.ConfigurationName == projectConfig);

                var outputPath = config.Properties.Item("OutputPath");
                var outputFileName = project.Properties.Item("OutputFileName");
                var projectPath = project.Properties.Item("FullPath");

                return $"{projectPath.Value}{outputPath.Value}{outputFileName.Value}";
            }
            catch (Exception ex)
            {
                _logger.Debug($"Could not get project output file for configuration: {ex.Message}");
                return null;
            }
        }

        private void MenuItemCallback(object sender, EventArgs e)
        {
            try
            {
                _settingsForm.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.Error("MenuItemCallback", ex);
            }
        }

        private void DocEventsOnDocumentOpened(Document document)
        {
            try
            {
                var category = _isBuildRunning
                        ? HeartbeatCategory.Building
                        : _dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode
                            ? HeartbeatCategory.Debugging
                            : HeartbeatCategory.Coding;

                var position = GetDocumentPosition(document);

                _wakatime.HandleActivity(document.FullName, false, GetProjectName(), category,
                    lineNumber: position.LineNumber, cursorPosition: position.CursorPosition,
                    lines: position.Lines, alternateLanguage: GetAlternateLanguage(document),
                    isUnsavedEntity: IsUnsavedEntity(document), projectFolder: GetProjectFolder());
            }
            catch (Exception ex)
            {
                _logger.Error("DocEventsOnDocumentOpened", ex);
            }
        }

        private void DocEventsOnDocumentSaved(Document document)
        {
            try
            {
                var category = _isBuildRunning
                        ? HeartbeatCategory.Building
                        : _dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode
                            ? HeartbeatCategory.Debugging
                            : HeartbeatCategory.Coding;

                var position = GetDocumentPosition(document);

                _wakatime.HandleActivity(document.FullName, true, GetProjectName(), category,
                    lineNumber: position.LineNumber, cursorPosition: position.CursorPosition,
                    lines: position.Lines, alternateLanguage: GetAlternateLanguage(document),
                    isUnsavedEntity: IsUnsavedEntity(document), projectFolder: GetProjectFolder());
            }
            catch (Exception ex)
            {
                _logger.Error("DocEventsOnDocumentSaved", ex);
            }
        }

        private void WindowEventsOnWindowActivated(Window gotFocus, Window lostFocus)
        {
            try
            {
                var document = _dte.ActiveWindow.Document;
                if (document != null)
                {
                    var category = _isBuildRunning
                        ? HeartbeatCategory.Building
                        : _dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode
                            ? HeartbeatCategory.Debugging
                            : HeartbeatCategory.Coding;

                    var position = GetDocumentPosition(document);

                    _wakatime.HandleActivity(document.FullName, false, GetProjectName(), category,
                        lineNumber: position.LineNumber, cursorPosition: position.CursorPosition,
                        lines: position.Lines, alternateLanguage: GetAlternateLanguage(document),
                        isUnsavedEntity: IsUnsavedEntity(document), projectFolder: GetProjectFolder());
                }
            }
            catch (Exception ex)
            {
                _logger.Error("WindowEventsOnWindowActivated", ex);
            }
        }

        private void SolutionEventsOnOpened()
        {
            try
            {
                _solutionName = _dte.Solution.FullName;
            }
            catch (Exception ex)
            {
                _logger.Error("SolutionEventsOnOpened", ex);
            }
        }

        private void SolutionEventsOnRenamed(string oldName)
        {
            try
            {
                _solutionName = _dte.Solution.FullName;
            }
            catch (Exception ex)
            {
                _logger.Error("SolutionEventsOnRenamed", ex);
            }
        }

        private void SolutionEventsOnAfterClosing()
        {
            _solutionName = null;
        }

        private void DebuggerEventsOnEnterRunMode(dbgEventReason reason)
        {
            try
            {
                var outputFile = GetCurrentProjectOutputForCurrentConfiguration();

                _wakatime.HandleActivity(outputFile, false, GetProjectName(), HeartbeatCategory.Debugging,
                    projectFolder: GetProjectFolder());
            }
            catch (Exception ex)
            {
                _logger.Error("DebuggerEventsOnEnterRunMode", ex);
            }
        }

        private void DebuggerEventsOnEnterDesignMode(dbgEventReason reason)
        {
            try
            {
                var outputFile = GetCurrentProjectOutputForCurrentConfiguration();

                _wakatime.HandleActivity(outputFile, false, GetProjectName(), HeartbeatCategory.Debugging,
                    projectFolder: GetProjectFolder());
            }
            catch (Exception ex)
            {
                _logger.Error("DebuggerEventsOnEnterDesignMode", ex);
            }
        }

        private void DebuggerEventsOnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction executionAction)
        {
            try
            {
                var outputFile = GetCurrentProjectOutputForCurrentConfiguration();

                _wakatime.HandleActivity(outputFile, false, GetProjectName(), HeartbeatCategory.Debugging,
                    projectFolder: GetProjectFolder());
            }
            catch (Exception ex)
            {
                _logger.Error("DebuggerEventsOnEnterBreakMode", ex);
            }
        }

        private void BuildEventsOnBuildProjConfigBegin(
            string project, string projectConfig, string platform, string solutionConfig)
        {
            try
            {
                _isBuildRunning = true;

                var outputFile = GetProjectOutputForConfiguration(project, platform, projectConfig);

                _wakatime.HandleActivity(outputFile, false, GetProjectName(), HeartbeatCategory.Building,
                    projectFolder: GetProjectFolder());
            }
            catch (Exception ex)
            {
                _logger.Error("BuildEventsOnBuildProjConfigBegin", ex);
            }
        }

        private void BuildEventsOnBuildProjConfigDone(
            string project, string projectConfig, string platform, string solutionConfig, bool success)
        {
            try
            {
                _isBuildRunning = false;

                var outputFile = GetProjectOutputForConfiguration(project, platform, projectConfig);

                _wakatime.HandleActivity(outputFile, success, GetProjectName(), HeartbeatCategory.Building,
                    projectFolder: GetProjectFolder());
            }
            catch (Exception ex)
            {
                _logger.Error("BuildEventsOnBuildProjConfigDone", ex);
            }
        }

        private void TextEditorEventsLineChanged(TextPoint startPoint, TextPoint endPoint, int hint)
        {
            try
            {
                var document = startPoint.Parent.Parent;
                if (document != null)
                {
                    var category = _isBuildRunning
                        ? HeartbeatCategory.Building
                        : _dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode
                            ? HeartbeatCategory.Debugging
                            : HeartbeatCategory.Coding;

                    var position = GetDocumentPosition(document);

                    // Prefer the changed line reported by the event over the selection,
                    // since the caret may already have moved past it.
                    _wakatime.HandleActivity(document.FullName, false, GetProjectName(), category,
                        lineNumber: position.LineNumber ?? endPoint.Line,
                        cursorPosition: position.CursorPosition,
                        lines: position.Lines, alternateLanguage: GetAlternateLanguage(document),
                        isUnsavedEntity: IsUnsavedEntity(document), projectFolder: GetProjectFolder());
                }
            }
            catch (Exception ex)
            {
                _logger.Error("TextEditorEventsLineChanged", ex);
            }
        }

        private DocumentPosition GetDocumentPosition(Document document)
        {
            var position = new DocumentPosition();

            try
            {
                var textDocument = document == null ? null : document.Object("TextDocument") as TextDocument;
                if (textDocument == null)
                    return position;

                position.Lines = textDocument.EndPoint.Line;

                var selection = textDocument.Selection;
                if (selection != null)
                {
                    position.LineNumber = selection.ActivePoint.Line;
                    position.CursorPosition = selection.ActivePoint.LineCharOffset;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Could not get document position: {ex.Message}");
            }

            return position;
        }

        private string GetAlternateLanguage(Document document)
        {
            try
            {
                // Map Visual Studio language ids that differ from WakaTime
                // language names, passing the rest through as-is. Only sent as
                // --alternate-language, so wakatime-cli's own detection still
                // takes priority when it recognizes the file.
                var language = document.Language;

                switch (language)
                {
                    case "CSharp": return "C#";
                    case "C/C++": return "C++";
                    case "Basic": return "VB.NET";
                    case "HTMLX": return "HTML";
                    case "HTMLXProjection": return "HTML";
                    case "Plain Text": return "Text";
                    default:
                        // Covers SQL, T-SQL, T-SQL80, T-SQL90, SQL Server Tools etc.
                        if (language != null && language.IndexOf("SQL", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "SQL";

                        return language;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Could not get document language: {ex.Message}");
                return null;
            }
        }

        private bool IsUnsavedEntity(Document document)
        {
            try
            {
                return !File.Exists(document.FullName);
            }
            catch (Exception ex)
            {
                _logger.Debug($"Could not check whether document is unsaved: {ex.Message}");
                return false;
            }
        }

        private string GetProjectFolder()
        {
            try
            {
                var solution = !string.IsNullOrEmpty(_solutionName)
                    ? _solutionName
                    : _dte.Solution != null ? _dte.Solution.FullName : null;

                return string.IsNullOrEmpty(solution) ? null : Path.GetDirectoryName(solution);
            }
            catch (Exception ex)
            {
                _logger.Debug($"Could not get project folder: {ex.Message}");
                return null;
            }
        }

        private class DocumentPosition
        {
            public int? LineNumber { get; set; }
            public int? CursorPosition { get; set; }
            public int? Lines { get; set; }
        }
    }

    internal static class CoreAssembly
    {
        private static readonly Assembly Reference = typeof(CoreAssembly).Assembly;
        public static readonly Version Version = Reference.GetName().Version;
    }

    internal static class Constants
    {
        internal static readonly string PluginVersion =
            $"{CoreAssembly.Version.Major}" +
            $".{CoreAssembly.Version.Minor}" +
            $".{CoreAssembly.Version.Build}";
    }
}
