using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using KOTEM.BariVSPackage.BariExtension.Option;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.VCProjectEngine;
using KOTEM.BariVSPackage.BariExtension;
using System.Windows.Forms;
using System.IO;
using EnvDTE;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;
using Commands = KOTEM.BariVSPackage.BariExtension.Commands;
using Timer = System.Timers.Timer;

namespace KOTEM.BariVSPackage
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    ///
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the 
    /// IVsPackage interface and uses the registration attributes defined in the framework to 
    /// register itself and its components with the shell.
    /// </summary>
    // This attribute tells the PkgDef creation utility (CreatePkgDef.exe) that this class is
    // a package.
    [PackageRegistration(UseManagedResourcesOnly = true)]
    // This attribute is used to register the information needed to show this package
    // in the Help/About dialog of Visual Studio.
    [ProvideAutoLoad(UIContextGuids80.NoSolution)]
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)]
    [ProvideProfileAttribute(typeof(AddonOptionsDialog), "Bari", "Addon", 201, 202, true)]
    [ProvideOptionPageAttribute(typeof(AddonOptionsDialog), "Bari", "Addon", 201, 202, true)]
    [ProvideProfileAttribute(typeof(AddonOptionsDialog), "Bari", "General", 201, 203, true)]
    [ProvideOptionPageAttribute(typeof(AddonOptionsDialog), "Bari", "General", 201, 203, true)]
    [Guid(GuidList.guidBariVSPackagePkgString)]
    public sealed class BariVsPackagePackage : Package, IDisposable, IVsServiceProvider, IVsSolutionLoadEvents, IVsSolutionEvents, IPackage
    {
        private enum ChangeTypeEnum
        {
            OnlyBuild,
            YamlChange,
            FileAddOrDelete,
        }

        private readonly ILog log = LogManager.GetLogger(typeof(BariVsPackagePackage));
        private const string vsProjectKindSolutionFolder = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

        private KeyboardHook keyboardHook;
        private SolutionWatcher solutionWatcher;
        private Commands commands;
        private uint registerCookie;
        private CommandTarget target;
        private ReloadDialogKiller dialogKiller;
        private Timer reloadTimer;
        private readonly HashSet<string> itemsChanged = new HashSet<string>();
        private readonly HashSet<string> itemsChangedDuringCommand = new HashSet<string>();
        private readonly Dictionary<string, bool> documents = new Dictionary<string, bool>();
        private SolutionInfo solutionInfo;
        private bool solutionLoaded;
        private DTE dte;
        private IVsStatusbar bar;
        private bool startupSet;
        private uint solutionEventsCoockie;

        private ChangeTypeEnum changeType;
        private bool reloadNeededAfterDebug;
        private bool reBuildNeeded;
        private bool reloading;
        private bool commandRunning;
        private object[] savedStartUp;
        private string activeDocument;
        private bool checking;
        private int checkNumber;
        private readonly HashSet<string> extensions = new HashSet<string>(new[] { ".cs", ".fs", ".xaml", ".cpp", ".xml", ".h", ".c", ".png", ".svg", ".txt", ".py", ".ini", ".chm", ".jpg", ".cg", ".hlsl", ".glsl" });
        private readonly HashSet<string> projExtensions = new HashSet<string>(new[] { ".yaml", ".csproj", ".vcxproj", ".fsproj", ".vcproj" });

        internal const int IDOK = 1;
        internal const int IDCANCEL = 2;
        internal const int IDABORT = 3;
        internal const int IDRETRY = 4;
        internal const int IDIGNORE = 5;
        internal const int IDYES = 6;
        internal const int IDNO = 7;
        internal const int IDCLOSE = 8;

        public SolutionInfo SolutionInfo
        {
            get
            {
                if (solutionInfo == null)
                {
                    solutionInfo = new SolutionInfo(GetDte().Solution.FileName);
                }
                return solutionInfo;
            }
            set { solutionInfo = value; }
        }

        public bool IsDebugging
        {
            get
            {
                return GetDte().Debugger.DebuggedProcesses.Count > 0;
            }
        }
        public bool IsWatcherBusy
        {
            get { return solutionWatcher.IsBusy; }
        }

        public IVsStatusbar StatusBar
        {
            get
            {
                if (bar == null)
                {
                    bar = GetService(typeof(SVsStatusbar)) as IVsStatusbar;
                }
                return bar;
            }
        }

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        protected override void Initialize()
        {
            InitLogging();

            Debug.WriteLine("Entering Initialize() of: {0}", this);

            var vsSolution = GetService(typeof(SVsSolution)) as IVsSolution;
            if (vsSolution != null)
            {
                vsSolution.AdviseSolutionEvents(this, out solutionEventsCoockie);
            }

            RegisterKeyboardHook();
            RegisterDialogKiller();

            base.Initialize();
        }

        private void InitLogging()
        {
            var hierarchy = (Hierarchy)LogManager.GetRepository();

            var patternLayout = new PatternLayout();
            patternLayout.ConversionPattern = "%date [%thread] %-5level %logger - %message%newline";
            patternLayout.ActivateOptions();

            var roller = new RollingFileAppender();
            roller.AppendToFile = true;
            roller.File = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bari", "Logs", "bari-log.txt");
            roller.Layout = patternLayout;
            roller.MaxSizeRollBackups = 5;
            roller.MaximumFileSize = "10MB";
            roller.RollingStyle = RollingFileAppender.RollingMode.Size;
            roller.StaticLogFileName = true;
            roller.ActivateOptions();
            hierarchy.Root.AddAppender(roller);

            hierarchy.Root.Level = Properties.Settings.Default.Logging ? Level.Debug : Level.Off;
            hierarchy.RaiseConfigurationChanged(EventArgs.Empty);
            hierarchy.Configured = true;

            log.Info("Logging initialized");
        }

        private void target_CommandSent(object sender, CommandTarget.CommandTargetEventArgs e)
        {
        }

        private void commands_CommandStarted(object sender, BariShell.BariCommandArgs e)
        {
            commandRunning = true;
        }

        private void commands_CommandFinished(object sender, BariShell.BariCommandArgs e)
        {
            if (changeType != ChangeTypeEnum.OnlyBuild)
            {
                if (changeType == ChangeTypeEnum.YamlChange)
                {
                    foreach (var collection in itemsChangedDuringCommand)
                    {
                        itemsChanged.Add(collection);
                    }
                }

                if (Properties.Settings.Default.PromptReload)
                {
                    if (ShowMessageBox("Do you want to reload projects/solution?") == IDYES)
                    {
                        Reload();
                    }
                }
                else
                {
                    Reload();
                }
            }

            changeType = ChangeTypeEnum.OnlyBuild;
            itemsChangedDuringCommand.Clear();
            itemsChanged.Clear();
            commandRunning = false;
        }

        private void DebuggerEvents_OnEnterDesignMode(dbgEventReason Reason)
        {
            if (reloadNeededAfterDebug)
            {
                ProcessReload();
            }
        }

        private void SolutionEvents_Opened()
        {
            AttachPluginToSolution();

            var solutionDir = SolutionInfo.TargetWorkingDirectory;
            if (SolutionInfo.IsBariSolution && solutionDir != null)
            {
                try
                {
                    SetStartUpProject();
                }
                catch (Exception ex)
                {
                }
            }
        }

        private void SolutionEvents_BeforeClosing()
        {
            var solutionDir = SolutionInfo.TargetWorkingDirectory;
            if (SolutionInfo.IsBariSolution && solutionDir != null)
            {
                UnRegisterPriorityCommandTarget();
                UnRegisterFileSystemWatcher();
                UnRegisterReloadTimer();
                DetachPluginFromSolution();
                SetDialogKiller(false);
                SetKeyboardHook(false);
            }
        }

        private void SetKeyboardHook(bool b)
        {
            if (keyboardHook != null)
            {
                keyboardHook.IsEnabled = b;
            }
        }

        private void SetDialogKiller(bool b)
        {
            if (dialogKiller != null)
            {
                dialogKiller.IsEnabled = b;
            }
        }

        private void SetStartUpProject()
        {
            if (Properties.Settings.Default.SetStartUpProject && !startupSet)
            {
                var cppProject = false;
                var startProject = SolutionInfo.BariConfig.StartupPath.TrimSuffix(".exe").Split('\\').LastOrDefault() + ".csproj";

                if (string.IsNullOrEmpty(startProject)) return;

                var startupProject = GetProject(startProject);

                if (startupProject == null)
                {
                    startProject = Path.ChangeExtension(startProject, "vcxproj");
                    startupProject = GetProject(startProject);
                    cppProject = true;
                }

                if (startupProject == null) return;


                GetDte().Solution.SolutionBuild.StartupProjects = startupProject.UniqueName;

                var startProgram = Path.GetDirectoryName(SolutionInfo.Solution) + "\\" + SolutionInfo.BariConfig.Target
                                   + "\\" + SolutionInfo.BariConfig.StartupPath.Split('\\').LastOrDefault();
                if (cppProject)
                {
                    var prj = startupProject.Object as VCProject;

                    if (prj != null)
                    {
                        //VS2017
                        try
                        {
                            VCConfiguration config = prj.ActiveConfiguration;

                            //c:\Program Files (x86)\MSBuild\Microsoft.Cpp\v4.0\V140\1033\debugger_local_windows.xml
                            IVCRulePropertyStorage rule = config.Rules.Item("WindowsLocalDebugger") as IVCRulePropertyStorage;
                            rule.SetPropertyValue("LocalDebuggerCommand", startProgram);
                            rule.SetPropertyValue("LocalDebuggerCommandArguments", Properties.Settings.Default.StartArguments);
                            rule.SetPropertyValue("LocalDebuggerWorkingDirectory", Path.GetDirectoryName(startProgram));

                        }
                        catch (Exception e)
                        {
                            VCConfiguration config = prj.ActiveConfiguration;
                            var debugsettings = config.DebugSettings as VCDebugSettings;

                            debugsettings.Command = startProgram;
                            debugsettings.CommandArguments = Properties.Settings.Default.StartArguments;
                            debugsettings.WorkingDirectory = Path.GetDirectoryName(startProgram);
                        }
                    }
                    else
                    {
                        //VS2013
                        var activeConfogProps = startupProject.ConfigurationManager.ActiveConfiguration.Properties;
                        activeConfogProps.Item("Command").Value = startProgram;
                        activeConfogProps.Item("CommandArguments").Value = Properties.Settings.Default.StartArguments;
                        activeConfogProps.Item("WorkingDirectory").Value = Path.GetDirectoryName(startProgram);
                    }
                }
                else
                {
                    var activeConfogProps = startupProject.ConfigurationManager.ActiveConfiguration.Properties;
                    activeConfogProps.Item("StartAction").Value = (int)StartAction.Program;
                    activeConfogProps.Item("StartProgram").Value = startProgram;
                    activeConfogProps.Item("StartArguments").Value = Properties.Settings.Default.StartArguments;
                    activeConfogProps.Item("StartWorkingDirectory").Value = Path.GetDirectoryName(startProgram);
                }

                startupSet = true;
            }
        }

        public Project GetProject(string name)
        {
            var lowerName = name.ToLowerInvariant();
            foreach (Project solFolder in GetDte().Solution.Projects)
            {
                if (solFolder != null)
                {
                    if (solFolder.UniqueName.ToLowerInvariant().Contains(lowerName))
                        return solFolder;
                }

                foreach (var projectItem in solFolder.ProjectItems)
                {
                    ProjectItem tmpItem = projectItem as ProjectItem;
                    if (tmpItem != null)
                    {
                        Project proj = tmpItem.Object as Project;
                        if (proj != null && proj.UniqueName.ToLowerInvariant().Contains(lowerName))
                        {
                            return proj;
                        }
                    }
                }
            }
            return null;
        }

        private void HandleKeyPressed(Keys keyCode)
        {
            if (keyCode == Keys.Cancel)
            {
                if (SolutionInfo.IsBariSolution)
                    commands.CancelAnyPreviousBariAction();
            }
        }

        private void RegisterReloadTimer()
        {
            reloadTimer = new Timer(150);
            reloadTimer.AutoReset = false;
            reloadTimer.Elapsed += ReloadTimerElapsed;
        }

        private void UnRegisterReloadTimer()
        {
            if (reloadTimer != null)
            {
                reloadTimer.Stop();
                reloadTimer.Elapsed -= ReloadTimerElapsed;
                reloadTimer.Dispose();
                reloadTimer = null;
            }
        }

        private void RegisterDialogKiller()
        {
            dialogKiller = new ReloadDialogKiller();
        }

        private void UnRegisterDialogKiller()
        {
            if (dialogKiller != null)
            {
                dialogKiller.Dispose();
                dialogKiller = null;
            }
        }

        private void RegisterKeyboardHook()
        {
            keyboardHook = new KeyboardHook(HandleKeyPressed);
        }

        private void UnRegisterKeyboardHook()
        {
            if (keyboardHook != null)
            {
                keyboardHook.Dispose();
                keyboardHook = null;
            }
        }

        private void UnRegisterFileSystemWatcher()
        {
            if (solutionWatcher != null)
            {
                solutionWatcher.Changed -= SolutionWatcherOnChanged;
                solutionWatcher.Checking -= SolutionWatcherOnChecking;
                solutionWatcher.Checked -= SolutionWatcherOnChecked;
                solutionWatcher.Dispose();
                solutionWatcher = null;
            }

            itemsChanged.Clear();
        }

        private void RegisterFileSystemWatcher()
        {
            var bariDir = SolutionInfo.BariWorkingDirectory;
            if (bariDir == null) return;

            var srcDir = Path.Combine(bariDir, "src");

            if (!Directory.Exists(srcDir)) return;

            var projects = new List<string>();
            projects.AddRange(Projects().Select(p => p.FullName));

            solutionWatcher = new SolutionWatcher(srcDir, extensions, projExtensions, projects, IsFileInProject);
            solutionWatcher.Changed += SolutionWatcherOnChanged;
            solutionWatcher.Checking += SolutionWatcherOnChecking;
            solutionWatcher.Checked += SolutionWatcherOnChecked;
        }

        private void SolutionWatcherOnChecked(object sender, EventArgs e)
        {
            object icon = (short)Microsoft.VisualStudio.Shell.Interop.Constants.SBAI_Synch;
            StatusBar.Animation(0, ref icon);
            StatusBar.SetText(checkNumber > 1 ? "File changes checked." : "Checksums created.");
            checking = false;
        }

        private void SolutionWatcherOnChecking(object sender, EventArgs e)
        {
            if (!checking)
            {
                checking = true;
                object icon = (short)Microsoft.VisualStudio.Shell.Interop.Constants.SBAI_Synch;

                StatusBar.Animation(1, ref icon);
                StatusBar.SetText(checkNumber > 0 ? "Checking file changes..." : "Creating checksums...");
                checkNumber++;
            }
        }

        private bool IsFileInProject(string fileName)
        {
            var project = GetProject(GetProjectName(fileName));

            var res = false;

            var file = Path.GetFileName(fileName);

            var items = project.ProjectItems.GetEnumerator();
            while (items.MoveNext())
            {
                var item = (ProjectItem)items.Current;
                if (GetFiles(item).Any(p => p.Equals(file, StringComparison.InvariantCultureIgnoreCase)))
                {
                    res = true;
                }
            }

            Debug.WriteLine("IsFileInProject: {0} - {1} - {2}", project.Name, fileName, res);

            return res;
        }

        IEnumerable<string> GetFiles(ProjectItem item)
        {
            //base case
            if (item.ProjectItems == null)
                return new List<string> { item.Name.ToLowerInvariant() };

            //      Debug.WriteLine("{0} - {1}", item.Name, item.ProjectItems == null ? -1 : item.ProjectItems.Count);

            var items = item.ProjectItems.GetEnumerator();
            var ret = new List<string> { item.Name.ToLowerInvariant() };
            while (items.MoveNext())
            {
                var currentItem = (ProjectItem)items.Current;
                ret.AddRange(GetFiles(currentItem));
            }

            return ret;
        }

        private IList<Project> Projects()
        {
            var dte = GetDte();
            var projects = dte.Solution.Projects;
            var list = new List<Project>();
            var item = projects.GetEnumerator();

            while (item.MoveNext())
            {
                if (!(item.Current is Project project))
                {
                    continue;
                }

                if (project.Kind == vsProjectKindSolutionFolder)
                {
                    list.AddRange(GetSolutionFolderProjects(project));
                }
                else
                {
                    list.Add(project);
                }
            }

            return list;
        }

        private IEnumerable<Project> GetSolutionFolderProjects(Project solutionFolder)
        {
            List<Project> list = new List<Project>();
            for (var i = 1; i <= solutionFolder.ProjectItems.Count; i++)
            {
                var subProject = solutionFolder.ProjectItems.Item(i).SubProject;
                if (subProject == null)
                {
                    continue;
                }

                // If this is another solution folder, do a recursive call, otherwise add
                if (subProject.Kind == vsProjectKindSolutionFolder)
                {
                    list.AddRange(GetSolutionFolderProjects(subProject));
                }
                else
                {
                    list.Add(subProject);
                }
            }
            return list;
        }

        private void SolutionWatcherOnChanged(object sender, SolutionWatcher.ReloadEventArgs e)
        {
            if (commandRunning)
            {
                foreach (var item in e.ItemsToReload)
                {
                    itemsChangedDuringCommand.Add(item.Key);
                }
            }
            else
            {
                commands.IsBuildNeeded = true;
                foreach (var item in e.ItemsToReload.Where(f => extensions.Any(f.Key.EndsWith) || f.Key.EndsWith(".yaml")))
                {
                    itemsChanged.Add(item.Key);
                }

                changeType = ChangeTypeEnum.OnlyBuild;

                if (e.ReBuildNeeded)
                {
                    if (reloading)
                    {
                        return;
                    }

                    if (itemsChanged.Any(i => i.EndsWith(".yaml")))
                    {
                        changeType = ChangeTypeEnum.YamlChange;
                    }
                    else
                    {
                        changeType = ChangeTypeEnum.FileAddOrDelete;
                    }

                    var timerNeeded = !commandRunning;

                    if (timerNeeded)
                    {
                        reloadTimer.Stop();

                        commands.IsBuildNeeded = true;
                        reBuildNeeded = true;
                    }

                    if (timerNeeded)
                    {
                        reloadTimer.Start();
                    }
                }
            }
        }

        private void ReloadTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            ProcessReload();
        }

        private int ShowMessageBox(string message)
        {
            var uiShell = GetService(typeof(IVsUIShell)) as IVsUIShell;
            Guid clsid = Guid.Empty;
            int result = VSConstants.S_FALSE;

            if (uiShell != null)
            {
                uiShell.ShowMessageBox(0,
                    ref clsid,
                    "File modification detected",
                    message,
                    string.Empty,
                    0,
                    OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST,
                    OLEMSGICON.OLEMSGICON_WARNING,
                    0,        // false
                    out result);
            }
            return result;
        }

        private void ProcessReload()
        {
            reloadTimer.Stop();
            reloading = true;

            if (reBuildNeeded)
            {
                if (ShowMessageBox("Do you want to build your solution?") == IDYES)
                {
                    BuildSolution();
                }
                reBuildNeeded = false;
            }
            reloading = false;
        }

        private void BuildSolution()
        {
            GetDte().Documents.SaveAll();
            commands.BuildIfNeeded((g) =>
            {
                return VSConstants.S_OK;
            }, Guid.Empty);
        }

        private void Reload()
        {
            if (Environment.HasShutdownStarted || AppDomain.CurrentDomain.IsFinalizingForUnload() || !itemsChanged.Any())
                return;

            if (IsDebugging)
            {
                reloadNeededAfterDebug = true;
                return;
            }

            GetDte().Documents.SaveAll();

            var onlySolution = itemsChanged.Any(item => item.ToLower().EndsWith(".sln"));

            var items = itemsChanged.Where(file => !file.EndsWith(".yaml") && (extensions.Contains(Path.GetExtension(file)) || projExtensions.Contains(Path.GetExtension(file)))).Select(GetProjectName);
            items = items.Where(i => i != null).Distinct().ToList();

            var projectItems = items.Select(GetProject).ToList();

            onlySolution = onlySolution || (projectItems.Count() > (GetDte().Solution.Projects.Count / 2));

            if (onlySolution)
            {
                ReloadSolution();
            }
            else
            {
                if (projectItems.Any())
                {
                    SaveDocuments(projectItems.Select(p => p.UniqueName).ToList());
                    SaveStartupProject();

                    foreach (var item in projectItems)
                    {
                        try
                        {
                            Debug.WriteLine(item.UniqueName);
                            ReloadProject(SolutionInfo, item);
                        }
                        catch (Exception e)
                        {
                        }
                    }

                    System.Threading.Thread.Sleep(50);

                    ReloadDocuments();
                    ReloadStartupProject();
                }
            }

            reloadNeededAfterDebug = false;
        }

        private void ReloadStartupProject()
        {
            if (savedStartUp != null)
            {
                if (savedStartUp.Length == 1)
                    GetDte().Solution.SolutionBuild.StartupProjects = savedStartUp[0];
                else
                    GetDte().Solution.SolutionBuild.StartupProjects = savedStartUp;
            }
        }

        private void SaveStartupProject()
        {
            savedStartUp = GetDte().Solution.SolutionBuild.StartupProjects as object[];
        }

        private void SaveDocuments(IEnumerable<string> reopenedProjects)
        {
            if (!Properties.Settings.Default.KeepFilesOpen)
                return;

            var projects = reopenedProjects.Select(p => GetProjectName(p)).ToList();
            activeDocument = GetDte().ActiveDocument == null ? string.Empty : GetDte().ActiveDocument.FullName;

            documents.Clear();
            foreach (var document in GetDte().Documents.OfType<Document>().Where(d => projects.Contains(GetProjectName(d.FullName))))
            {
                object pinned = null;
                var frame = GetWindowFrameFromDocument(document.FullName);
                if (frame != null)
                    frame.GetProperty((int)__VSFPROPID5.VSFPROPID_IsPinned, out pinned);

                documents.Add(document.FullName, pinned != null && (bool)pinned);
            }

            System.Threading.Thread.Sleep(50);
        }

        private void ReloadDocuments()
        {
            if (!Properties.Settings.Default.KeepFilesOpen)
                return;

            var dte = GetDte();
            Window activeWindow = null;
            foreach (var document in documents.Reverse())
            {
                if (File.Exists(document.Key))
                {
                    var win = dte.ItemOperations.OpenFile(document.Key);
                    if (document.Key.Equals(activeDocument))
                        activeWindow = win;

                    var frame = GetWindowFrameFromDocument(document.Key);
                    if (frame != null)
                        frame.SetProperty((int)__VSFPROPID5.VSFPROPID_IsPinned, document.Value);

                    System.Threading.Thread.Sleep(10);
                }
            }

            if (activeWindow != null)
                activeWindow.Activate();
        }

        private void ReloadProject(SolutionInfo solutionInfo, Project projectRef)
        {
            var solution = base.GetService(typeof(SVsSolution)) as IVsSolution4;
            var solution2 = solution as IVsSolution2;

            IVsHierarchy selectedHierarchy;
            solution2.GetProjectOfUniqueName(projectRef.UniqueName, out selectedHierarchy);

            if (selectedHierarchy != null)
            {
                Guid guid;
                solution2.GetGuidOfProject(selectedHierarchy, out guid);
                solution.UnloadProject(ref guid, (uint)_VSProjectUnloadStatus.UNLOADSTATUS_UnloadedByUser);
                System.Threading.Thread.Sleep(50);
                solution.ReloadProject(ref guid);
            }
        }

        private string GetProjectName(string projectFile)
        {
            if (projectFile.Contains("{"))//project
            {
                return projectFile.Split('{')[0];
            }

            var bariDir = SolutionInfo.BariWorkingDirectory;
            var srcDir = Path.Combine(bariDir, "src");

            projectFile = projectFile.Remove(0, srcDir.Length + 1);
            projectFile = projectFile.Remove(0, projectFile.IndexOf("\\") + 1);

            if (projectFile.StartsWith("tests"))
                projectFile = projectFile.Remove(0, projectFile.IndexOf("\\") + 1);

            projectFile = projectFile.Substring(0, projectFile.IndexOf("\\"));
            return projectFile;
        }

        private void ReloadSolution()
        {
            var slnName = GetDte().Solution.FullName;
            var solution = GetService(typeof(SVsSolution)) as IVsSolution2;
            solution.CloseSolutionElement((uint)__VSSLNCLOSEOPTIONS.SLNCLOSEOPT_UnloadProject, null, 0);
            solution.OpenSolutionFile((int)__VSSLNOPENOPTIONS.SLNOPENOPT_AddToCurrent, slnName);
        }

        private void UnRegisterPriorityCommandTarget()
        {
            var vsRegisterPriorityCommandTarget =
                (IVsRegisterPriorityCommandTarget)GetService(typeof(SVsRegisterPriorityCommandTarget));
            if (vsRegisterPriorityCommandTarget == null) return;
            vsRegisterPriorityCommandTarget.UnregisterPriorityCommandTarget(registerCookie);
            registerCookie = 0;
        }

        private void RegisterPriorityCommandTarget()
        {
            UnRegisterPriorityCommandTarget();
            var vsRegisterPriorityCommandTarget =
                (IVsRegisterPriorityCommandTarget)GetService(typeof(SVsRegisterPriorityCommandTarget));
            if (vsRegisterPriorityCommandTarget == null) return;
            vsRegisterPriorityCommandTarget.RegisterPriorityCommandTarget(0, target, out registerCookie);
        }

        private IVsWindowFrame GetWindowFrameFromDocument(string path)
        {
            var shell = GetService<IVsUIShell>();
            IEnumWindowFrames frames;
            shell.GetDocumentWindowEnum(out frames);

            if (frames == null)
                return null;

            foreach (IVsWindowFrame enumWindowFrame in ComUtilities.EnumerableFrom(frames))
            {
                object doc;
                enumWindowFrame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out doc);
                if (doc is string && doc.ToString().ToLower().Equals(path.ToLower()))
                    return enumWindowFrame;
            }
            return null;
        }


        public DTE GetDte()
        {
            if (dte == null)
                dte = GetService<DTE>();
            return dte;
        }

        public T GetService<T>()
        {
            return (T)GetService(typeof(T));
        }

        #region IVsSolutionLoadEvents

        int IVsSolutionLoadEvents.OnAfterBackgroundSolutionLoadComplete()
        {
            AttachPluginToSolution();
            UpdateSolutionWatcher();

            if (SolutionInfo != null && SolutionInfo.IsBariSolution)
            {
                try
                {
                    SetStartUpProject();
                }
                catch (Exception ex)
                {
                }
            }

            return VSConstants.S_OK;
        }

        int IVsSolutionLoadEvents.OnAfterLoadProjectBatch(bool fIsBackgroundIdleBatch)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionLoadEvents.OnBeforeBackgroundSolutionLoadBegins()
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionLoadEvents.OnBeforeLoadProjectBatch(bool fIsBackgroundIdleBatch)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionLoadEvents.OnBeforeOpenSolution(string pszSolutionFilename)
        {
            AttachPluginToSolution(pszSolutionFilename);
            return VSConstants.S_OK;
        }

        int IVsSolutionLoadEvents.OnQueryBackgroundLoadProjectBatch(out bool pfShouldDelayLoadToNextIdle)
        {
            pfShouldDelayLoadToNextIdle = false;
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnAfterCloseSolution(object pUnkReserved)
        {
            solutionLoaded = false;
            SolutionInfo = null;
            startupSet = false;

            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy)
        {
            UpdateSolutionWatcher();
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
        {
            UpdateSolutionWatcher();
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            AttachPluginToSolution();
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnBeforeCloseSolution(object pUnkReserved)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnQueryCloseSolution(object pUnkReserved, ref int pfCancel)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel)
        {
            return VSConstants.S_OK;
        }

        #endregion

        private void UpdateSolutionWatcher()
        {
            if (solutionWatcher != null)
            {
                var projects = new List<string>();
                projects.AddRange(Projects().Select(p => p.FullName));
                solutionWatcher.AddProject(projects);
            }
        }

        private void DetachPluginFromSolution()
        {
            var dte = GetDte();
            try
            {
                dte.Events.SolutionEvents.Opened -= SolutionEvents_Opened;
                dte.Events.SolutionEvents.BeforeClosing -= SolutionEvents_BeforeClosing;
                dte.Events.DebuggerEvents.OnEnterDesignMode -= DebuggerEvents_OnEnterDesignMode;
            }
            catch (Exception)
            {
            }

            if (target != null)
            {
                target.CommandSent -= target_CommandSent;
                target = null;
            }

            if (commands != null)
            {
                commands.CommandFinished -= commands_CommandFinished;
                commands.CommandStarted -= commands_CommandStarted;
                commands.Dispose();
                commands = null;
            }
        }

        private void AttachPluginToSolution(string soultionFileName = "")
        {
            if (!solutionLoaded && solutionInfo == null)
            {
                solutionLoaded = true;

                log.Info("Attach started");
                var fileName = string.IsNullOrEmpty(soultionFileName) ? GetDte().Solution.FileName : soultionFileName;
                SolutionInfo = new SolutionInfo(fileName);

                if (SolutionInfo.IsBariSolution)
                {
                    commands = new Commands(this, fileName);
                    commands.CommandFinished += commands_CommandFinished;
                    commands.CommandStarted += commands_CommandStarted;

                    target = new CommandTarget(this, commands, this);
                    target.CommandSent += target_CommandSent;

                    RegisterPriorityCommandTarget();
                    RegisterReloadTimer();
                    RegisterFileSystemWatcher();
                    SetDialogKiller(true);
                    SetKeyboardHook(true);

                    GetDte().Events.SolutionEvents.Opened += SolutionEvents_Opened;
                    GetDte().Events.SolutionEvents.BeforeClosing += SolutionEvents_BeforeClosing;
                    GetDte().Events.DebuggerEvents.OnEnterDesignMode += DebuggerEvents_OnEnterDesignMode;
                }
                else
                {
                    SolutionInfo = null;
                }

                log.Info("Attach finished");
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {

                var vsSolution = GetService(typeof(SVsSolution)) as IVsSolution;
                if (vsSolution != null)
                {
                    vsSolution.UnadviseSolutionEvents(solutionEventsCoockie);
                }

                DetachPluginFromSolution();
                UnRegisterPriorityCommandTarget();
                base.Dispose(disposing);
                UnRegisterDialogKiller();
                UnRegisterKeyboardHook();
                UnRegisterFileSystemWatcher();
                UnRegisterReloadTimer();
            }
        }
    }
}
