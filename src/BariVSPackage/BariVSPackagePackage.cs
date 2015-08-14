using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using EnvDTE90;
using KOTEM.BariVSPackage.BariExtension.Option;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell;
using KOTEM.BariVSPackage.BariExtension;
using System.Windows.Forms;
using System.IO;
using EnvDTE;
using Commands = KOTEM.BariVSPackage.BariExtension.Commands;

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
    public sealed class BariVsPackagePackage : Package, IVsServiceProvider, IVsSolutionLoadManager, IVsSolutionEvents
    {
        private KeyboardHook keyboardHook;
        private SolutionWatcher solutionWatcher;
        private Commands commands;
        private uint registerCookie;
        private CommandTarget target;
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

            RegisterPriorityCommandTarget();
            RegisterKeyboardHook();
            RegisterFileSystemWatcher();

            GetDte().Events.SolutionEvents.Opened += SolutionEvents_Opened;
        }

        private void SolutionEvents_Opened()
        {
            var dte = GetDte();
            var solutionInfo = new SolutionInfo(dte);

            var solutionDir = solutionInfo.TargetWorkingDirectory;
            if (Properties.Settings.Default.SetStartUpProject && solutionInfo.IsBariSolution && solutionDir != null)
            {
                try
                {
                    var startProject = solutionInfo.BariConfig.StartupPath.TrimSuffix(".exe").Split('\\').LastOrDefault() + ".csproj";

                    if (string.IsNullOrEmpty(startProject)) return;

                    var startupProject = GetProject(solutionInfo, startProject);

                    if (startupProject == null) return;

                    solutionInfo.Solution.SolutionBuild.StartupProjects = startupProject.UniqueName;

                    startupProject.ConfigurationManager.ActiveConfiguration.Properties.Item("StartAction").Value = (int)StartAction.Program;
                    startupProject.ConfigurationManager.ActiveConfiguration.Properties.Item("StartProgram").Value = Path.GetDirectoryName(solutionInfo.Solution.FileName) + "\\" + solutionInfo.BariConfig.Target
                         + "\\" + solutionInfo.BariConfig.StartupPath.Split('\\').LastOrDefault();
                    startupProject.ConfigurationManager.ActiveConfiguration.Properties.Item("StartArguments").Value =
                        Properties.Settings.Default.StartArguments;
                }
                catch (Exception)
                {
                }
            }

            //if (Properties.Settings.Default.SetExceptions && solutionInfo.IsBariSolution && solutionDir != null)
            //{
            //    try
            //    {
            //        var dbg = dte.Debugger as Debugger3;
            //        if (dbg != null)
            //        {
            //            var ex = dbg.ExceptionGroups.Item("Common Language Runtime Exceptions");
            //            if (ex != null)
            //            {
            //                var common = ex.Item("Common Language Runtime Exceptions");
            //                ex.SetBreakWhenThrown(true, common);
            //                //foreach (var exceptionSetting in ex)
            //                //{
            //                //    ex.SetBreakWhenThrown(true, (ExceptionSetting)exceptionSetting);
            //                //}
            //            }
            //        }
            //    }
            //    catch (Exception)
            //    {
            //    }
            //}
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

        protected override void Dispose(bool disposing)
        {
            GetDte().Events.SolutionEvents.Opened -= SolutionEvents_Opened;

            UnRegisterPriorityCommandTarget();

            base.Dispose(disposing);

            if (keyboardHook != null)
            {
                keyboardHook.Dispose();
                keyboardHook = null;
            }

            if (solutionWatcher != null)
            {
                solutionWatcher.Dispose();
                solutionWatcher = null;
            }
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

        private void RegisterKeyboardHook()
        {
            keyboardHook = new KeyboardHook(HandleKeyPressed);
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
            var vsRegisterPriorityCommandTarget =
                (IVsRegisterPriorityCommandTarget)GetService(typeof(SVsRegisterPriorityCommandTarget));
            if (vsRegisterPriorityCommandTarget == null) return;
            vsRegisterPriorityCommandTarget.RegisterPriorityCommandTarget(0, target, out registerCookie);
        }

        public DTE GetDte()
        {
            return GetService<DTE>();
        }

        public T GetService<T>()
        {
            return (T)GetService(typeof(T));
        }

        public int OnBeforeOpenProject(ref Guid guidProjectID, ref Guid guidProjectType, string pszFileName, IVsSolutionLoadManagerSupport pSLMgrSupport)
        {
            return 0;
        }

        public int OnDisconnect()
        {
            return 0;
        }

        public int OnAfterCloseSolution(object pUnkReserved)
        {
            return 0;
        }

        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy)
        {
            return 0;
        }

        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
        {
            return 0;
        }

        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            return 0;
        }

        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved)
        {
            return 0;
        }

        public int OnBeforeCloseSolution(object pUnkReserved)
        {
            return 0;
        }

        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy)
        {
            return 0;
        }

        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel)
        {
            return 0;
        }

        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel)
        {
            return 0;
        }

        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel)
        {
            return 0;
        }
    }
}
