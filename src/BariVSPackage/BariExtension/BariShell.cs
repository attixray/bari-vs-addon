using System;
using System.Diagnostics;
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

        public int Execute(string actionName, bool forceAction, Func<bool, int> after = null)
        {
            running = true;
            if (CommandStarted != null)
            {
                CommandStarted(this, new BariCommandArgs(actionName));
            }
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

                proc.Start();
                proc.BeginOutputReadLine();
                var cancelled = false;

                DispatcherFrame frame = new DispatcherFrame();

                Task.Factory.StartNew(() =>
                {
                    while (true)
                    {
                        proc.WaitForExit(100);
                        if (isCancellationRequested)
                        {
                            proc.Kill();
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
                    return after(cancelled);
                }
                return 0;
            }
            finally
            {
                running = false;
                if (CommandFinished != null)
                {
                    CommandFinished(this, new BariCommandArgs(actionName));
                }
            }
        }

        private void ShowOutput(string data)
        {
            bariOutputPane.WriteLine(string.Format("{0}", data));
        }

        public void ExecuteAsync(string actionName, Action<bool> after, bool forceAction)
        {
            isCancellationRequested = false;
            Task.Factory.StartNew(() => Execute(actionName, forceAction, (f) => { after(f); return 0; }));
        }

        public void CancelAll()
        {
            isCancellationRequested = true;
        }
    }
}