using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using EnvDTE80;
using KOTEM.BariVSPackage.BariExtension.Option;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell;
using KOTEM.BariVSPackage.BariExtension;
using System.Windows.Forms;
using System.IO;
using EnvDTE;
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
    [ProvideAutoLoad(UIContextGuids80.SolutionExists)]
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)]
    [ProvideProfileAttribute(typeof(AddonOptionsDialog), "Bari", "Addon", 201, 202, true)]
    [ProvideOptionPageAttribute(typeof(AddonOptionsDialog), "Bari", "Addon", 201, 202, true)]
    [ProvideProfileAttribute(typeof(AddonOptionsDialog), "Bari", "General", 201, 203, true)]
    [ProvideOptionPageAttribute(typeof(AddonOptionsDialog), "Bari", "General", 201, 203, true)]
    [Guid(GuidList.guidBariVSPackagePkgString)]
    public sealed class BariVsPackagePackage : Package, IDisposable, IVsServiceProvider, IVsSolutionLoadEvents, IVsSolutionEvents
    {
        private KeyboardHook keyboardHook;
        private SolutionWatcher solutionWatcher;
        private Commands commands;
        private uint registerCookie;
        private CommandTarget target;
        private ReloadDialogKiller dialogKiller;
        private Timer reloadTimer;
        private readonly HashSet<string> itemsToReload = new HashSet<string>();
        private readonly HashSet<string> itemsToAddDelete = new HashSet<string>();
        private readonly Dictionary<string, bool> documents = new Dictionary<string, bool>();
        private SolutionInfo solutionInfo;
        private bool solutionLoaded;

        private uint solutionEventsCoockie;

        private bool reloadNeededAfterDebug;
        private bool reBuildNeeded;
        private bool reloading;
        private bool commandRunning;
        private object[] savedStartUp;
        private string activeDocument;
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

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        protected override void Initialize()
        {
            Debug.WriteLine("Entering Initialize() of: {0}", this);

            var vsSolution = GetService(typeof(SVsSolution)) as IVsSolution;
            if (vsSolution != null)
            {
                vsSolution.AdviseSolutionEvents(this, out solutionEventsCoockie);
            }

            

            base.Initialize();
        }

        private void target_CommandSent(object sender, CommandTarget.CommandTargetEventArgs e)
        {
            if (e.Command == "Delete")
            {
            }
        }

        private void commands_CommandStarted(object sender, BariShell.BariCommandArgs e)
        {
            commandRunning = true;
        }

        private void commands_CommandFinished(object sender, BariShell.BariCommandArgs e)
        {
            commandRunning = false;
            ProcessReload();
        }

        private void DebuggerEvents_OnEnterDesignMode(dbgEventReason Reason)
        {
            if (reloadNeededAfterDebug)
            {
                ProcessReload();
            }
        }

        private void SolutionEvents_BeforeClosing()
        {
            var solutionDir = SolutionInfo.TargetWorkingDirectory;
            if (SolutionInfo.IsBariSolution && solutionDir != null)
            {
                UnRegisterPriorityCommandTarget();
                UnRegisterKeyboardHook();
                UnRegisterFileSystemWatcher();
                UnRegisterDialogKiller();
                UnRegisterReloadTimer();
                DetachPluginFromSolution();
            }
        }

        private void SolutionEvents_Opened()
        {
            var solutionDir = SolutionInfo.TargetWorkingDirectory;
            if (SolutionInfo.IsBariSolution && solutionDir != null)
            {
                try
                {
                    RegisterPriorityCommandTarget();
                    RegisterKeyboardHook();
                    RegisterFileSystemWatcher();
                    // RegisterDialogKiller();
                    RegisterReloadTimer();
                    SetStartUpProject();
                }
                catch (Exception)
                {
                }
            }
        }

        private void SetStartUpProject()
        {
            if (Properties.Settings.Default.SetStartUpProject)
            {
                var startProject =
                    SolutionInfo.BariConfig.StartupPath.TrimSuffix(".exe").Split('\\').LastOrDefault() + ".csproj";

                if (string.IsNullOrEmpty(startProject)) return;

                var startupProject = GetProject(startProject);

                if (startupProject == null) return;

                GetDte().Solution.SolutionBuild.StartupProjects = startupProject.UniqueName;

                startupProject.ConfigurationManager.ActiveConfiguration.Properties.Item("StartAction").Value =
                    (int)StartAction.Program;
                startupProject.ConfigurationManager.ActiveConfiguration.Properties.Item("StartProgram").Value =
                    Path.GetDirectoryName(SolutionInfo.Solution) + "\\" + SolutionInfo.BariConfig.Target
                    + "\\" + SolutionInfo.BariConfig.StartupPath.Split('\\').LastOrDefault();
                startupProject.ConfigurationManager.ActiveConfiguration.Properties.Item("StartArguments").Value =
                    Properties.Settings.Default.StartArguments;
            }
        }

        public Project GetProject(string name)
        {
            foreach (Project solFolder in GetDte().Solution.Projects)
            {
                if (solFolder != null)
                {
                    if (solFolder.UniqueName.Contains(name))
                        return solFolder;
                }

                foreach (var projectItem in solFolder.ProjectItems)
                {
                    ProjectItem tmpItem = projectItem as ProjectItem;
                    if (tmpItem != null)
                    {
                        Project proj = tmpItem.Object as Project;
                        if (proj != null && proj.UniqueName.Contains(name))
                            return proj;
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
            reloadTimer = new Timer(450);
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
                solutionWatcher.ReloadNeeded -= SolutionWatcherOnReloadNeeded;
                solutionWatcher.Dispose();
                solutionWatcher = null;
            }
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
            solutionWatcher.ReloadNeeded += SolutionWatcherOnReloadNeeded;
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
                if (GetFiles(item).Any(p => p.Equals(file)))
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
                return new List<string> { item.Name };

            //      Debug.WriteLine("{0} - {1}", item.Name, item.ProjectItems == null ? -1 : item.ProjectItems.Count);

            var items = item.ProjectItems.GetEnumerator();
            var ret = new List<string> { item.Name };
            while (items.MoveNext())
            {
                var currentItem = (ProjectItem)items.Current;
                ret.AddRange(GetFiles(currentItem));
            }

            return ret;
        }

        private IList<Project> Projects()
        {
            var projects = GetDte().Solution.Projects;
            var list = new List<Project>();
            var item = projects.GetEnumerator();

            while (item.MoveNext())
            {
                var project = item.Current as Project;
                if (project == null)
                {
                    continue;
                }

                if (project.Kind == ProjectKinds.vsProjectKindSolutionFolder)
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
                if (subProject.Kind == ProjectKinds.vsProjectKindSolutionFolder)
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
            commands.IsBuildNeeded = true;
            foreach (var item in e.ItemsToReload)
            {
                itemsToReload.Add(item.Key);
            }
        }

        private void SolutionWatcherOnReloadNeeded(object sender, SolutionWatcher.ReloadEventArgs e)
        {
            if (reloading)
            {
                return;
            }

            reloadTimer.Stop();
            if (e.ReBuildNeeded)
            {
                commands.IsBuildNeeded = true;
                reBuildNeeded = true;
            }
            foreach (var item in e.ItemsToReload)
            {
                itemsToAddDelete.Add(item.Key);
            }
            reloadTimer.Start();
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
            else
            {
                if (!commandRunning)
                {
                    if (!itemsToAddDelete.All(i => projExtensions.Contains(Path.GetExtension(i))))
                    {
                        if (Properties.Settings.Default.PromptReload)
                        {
                            if (ShowMessageBox("Do you want to reload projects/solution?") == IDYES)
                                Reload();
                        }
                        else
                            Reload();

                    }
                    itemsToReload.Clear();
                    itemsToAddDelete.Clear();
                }
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
            if (Environment.HasShutdownStarted || AppDomain.CurrentDomain.IsFinalizingForUnload() || !itemsToReload.Any())
                return;

            if (SolutionInfo.IsBariSolution)
            {
                if (IsDebugging)
                {
                    reloadNeededAfterDebug = true;
                    return;
                }

                GetDte().Documents.SaveAll();

                var onlySolution =
                    itemsToReload.Any(item => item.ToLower().EndsWith(".sln") || item.ToLower().EndsWith(".yaml"));

                var items = itemsToReload.Where(file => extensions.Contains(Path.GetExtension(file))).Select(project => GetProject(GetProjectName(project)));
                items = items.Where(i => i != null).Distinct();

                onlySolution = onlySolution || (items.Count() > (GetDte().Solution.Projects.Count / 2));

                if (onlySolution)
                {
                    ReloadSolution();
                }
                else
                {
                    SaveDocuments(items.Select(p => p.UniqueName).ToList());
                    SaveStartupProject();

                    foreach (var item in items)
                    {
                        ReloadProject(SolutionInfo, item);
                    }
                    System.Threading.Thread.Sleep(50);

                    ReloadDocuments();
                    ReloadStartupProject();
                }

                reloadNeededAfterDebug = false;
                itemsToReload.Clear();
                itemsToAddDelete.Clear();
            }
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
            activeDocument = GetDte().ActiveDocument.FullName;

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

        private DTE dte;

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
            return VSConstants.S_OK;
        }

        int IVsSolutionLoadEvents.OnAfterLoadProjectBatch(bool fIsBackgroundIdleBatch)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionLoadEvents.OnBeforeBackgroundSolutionLoadBegins()
        {
            if (!solutionLoaded && solutionInfo == null)
            {
                solutionLoaded = true;
                AttachPluginToSolution();
            }

            return VSConstants.S_OK;
        }

        private void DetachPluginFromSolution()
        {
            GetDte().Events.SolutionEvents.Opened -= SolutionEvents_Opened;
            GetDte().Events.SolutionEvents.BeforeClosing -= SolutionEvents_BeforeClosing;
            GetDte().Events.DebuggerEvents.OnEnterDesignMode -= DebuggerEvents_OnEnterDesignMode;

            if (target != null)
            {
                target.CommandSent -= target_CommandSent;
            }

            if (commands != null)
            {
                commands.CommandFinished -= commands_CommandFinished;
                commands.CommandStarted -= commands_CommandStarted;
                commands.Dispose();
            }
        }

        private void AttachPluginToSolution(string fileName = "")
        {
            SolutionInfo = new SolutionInfo(string.IsNullOrEmpty(fileName) ? GetDte().Solution.FileName : fileName);

            Debug.WriteLine("Solution name: {0}", SolutionInfo.Solution);

            if (SolutionInfo.IsBariSolution)
            {
                commands = new Commands(this);
                commands.CommandFinished += commands_CommandFinished;
                commands.CommandStarted += commands_CommandStarted;

                target = new CommandTarget(this, commands, this);
                target.CommandSent += target_CommandSent;

                RegisterDialogKiller();

                GetDte().Events.SolutionEvents.Opened += SolutionEvents_Opened;
                GetDte().Events.SolutionEvents.BeforeClosing += SolutionEvents_BeforeClosing;
                GetDte().Events.DebuggerEvents.OnEnterDesignMode += DebuggerEvents_OnEnterDesignMode;
            }
            else
            {
                SolutionInfo = null;
            }
        }

        int IVsSolutionLoadEvents.OnBeforeLoadProjectBatch(bool fIsBackgroundIdleBatch)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionLoadEvents.OnBeforeOpenSolution(string pszSolutionFilename)
        {
            if (!solutionLoaded && solutionInfo == null)
            {
                solutionLoaded = true;
                AttachPluginToSolution(pszSolutionFilename);
            }

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
            
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            if (!solutionLoaded && solutionInfo == null)
            {
                solutionLoaded = true;
                AttachPluginToSolution();
            }

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
