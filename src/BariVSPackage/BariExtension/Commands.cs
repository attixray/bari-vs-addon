using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using EnvDTE;
using Microsoft.VisualStudio.Shell.Interop;
using Process = System.Diagnostics.Process;

namespace KOTEM.BariVSPackage.BariExtension
{
    public class Commands
    {
        private readonly IVsServiceProvider owner;
        private BariShell bariShell;
        private bool isBuildNeeded;
        private Process debuggedProcess;

        public Commands(IVsServiceProvider owner)
        {
            this.owner = owner;
            isBuildNeeded = true;
        }

        private DTE GetDte()
        {
            return owner.GetDte();
        }

        private void ShowBuildStatus()
        {
            //var dte = GetDte();
            //dte.StatusBar.Progress(true, "Building...");
        }

        private void HideBuildStatus(bool cancelled)
        {
            //var dte = GetDte();
            //dte.StatusBar.Progress(false);
        }

        public void ExecuteBariBuild()
        {
            ShowBuildStatus();
            ExecuteBariBuild(HideBuildStatus);
        }

        private void ExecuteBariBuild(Action<bool> after)
        {
            ExecuteBariActionAsync("build", after: after);
        }

        public void ExecuteBariRebuild()
        {
            ExecuteBariActionAsync("rebuild", after: HideBuildStatus);
        }

        public void ExecuteBariClean()
        {
            ExecuteBariActionAsync("clean");
        }

        private void ExecuteBariActionAsync(string actionName, bool forceAction = false, Action<bool> after = null)
        {
            var solutionInfo = new SolutionInfo(GetDte());

            CancelAnyPreviousBariAction();

            var output = owner.GetService<SVsOutputWindow>() as IVsOutputWindow;
            var bariOutputPane = new BariOutputPane(output);
            bariOutputPane.Clear();
            bariOutputPane.WriteLine(string.Format("Executing bari {0}...\n", actionName));

            var workingDirectory = solutionInfo.BariWorkingDirectory;

            var bariConfig = solutionInfo.BariConfig;

            bariShell = new BariShell(bariConfig.BariPath, bariConfig.Goal, bariConfig.Target, workingDirectory, bariOutputPane);
            bariShell.ExecuteAsync(actionName, cancelled =>
            {
                CancelAnyPreviousBariAction();
                if (!cancelled)
                {
                    isBuildNeeded = false;
                }
                if (after != null)
                {
                    after(cancelled);
                }
            }, forceAction);
        }

        private int ExecuteBariAction(string actionName, bool forceAction = false, Func<bool, int> after = null)
        {
            var solutionInfo = new SolutionInfo(GetDte());

            CancelAnyPreviousBariAction();

            var output = owner.GetService<SVsOutputWindow>() as IVsOutputWindow;
            var bariOutputPane = new BariOutputPane(output);
            bariOutputPane.Clear();
            bariOutputPane.WriteLine(string.Format("Executing bari {0}...\n", actionName));

            var workingDirectory = solutionInfo.BariWorkingDirectory;

            var bariConfig = solutionInfo.BariConfig;

            bariShell = new BariShell(bariConfig.BariPath, bariConfig.Goal, bariConfig.Target, workingDirectory, bariOutputPane);
            return bariShell.Execute(actionName, forceAction, cancelled =>
            {
                CancelAnyPreviousBariAction();
                if (!cancelled)
                {
                    isBuildNeeded = false;
                }
                if (after != null)
                {
                    return after(cancelled);
                }
                return 0;
            });
        }

        public void CancelAnyPreviousBariAction()
        {
            if (bariShell != null)
            {
                bariShell.CancelAll();
                bariShell = null;
            }
        }

        public void StopDebugger()
        {
            var dte = GetDte();
            try
            {
                dte.Debugger.TerminateAll();
            }
            catch (InvalidOperationException)
            {

            }
        }

        public bool IsDebugging()
        {
            var dte = GetDte();
            if (dte == null) return false;
            if (dte.Debugger == null) return false;

            return dte.Debugger.DebuggedProcesses.Count > 0;
        }

        public int BuildIfNeeded(Func<Guid, int> after, Guid g)
        {
            if (IsBuildNeeded)
            {
                var promptStopDebuggerResult = IsDebugging() ? PromptStopDebugger() : PromptStopDebuggerResult.StopDebuggerAndExecuteAction;
                switch (promptStopDebuggerResult)
                {
                    case PromptStopDebuggerResult.Cancel:
                        return 0;
                    case PromptStopDebuggerResult.StopDebuggerAndExecuteAction:
                        StopDebugger();
                        break;
                    case PromptStopDebuggerResult.KeepDebuggingAndExecuteAction:
                        break;
                }
                return ExecuteBariAction("build", false, c =>
                {
                    HideBuildStatus(c);
                    return after(g);
                });
            }
            else
            {
                return after(g);
            }
        }

        //public void ExecuteStartWithDebugger()
        //{
        //    if (IsBuildNeeded)
        //    {
        //        var promptStopDebuggerResult = IsDebugging() ? PromptStopDebugger() : PromptStopDebuggerResult.StopDebuggerAndExecuteAction;
        //        switch (promptStopDebuggerResult)
        //        {
        //            case PromptStopDebuggerResult.Cancel:
        //                return;
        //            case PromptStopDebuggerResult.StopDebuggerAndExecuteAction:
        //                StopDebugger();
        //                break;
        //            case PromptStopDebuggerResult.KeepDebuggingAndExecuteAction:
        //                break;
        //        }
        //        ExecuteBariBuild(c => { StartWithDebugger(c); HideBuildStatus(c); });
        //        return;
        //    }
        //    StartWithDebugger(false);
        //}

        private void ExecuteStartWithoutDebugger(bool cancelled)
        {
            if (IsBuildNeeded)
            {
                var promptStopDebuggerResult = IsDebugging() ? PromptStopDebugger() : PromptStopDebuggerResult.StopDebuggerAndExecuteAction;
                switch (promptStopDebuggerResult)
                {
                    case PromptStopDebuggerResult.Cancel:
                        return;
                    case PromptStopDebuggerResult.StopDebuggerAndExecuteAction:
                        StopDebugger();
                        break;
                    case PromptStopDebuggerResult.KeepDebuggingAndExecuteAction:
                        break;
                }
                ExecuteBariBuild(c => { StartWithoutDebugger(c); HideBuildStatus(c); });
                return;
            }
            StartWithoutDebugger(false);
        }

        private PromptStopDebuggerResult PromptStopDebugger()
        {
            var dialog = new PromptStopDebuggerDialog();
            dialog.ShowDialog();
            return dialog.Result;
        }

        public void ExecuteStartWithoutDebugger()
        {
            ExecuteStartWithoutDebugger(false);
        }

        //public void StartWithDebugger(bool cancelled)
        //{
        //    HideBuildStatus(cancelled);

        //    if (cancelled) return;

        //    new SolutionInfo(GetDte());
        //    var dte = GetDte();
        //    if (dte.Debugger.DebuggedProcesses.Count > 0)
        //    {
        //        dte.Debugger.Go(false);
        //        return;
        //    }

        //    var processId = StartProcess();

        //    AttachDebugger(processId);
        //}

        public void StartWithoutDebugger(bool cancelled)
        {
            HideBuildStatus(cancelled);

            if (!cancelled) StartProcess();
        }

        //private void AttachDebugger(int processId)
        //{
        //    var dte = GetDte();
        //    var dteProcess = dte.Debugger.LocalProcesses.OfType<EnvDTE.Process>().FirstOrDefault(p => p.ProcessID == processId);
        //    if (dteProcess != null)
        //    {
        //        dteProcess.Attach();
        //    }
        //}

        private int StartProcess()
        {
            var solutionInfo = new SolutionInfo(GetDte());

            var solutionDir = solutionInfo.TargetWorkingDirectory;
            if (solutionDir != null)
            {
                var startupProjectName = ((Array)solutionInfo.Solution.SolutionBuild.StartupProjects).Cast<string>().First();
                var startupProject = GetProject(solutionInfo, startupProjectName);

                if (startupProject == null)
                    return -1;

                var configurationManager = startupProject.ConfigurationManager;
                var activeConfiguration = configurationManager.ActiveConfiguration;
                var startParameters = StartParameters.FromProperties(activeConfiguration.Properties);

                var exeName = Path.Combine(solutionDir, GetExeName(startParameters, startupProject));
                try
                {
                    var processStartInfo = new ProcessStartInfo(exeName)
                    {
                        WorkingDirectory = string.IsNullOrEmpty(startParameters.StartWorkingDirectory) ? solutionInfo.TargetWorkingDirectory : startParameters.StartWorkingDirectory,
                        Arguments = startParameters.StartArguments,
                        UseShellExecute = false
                    };

                    debuggedProcess = new Process { StartInfo = processStartInfo };
                    debuggedProcess.Start();
                    var processId = debuggedProcess.Id;
                    return processId;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        string.Format(
                            "Failed to start '{0}'.{1}" +
                            "Check Debug\\Properties\\Debug\\Startup action.{1}{1}" +
                            "{2}", exeName, Environment.NewLine, ex),
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
            return -1;
        }

        private Project GetProject(SolutionInfo solutionInfo, string name)
        {
            foreach (Project solFolder in solutionInfo.Solution.Projects)
            {
                if (solFolder != null)
                {
                    if (solFolder.UniqueName == name)
                        return solFolder;
                }

                foreach (var projectItem in solFolder.ProjectItems)
                {
                    ProjectItem tmpItem = projectItem as ProjectItem;
                    if (tmpItem != null)
                    {
                        Project proj = tmpItem.Object as Project;
                        if (proj != null && proj.UniqueName == name)
                            return proj;
                    }


                }
            }
            return null;
        }

        private static string GetExeName(StartParameters startParameters, Project startupProject)
        {
            if (startParameters.StartAction == StartAction.Program)
            {
                return Path.GetFileName(startParameters.StartProgram);
            }

            if (startParameters.StartAction == StartAction.StartupProject)
            {
                return string.Format("{0}.exe", Path.GetFileNameWithoutExtension(startupProject.FileName));
            }
            return null;
        }

        public bool IsBuildNeeded
        {
            get { return isBuildNeeded; }
            set { isBuildNeeded = value; }
        }
    }
}
