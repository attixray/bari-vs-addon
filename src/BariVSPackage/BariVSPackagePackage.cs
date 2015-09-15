using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms.VisualStyles;
using EnvDTE80;
using KOTEM.BariVSPackage.BariExtension.Option;
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
    public sealed class BariVsPackagePackage : Package, IVsServiceProvider
    {
        private KeyboardHook keyboardHook;
        private SolutionWatcher solutionWatcher;
        private Commands commands;
        private uint registerCookie;
        private CommandTarget target;
        private ReloadDialogKiller dialogKiller;
        private Timer reloadTimer;
        private readonly HashSet<string> itemsToReload = new HashSet<string>();
        private readonly HashSet<string> documents = new HashSet<string>();

        private bool reloadNeededAfterDebug;
        private bool reBuildNeeded;
        private bool reloading;
        private object[] savedStartUp;
        private string activeDocument;

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        protected override void Initialize()
        {
            Debug.WriteLine("Entering Initialize() of: {0}", this);
            base.Initialize();

            commands = new Commands(this);

            target = new CommandTarget(this, commands, this);

            GetDte().Events.SolutionEvents.Opened += SolutionEvents_Opened;
            GetDte().Events.SolutionEvents.BeforeClosing += SolutionEvents_BeforeClosing;
            GetDte().Events.DebuggerEvents.OnEnterDesignMode += DebuggerEvents_OnEnterDesignMode;
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
            var solutionInfo = new SolutionInfo(GetDte());

            var solutionDir = solutionInfo.TargetWorkingDirectory;
            if (solutionInfo.IsBariSolution && solutionDir != null)
            {
                UnRegisterPriorityCommandTarget();
                UnRegisterKeyboardHook();
                UnRegisterFileSystemWatcher();
                UnRegisterDialogKiller();
                UnRegisterReloadTimer();
            }
        }

        private void SolutionEvents_Opened()
        {
            var solutionInfo = new SolutionInfo(GetDte());

            var solutionDir = solutionInfo.TargetWorkingDirectory;
            if (solutionInfo.IsBariSolution && solutionDir != null)
            {
                try
                {
                    RegisterPriorityCommandTarget();
                    RegisterKeyboardHook();
                    RegisterFileSystemWatcher();
                    RegisterDialogKiller();
                    RegisterReloadTimer();
                    SetStartUpProject(solutionInfo);
                }
                catch (Exception)
                {
                }
            }
        }

        private void SetStartUpProject(SolutionInfo solutionInfo)
        {
            if (Properties.Settings.Default.SetStartUpProject)
            {
                var startProject =
                    solutionInfo.BariConfig.StartupPath.TrimSuffix(".exe").Split('\\').LastOrDefault() + ".csproj";

                if (string.IsNullOrEmpty(startProject)) return;

                var startupProject = GetProject(solutionInfo, startProject);

                if (startupProject == null) return;

                solutionInfo.Solution.SolutionBuild.StartupProjects = startupProject.UniqueName;

                startupProject.ConfigurationManager.ActiveConfiguration.Properties.Item("StartAction").Value =
                    (int)StartAction.Program;
                startupProject.ConfigurationManager.ActiveConfiguration.Properties.Item("StartProgram").Value =
                    Path.GetDirectoryName(solutionInfo.Solution.FileName) + "\\" + solutionInfo.BariConfig.Target
                    + "\\" + solutionInfo.BariConfig.StartupPath.Split('\\').LastOrDefault();
                startupProject.ConfigurationManager.ActiveConfiguration.Properties.Item("StartArguments").Value =
                    Properties.Settings.Default.StartArguments;
            }
        }

        private Project GetProject(SolutionInfo solutionInfo, string name)
        {
            foreach (Project solFolder in solutionInfo.Solution.Projects)
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
                var solutionInfo = new SolutionInfo(GetDte());
                if (solutionInfo.IsBariSolution)
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
            if (dialogKiller != null)
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
            var solutionInfo = new SolutionInfo(GetDte());

            var bariDir = solutionInfo.BariWorkingDirectory;
            if (bariDir == null) return;

            var srcDir = Path.Combine(bariDir, "src");

            if (!Directory.Exists(srcDir)) return;

            solutionWatcher = new SolutionWatcher(srcDir);
            solutionWatcher.Changed += SolutionWatcherOnChanged;
            solutionWatcher.ReloadNeeded += SolutionWatcherOnReloadNeeded;
        }

        private void SolutionWatcherOnReloadNeeded(object sender, SolutionWatcher.ReloadEventArgs e)
        {
            if (reloading)
                return;

            reloadTimer.Stop();
            if (e.ReBuildNeeded)
                reBuildNeeded = true;
            itemsToReload.Add(e.ItemToReload);
            reloadTimer.Start();
        }

        private void ReloadTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            ProcessReload();
        }

        private void ProcessReload()
        {
            reloadTimer.Stop();
            reloading = true;

            if (reBuildNeeded)
            {
                MessageBox.Show("You shuold build your application!", "File modification detected", MessageBoxButtons.OK);
                reBuildNeeded = false;
            }
            else
            {
                if (!itemsToReload.All(i => i.EndsWith(".csproj") || i.EndsWith(".fsproj") || i.EndsWith(".vcxproj")))
                {
                    if (Properties.Settings.Default.PromptReload)
                    {
                        if (
                            MessageBox.Show("Do you want to reload projects/solution?", "File modification detected",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                            Reload();
                    }
                    else
                        Reload();
                }
            }
            reloading = false;
        }

        private void Reload()
        {
            if (Environment.HasShutdownStarted || AppDomain.CurrentDomain.IsFinalizingForUnload() || !itemsToReload.Any())
                return;

            var solutionInfo = new SolutionInfo(GetDte());
            if (solutionInfo.IsBariSolution)
            {
                if (solutionInfo.IsDebugging)
                {
                    reloadNeededAfterDebug = true;
                    return;
                }

                GetDte().Documents.SaveAll();

                SaveStartupProject();

                SaveDocuments();

                if (itemsToReload.Any(item => item.ToLower().EndsWith(".sln") || item.ToLower().EndsWith(".yaml")))
                    ReloadSolution(solutionInfo);
                else
                {
                    var items = itemsToReload.Select(project => GetProject(solutionInfo, GetProjectName(solutionInfo, project)));
                    items = items.Where(i => i != null).Distinct();

                    if (items.Count() > (solutionInfo.Solution.Projects.Count / 2))
                        ReloadSolution(solutionInfo);
                    else
                    {
                        foreach (var item in items)
                        {
                            ReloadProject(solutionInfo, item);
                        }
                    }
                }

                System.Threading.Thread.Sleep(50);
                ReloadDocuments();

                ReloadStartupProject();

                reloadNeededAfterDebug = false;
                itemsToReload.Clear();
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

        private void SaveDocuments()
        {
            if (!Properties.Settings.Default.KeepFilesOpen)
                return;

            activeDocument = GetDte().ActiveDocument.FullName;

            documents.Clear();
            foreach (Document document in GetDte2().Documents)
            {
                documents.Add(document.FullName);
            }
        }

        private void ReloadDocuments()
        {
            if (!Properties.Settings.Default.KeepFilesOpen)
                return;

            var dte = GetDte2();
            Window activeWindow = null;
            foreach (var document in documents.Reverse())
            {
                if (File.Exists(document))
                {
                    var win = dte.ItemOperations.OpenFile(document);
                    if (document.Equals(activeDocument))
                        activeWindow = win;
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
                solution.ReloadProject(ref guid);
            }
        }

        private string GetProjectName(SolutionInfo solutionInfo, string projectFile)
        {
            var bariDir = solutionInfo.BariWorkingDirectory;
            var srcDir = Path.Combine(bariDir, "src");

            projectFile = projectFile.Remove(0, srcDir.Length + 1);

            projectFile = projectFile.Remove(0, projectFile.IndexOf("\\") + 1);

            if (projectFile.StartsWith("tests"))
            {
                projectFile = projectFile.Remove(0, projectFile.IndexOf("\\") + 1);
            }

            projectFile = projectFile.Substring(0, projectFile.IndexOf("\\"));
            return projectFile;
        }

        private void ReloadSolution(SolutionInfo solutionInfo)
        {
            var slnName = solutionInfo.Solution.FullName;
            var solution = GetService(typeof(SVsSolution)) as IVsSolution2;
            solution.CloseSolutionElement((uint)__VSSLNCLOSEOPTIONS.SLNCLOSEOPT_UnloadProject, null, 0);
            solution.OpenSolutionFile((int)__VSSLNOPENOPTIONS.SLNOPENOPT_AddToCurrent, slnName);
        }

        private void SolutionWatcherOnChanged(object sender, EventArgs eventArgs)
        {
            commands.IsBuildNeeded = true;
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

        public DTE GetDte()
        {
            return GetService<DTE>();
        }

        public DTE2 GetDte2()
        {
            return GetDte() as DTE2;
        }

        public T GetService<T>()
        {
            return (T)GetService(typeof(T));
        }

        protected override void Dispose(bool disposing)
        {
            GetDte().Events.SolutionEvents.Opened -= SolutionEvents_Opened;
            GetDte().Events.SolutionEvents.BeforeClosing -= SolutionEvents_BeforeClosing;
            GetDte().Events.DebuggerEvents.OnEnterDesignMode -= DebuggerEvents_OnEnterDesignMode;

            UnRegisterPriorityCommandTarget();

            base.Dispose(disposing);

            UnRegisterDialogKiller();

            UnRegisterKeyboardHook();

            UnRegisterFileSystemWatcher();
        }


    }
}
