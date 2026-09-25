using System;
using System.Diagnostics;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Threading;
using KOTEM.BariVSPackage.Properties;

namespace KOTEM.BariVSPackage.BariExtension
{
    public class BariShell
    {
        public class BariCommandArgs : EventArgs
        {
            public BariCommandArgs(string action)
            {
                ActionName = action;
            }
            public string ActionName { get; set; }
        }


        private readonly string bariPath;
        private readonly BariOutputPane bariOutputPane;
        private readonly string productName;
        private readonly string workingDirectory;
        private bool isCancellationRequested;
        private string goal;
        private bool running;

        public event EventHandler<BariCommandArgs> CommandFinished;
        public event EventHandler<BariCommandArgs> CommandStarted;

        public BariShell(string bariPath, string goal, string productName, string workingDirectory, BariOutputPane bariOutputPane)
        {
            this.bariPath = bariPath;
            this.bariOutputPane = bariOutputPane;
            this.goal = goal;
            this.productName = productName;
            this.workingDirectory = workingDirectory;
        }

        public bool IsRunning
        {
            get { return running; }
        }

        public void Execute(string actionName, bool forceAction, Action<bool, bool> after = null)
        {
            running = true;
            CommandStarted?.Invoke(this, new BariCommandArgs(actionName));
            try
            {
                var arguments = string.Format("{0} --target {1} {2} {3} {4}",
                            Settings.Default.Verbose ? " -v " : string.Empty,
                            goal,
                            actionName,
                            productName,
                            Settings.Default.SoftClean && (actionName.StartsWith("rebuild") || actionName.StartsWith("clean")) ? " --soft-clean " : string.Empty);

                ShowOutput(string.Format("{0} {1}", bariPath, arguments));

                var processStartInfo = new ProcessStartInfo(
                    bariPath,
                    arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory,
                };

                var proc = new Process { StartInfo = processStartInfo };
                proc.OutputDataReceived += (sendingProcess, outLine)
                    => ShowOutput(outLine.Data);
                // stderr is redirected, so it must be drained too: once its pipe buffer fills,
                // bari blocks on the next write and the build never finishes.
                proc.ErrorDataReceived += (sendingProcess, errLine) =>
                {
                    if (errLine.Data != null)
                        ShowOutput(errLine.Data);
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                var cancelled = false;

                DispatcherFrame frame = new DispatcherFrame();

                Task.Factory.StartNew(() =>
                {
                    while (true)
                    {
                        proc.WaitForExit(100);
                        if (isCancellationRequested)
                        {
                            KillProcessAndChildren(proc.Id);
                            ShowOutput("Build cancelled.");
                            cancelled = true;
                            frame.Continue = false;
                            break;
                        }
                        if (proc.HasExited)
                        {
                            frame.Continue = false;
                            break;
                        }
                    }
                });
                Dispatcher.PushFrame(frame);

                if (after != null && (forceAction || proc.ExitCode == 0))
                {
                    after(cancelled, proc.ExitCode == 0);
                }
            }
            finally
            {
                running = false;
                CommandFinished?.Invoke(this, new BariCommandArgs(actionName));
            }
        }

        /// <summary>
        /// Kill a process, and all of its children, grandchildren, etc.
        /// </summary>
        /// <param name="pid">Process ID.</param>
        private static void KillProcessAndChildren(int pid)
        {
            var searcher = new ManagementObjectSearcher("Select * From Win32_Process Where ParentProcessID=" + pid);
            var moc = searcher.Get();
            foreach (var mo in moc)
            {
                KillProcessAndChildren(Convert.ToInt32(mo["ProcessID"]));
            }
            try
            {
                var proc = Process.GetProcessById(pid);
                proc.Kill();
            }
            catch (ArgumentException)
            {
                // Process already exited.
            }
        }

        private void ShowOutput(string data)
        {
            bariOutputPane.WriteLine(string.Format("{0}", data));
        }

        public void ExecuteAsync(string actionName, Action<bool, bool> after, bool forceAction)
        {
            isCancellationRequested = false;
            Task.Factory.StartNew(() => Execute(actionName, forceAction, after));
        }

        public void CancelAll()
        {
            isCancellationRequested = true;
        }
    }
}