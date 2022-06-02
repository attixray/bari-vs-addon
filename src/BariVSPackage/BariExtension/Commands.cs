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
    public class Commands : IDisposable
    {
        private readonly IVsServiceProvider owner;
        private BariShell bariShell;
        private bool isBuildNeeded;
        private Process debuggedProcess;
        private BariOutputPane bariOutputPane;

        public event EventHandler<BariShell.BariCommandArgs> CommandFinished;
        public event EventHandler<BariShell.BariCommandArgs> CommandStarted;

        public bool IsRunning
        {
            get { return bariShell.IsRunning; }
        }

        public Commands(IVsServiceProvider owner, string solutionFileName)
        {
            this.owner = owner;
            isBuildNeeded = true;

            var solutionInfo = new SolutionInfo(string.IsNullOrEmpty(solutionFileName) ? GetDte().Solution.FileName : solutionFileName);
            var workingDirectory = solutionInfo.BariWorkingDirectory;

            var output = owner.GetService<SVsOutputWindow>() as IVsOutputWindow;
            bariOutputPane = new BariOutputPane(output);


            if (solutionInfo.IsBariSolution)
            {
                var bariConfig = solutionInfo.BariConfig;
                bariShell = new BariShell(bariConfig.BariPath, bariConfig.Goal, bariConfig.Target, workingDirectory, bariOutputPane);
                bariShell.CommandFinished += bariShell_CommandFinished;
                bariShell.CommandStarted += bariShell_CommandStarted;
            }
        }


        private DTE GetDte()
        {
            return owner.GetDte();
        }

        private void ShowBuildStatus()
        {
            object icon = (short)Microsoft.VisualStudio.Shell.Interop.Constants.SBAI_Build;
            owner.StatusBar.Animation(1, ref icon);
            owner.StatusBar.SetText("Build started...");

        }

        private void HideBuildStatus(bool cancelled, bool completed)
        {
            object icon = (short)Microsoft.VisualStudio.Shell.Interop.Constants.SBAI_Build;
            owner.StatusBar.Animation(0, ref icon);
            if (completed)
            {
                owner.StatusBar.SetText("Build completed!");
            }
            else
            {
                owner.StatusBar.SetText("Build failed!");
            }
        }

        public void ExecuteBariBuild()
        {
            ShowBuildStatus();
            ExecuteBariBuild(HideBuildStatus);
        }

        private void ExecuteBariBuild(Action<bool, bool> after)
        {
            ExecuteBariActionAsync("build", after: after, forceAction: true);
        }

        public void ExecuteBariRebuild()
        {
            ShowBuildStatus();
            ExecuteBariActionAsync("rebuild", after: HideBuildStatus, forceAction: true);
        }

        public void ExecuteBariClean()
        {
            ExecuteBariActionAsync("clean");
        }

        private void ExecuteBariActionAsync(string actionName, bool forceAction = false, Action<bool, bool> after = null)
        {
            CancelAnyPreviousBariAction();

            bariOutputPane.Clear();
            GetDte().ExecuteCommand("View.Output");
            bariOutputPane.WriteLine(string.Format("Executing bari {0}...\n", actionName));

            bariShell.ExecuteAsync(actionName, (cancelled, completed) =>
            {
                CancelAnyPreviousBariAction();
                if (!cancelled && completed)
                {
                    isBuildNeeded = false;
                }

                if (!completed)
                {
                    isBuildNeeded = true;
                }

                if (after != null)
                {
                    after(cancelled, completed);
                }
            }, forceAction);
        }

        private void ExecuteBariAction(string actionName, bool forceAction = false, Action<bool, bool> after = null)
        {
            CancelAnyPreviousBariAction();

            bariOutputPane.Clear();
            GetDte().ExecuteCommand("View.Output");
            bariOutputPane.WriteLine(string.Format("Executing bari {0}...\n", actionName));

            bariShell.Execute(actionName, forceAction, (cancelled, completed) =>
            {
                if (!cancelled)
                {
                    isBuildNeeded = false;
                }

                after?.Invoke(cancelled, completed);
            });
        }

        private void bariShell_CommandStarted(object sender, BariShell.BariCommandArgs e)
        {
            if (bariShell != null)
            {
                if (CommandStarted != null)
                {
                    CommandStarted(this, e);
                }
            }
        }

        private void bariShell_CommandFinished(object sender, BariShell.BariCommandArgs e)
        {
            if (bariShell != null)
            {
                if (CommandFinished != null)
                {
                    CommandFinished(this, e);
                }
                bariShell.CancelAll();
            }
        }

        public void CancelAnyPreviousBariAction()
        {
            if (bariShell != null)
            {
                bariShell.CancelAll();
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
                ShowBuildStatus();
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
                ExecuteBariActionAsync("build", true, (ca, co) =>
                {
                    HideBuildStatus(ca, co);
                    if (co)
                    {
                        after(g);
                    }
                });
                return 0;
            }
            else
            {
                return after(g);
            }
        }

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
                ExecuteBariBuild((ca, co) => { StartWithoutDebugger(ca, co); HideBuildStatus(ca, co); });
                return;
            }
            StartWithoutDebugger(false, true);
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

        public void StartWithoutDebugger(bool cancelled, bool completed)
        {
            HideBuildStatus(cancelled, completed);

            if (!cancelled && completed) StartProcess();
        }

        private int StartProcess()
        {
            var solutionInfo = new SolutionInfo(GetDte().Solution.FileName);

            var solutionDir = solutionInfo.TargetWorkingDirectory;
            if (solutionDir != null)
            {
                var startupProjectName = ((Array)GetDte().Solution.SolutionBuild.StartupProjects).Cast<string>().First();
                var startupProject = owner.GetProject(startupProjectName);

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

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing && bariShell != null)
            {
                bariShell.CancelAll();
                bariShell.CommandFinished -= bariShell_CommandFinished;
                bariShell.CommandStarted -= bariShell_CommandStarted;
            }
        }
    }
}
