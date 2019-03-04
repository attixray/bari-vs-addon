using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using KOTEM.BariVSPackage.BariExtension.Utils;
using log4net;

namespace KOTEM.BariVSPackage.BariExtension
{
    internal class SolutionWatcher : SuspendableBase, IDisposable
    {
        public class ReloadEventArgs : EventArgs
        {
            public IDictionary<string, WatcherChangeTypes> ItemsToReload { get; set; }
            public bool ReBuildNeeded { get; set; }
            public ReloadEventArgs(IDictionary<string, WatcherChangeTypes> items)
            {
                ItemsToReload = items;
            }

            public ReloadEventArgs(string item, WatcherChangeTypes type)
            {
                ItemsToReload = new Dictionary<string, WatcherChangeTypes> { { item, type } };
            }
        }

        private class ChangeCompare : IEqualityComparer<KeyValuePair<string, byte[]>>
        {
            public bool Equals(KeyValuePair<string, byte[]> x, KeyValuePair<string, byte[]> y)
            {
                return x.Key.Equals(y.Key) && x.Value.SequenceEqual(y.Value);
            }

            public int GetHashCode(KeyValuePair<string, byte[]> obj)
            {
                return 0;
            }
        }

        private readonly ILog log = LogManager.GetLogger(typeof(SolutionWatcher));

        private readonly object taskLockObject = new object();
        private CancellationTokenSource tokenSource;
        private Task pendingTask;

        private readonly ChangeCompare changeComparer = new ChangeCompare();
        private readonly Predicate<string> isFileOpenedInSln;
        private readonly IList<string> openedProjects;
        private readonly HashSet<string> extensions;
        private readonly HashSet<string> projExtensions;
        private readonly IList<string> changedFiles = new List<string>();

        private IDictionary<string, byte[]> checkSums = new ConcurrentDictionary<string, byte[]>();
        private FileSystemWatcher watcher;
        private FileSystemWatcher yamlWatcher;
        private STATaskScheduler scheduler;
        private STATaskScheduler timerScheduler;
        private STATaskScheduler md5Scheduler;

        public string YamlPath { get; }
        public bool IsBusy => IsSuspended;

        public event EventHandler<ReloadEventArgs> Changed;
        public event EventHandler Checking;
        public event EventHandler Checked;


        public SolutionWatcher(string srcDir, IEnumerable<string> extension, IEnumerable<string> projectExtension, IEnumerable<string> openedProjects, Predicate<string> isFileOpenedInSln)
        {
            this.isFileOpenedInSln = isFileOpenedInSln;
            extensions = new HashSet<string>(extension);
            projExtensions = new HashSet<string>(projectExtension);
            scheduler = new STATaskScheduler(1);
            timerScheduler = new STATaskScheduler(Environment.ProcessorCount);
            md5Scheduler = new STATaskScheduler(Environment.ProcessorCount);
            this.openedProjects = new List<string>();
            log.Info("SolutionWatcher initialized.");

            watcher = new FileSystemWatcher(srcDir)
            {
                EnableRaisingEvents = true,
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024, // this is max
                Filter = "*.*",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };

            YamlPath = Directory.GetParent(srcDir).GetFiles("*.yaml").FirstOrDefault().FullName;
            yamlWatcher = new FileSystemWatcher(Directory.GetParent(srcDir).FullName)
            {
                EnableRaisingEvents = true,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024, // this is max
                Filter = "*.yaml",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };

            InitCheckSums(openedProjects);
            Task.Factory.StartNew(() =>
            {
                checkSums.Add(YamlPath.ToLowerInvariant(), ComputeChecksum(YamlPath));
            }, CancellationToken.None, TaskCreationOptions.None, scheduler);

            watcher.Changed += FileSystemChanged;
            watcher.Deleted += FileSystemChanged;
            watcher.Created += FileSystemChanged;
            watcher.Renamed += FileSystemChanged;
            yamlWatcher.Changed += FileSystemChanged;
            yamlWatcher.Deleted += FileSystemChanged;
            yamlWatcher.Created += FileSystemChanged;
            yamlWatcher.Renamed += FileSystemChanged;
        }

        private void InitCheckSums(IEnumerable<string> projects)
        {
            var currentProjects = projects.Where(p => projExtensions.Any(p.EndsWith)).Select(p => Directory.GetParent(Path.GetDirectoryName(p)).FullName.ToLower()).ToList();
            if (currentProjects.Any())
            {
                Task.Factory.StartNew(() =>
                {
                    Checking?.Invoke(this, EventArgs.Empty);
                    var checkSumss = currentProjects.Except(openedProjects).SelectMany(p =>
                        Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories)
                            .Select(f => f.ToLowerInvariant())
                            .Where(file => projExtensions.Any(file.EndsWith) || extensions.Any(file.EndsWith)));

                    var tasks = new ConcurrentBag<Task<Tuple<string, byte[]>>>();
                    Parallel.ForEach(checkSumss, (t) =>
                    {
                        var task = Task.Factory.StartNew(() => new Tuple<string, byte[]>(t, ComputeChecksum(t)), CancellationToken.None, TaskCreationOptions.None, md5Scheduler);
                        tasks.Add(task);
                    });

                    Parallel.ForEach(tasks, (t) =>
                    {
                        if (!checkSums.ContainsKey(t.Result.Item1))
                        {
                            checkSums.Add(t.Result.Item1, t.Result.Item2);
                        }
                    });

                    foreach (var currentProject in currentProjects)
                    {
                        if (!openedProjects.Contains(currentProject))
                        {
                            openedProjects.Add(currentProject);
                        }
                    }

                    Checked?.Invoke(this, EventArgs.Empty);
                }, CancellationToken.None, TaskCreationOptions.None, scheduler);
            }
        }

        private void FileSystemChanged(object sender, FileSystemEventArgs e)
        {
            var ext = (Path.GetExtension(e.FullPath) ?? string.Empty).ToLower();
            if (CheckProjects(e.FullPath.ToLower(), ext))
            {
                return;
            }

            lock (changedFiles)
            {
                changedFiles.Add(e.FullPath);
            }

            Checking?.Invoke(this, EventArgs.Empty);

            StartCheck(CancellationToken.None);
        }

        private void StartCheck(CancellationToken token)
        {
            lock (taskLockObject)
            {
                var previousCts = tokenSource;
                var previousTask = pendingTask;
                var newCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                tokenSource = newCts;
                pendingTask = null;

                pendingTask = Task.Factory.StartNew(state =>
                {
                    using (BeginSuspend())
                    {
                        var ptuple = state as Tuple<CancellationTokenSource, Task, CancellationToken>;
                        var pCTS = ptuple.Item1;
                        var pTask = ptuple.Item2;
                        var nToken = ptuple.Item3;
                        if (pCTS != null && pTask != null)
                        {
                            // cancel the previous session and wait for its termination
                            if (!pTask.IsCompleted && !pCTS.IsCancellationRequested)
                            {
                                pCTS.Cancel();
                            }

                            try
                            {
                                if (!pTask.IsFaulted)
                                {
                                    pTask.Wait(nToken);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                            }
                            finally
                            {
                                pCTS.Dispose();
                            }
                        }

                        try
                        {
                            Task.Delay(330, nToken).Wait(nToken);
                            Check(nToken).Wait(nToken);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }
                }, new Tuple<CancellationTokenSource, Task, CancellationToken>(previousCts, previousTask, newCts.Token), CancellationToken.None, TaskCreationOptions.HideScheduler, timerScheduler);
            }
        }

        private bool CheckProjects(string e, string ext)
        {
            if (string.IsNullOrEmpty(ext))
            {
                return false;
            }

            if (!openedProjects.AsParallel().Any(e.StartsWith) && !ext.EndsWith("yaml"))
            {
                return true;
            }
            return false;
        }

        private bool IsFolder(string path)
        {
            return string.IsNullOrEmpty(Path.GetExtension(path) ?? string.Empty);
        }

        private Task Check(CancellationToken token)
        {
            return Task.Factory.StartNew(() =>
            {
                CheckFiles(token);
            }, token, TaskCreationOptions.None, scheduler).ContinueWith((t) =>
                {
                    Checked?.Invoke(this, EventArgs.Empty);
                }, token, TaskContinuationOptions.OnlyOnRanToCompletion, scheduler);
        }

        private void CheckFiles(CancellationToken token)
        {
            using (BeginSuspend())
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                IList<string> files;
                lock (changedFiles)
                {
                    files = changedFiles.Distinct()
                        .Select(f => f.ToLowerInvariant())
                        .Where(f => openedProjects.Any(f.ToLowerInvariant().StartsWith) || f.EndsWith(".yaml"))
                        .Where(f => IsFolder(f) || projExtensions.Any(f.EndsWith) || extensions.Any(f.EndsWith) || f.EndsWith(".yaml"))
                        .ToList();
                    changedFiles.Clear();

                    if (!files.Any())
                    {
                        return;
                    }
                }

                var oldCheckSums = files.SelectMany(p => checkSums.AsParallel().Where(f => Path.GetDirectoryName(f.Key).Equals(IsFolder(p) ? p : Path.GetDirectoryName(p)))).ToList().Distinct();

                IDictionary<string, byte[]> currentCheckSums = new ConcurrentDictionary<string, byte[]>();

                var checkSumss = files.SelectMany(p =>
                    Directory.EnumerateFiles(IsFolder(p) ? p : Path.GetDirectoryName(p), "*.*", SearchOption.TopDirectoryOnly)
                        .Select(f => f.ToLowerInvariant())
                        .Where(file => projExtensions.Any(file.EndsWith) || extensions.Any(file.EndsWith))).Distinct();

                var tasks = new ConcurrentBag<Task<Tuple<string, byte[]>>>();
                Parallel.ForEach(checkSumss, (t) =>
                {
                    var task = Task.Factory.StartNew(() => new Tuple<string, byte[]>(t, ComputeChecksum(t)), CancellationToken.None, TaskCreationOptions.None, md5Scheduler);
                    tasks.Add(task);
                });

                Parallel.ForEach(tasks, (t) => { currentCheckSums.Add(t.Result.Item1, t.Result.Item2); });

                var added = new List<KeyValuePair<string, byte[]>>();
                var deleted = new List<KeyValuePair<string, byte[]>>();
                foreach (var currentCheckSum in currentCheckSums.Where(v => extensions.Any(v.Key.EndsWith) || v.Key.EndsWith(".yaml")))
                {
                    if (!checkSums.ContainsKey(currentCheckSum.Key))
                    {
                        added.Add(currentCheckSum);
                    }
                }

                foreach (var keyValuePair in oldCheckSums.Where(v => extensions.Any(v.Key.EndsWith) || v.Key.EndsWith(".yaml")))
                {
                    if (!currentCheckSums.ContainsKey(keyValuePair.Key))
                    {
                        deleted.Add(keyValuePair);
                    }
                }

                var changed = currentCheckSums.Except(oldCheckSums, changeComparer)
                    .Concat(files.Where(f => projExtensions.Any(f.EndsWith)).Select(f => new KeyValuePair<string, byte[]>(f, new byte[] { }))).ToList();

                var args = new Dictionary<string, WatcherChangeTypes>();

                foreach (var keyValuePair in added)
                {
                    checkSums.Add(keyValuePair);
                    if (!isFileOpenedInSln(keyValuePair.Key))
                    {
                        args.Add(keyValuePair.Key, WatcherChangeTypes.Created);
                    }
                }

                foreach (var keyValuePair in deleted)
                {
                    checkSums.Remove(keyValuePair.Key);
                    if (isFileOpenedInSln(keyValuePair.Key))
                    {
                        args.Add(keyValuePair.Key, WatcherChangeTypes.Deleted);
                    }
                    else
                    {
                        args.Add(keyValuePair.Key, WatcherChangeTypes.Changed);
                    }
                }

                foreach (var keyValuePair in changed)
                {
                    checkSums[keyValuePair.Key] = keyValuePair.Value;
                    args[keyValuePair.Key] = WatcherChangeTypes.Changed;
                }

                if (args.Any())
                {
                    Changed?.Invoke(this, new ReloadEventArgs(args)
                    {
                        ReBuildNeeded = args.Any(ct => ct.Value == WatcherChangeTypes.Created || (ct.Value & WatcherChangeTypes.Deleted) != 0) || changed.Any(c => c.Key.EndsWith(".yaml"))
                    });
                }
            }
        }

        private byte[] ComputeChecksum(string path)
        {
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var bufferedStream = new BufferedStream(stream, 1048576))
            {
                return new MD5CryptoServiceProvider().ComputeHash(bufferedStream);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (watcher != null)
                {
                    watcher.Changed -= FileSystemChanged;
                    watcher.Dispose();
                    watcher = null;
                }

                if (yamlWatcher != null)
                {
                    yamlWatcher.Changed -= FileSystemChanged;
                    yamlWatcher.Dispose();
                    yamlWatcher = null;
                }

                if (tokenSource != null)
                {
                    if (!tokenSource.IsCancellationRequested)
                    {
                        tokenSource.Cancel();
                    }
                    if (pendingTask != null && !pendingTask.IsCompleted)
                    {
                        pendingTask.Wait();
                    }
                    if (pendingTask != null)
                    {
                        pendingTask.Dispose();
                    }
                }

                scheduler?.Dispose();
                timerScheduler?.Dispose();
                md5Scheduler?.Dispose();
            }
        }

        public void AddProject(IEnumerable<string> openedProjects)
        {
            InitCheckSums(openedProjects);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}