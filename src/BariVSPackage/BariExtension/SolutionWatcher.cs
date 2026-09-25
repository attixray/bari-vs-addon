using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using KOTEM.BariVSPackage.BariExtension.Utils;
using log4net;
using Newtonsoft.Json.Linq;

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
        private STATaskScheduler md5Scheduler;
        private System.Timers.Timer timer;
        private bool started;

        public string YamlPath { get; }
        public bool IsBusy => IsSuspended || started;

        public event EventHandler<ReloadEventArgs> Changed;
        public event EventHandler Checking;
        public event EventHandler Checked;


        public SolutionWatcher(string srcDir, IEnumerable<string> extension, IEnumerable<string> projectExtension, IEnumerable<string> openedProjects, Predicate<string> isFileOpenedInSln)
        {
            this.isFileOpenedInSln = isFileOpenedInSln;
            extensions = new HashSet<string>(extension);
            projExtensions = new HashSet<string>(projectExtension);
            scheduler = new STATaskScheduler(1);
            md5Scheduler = new STATaskScheduler(Environment.ProcessorCount);
            this.openedProjects = new List<string>();
            log.Info("SolutionWatcher initialized.");

            timer = new System.Timers.Timer(330);
            timer.AutoReset = false;
            timer.Elapsed += Timer_Elapsed;

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
            }, CancellationToken.None, TaskCreationOptions.HideScheduler, scheduler);

            watcher.Changed += FileSystemChanged;
            watcher.Deleted += FileSystemChanged;
            watcher.Created += FileSystemChanged;
            watcher.Renamed += FileSystemChanged;
            yamlWatcher.Changed += FileSystemChanged;
            yamlWatcher.Deleted += FileSystemChanged;
            yamlWatcher.Created += FileSystemChanged;
            yamlWatcher.Renamed += FileSystemChanged;
        }

        protected override void OnBeginSuspend()
        {
            Checking?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnEndSuspend(bool releaseCall)
        {
            if (releaseCall)
            {
                Checked?.Invoke(this, EventArgs.Empty);
            }
        }

        private void InitCheckSums(IEnumerable<string> projects)
        {
            var currentProjects = projects.Where(p => projExtensions.Any(p.EndsWith)).Select(p => Directory.GetParent(Path.GetDirectoryName(p)).FullName.ToLower()).ToList();
            if (currentProjects.Any())
            {
                Task.Factory.StartNew(() =>
                {
                    var watch = Stopwatch.StartNew();
                    var files = 0;
                    var checkSumss = currentProjects.Except(openedProjects).SelectMany(p =>
                        Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories)
                            .Select(f => f.ToLowerInvariant())
                            .Where(file => !IsBuildByproduct(file))
                            .Where(file => projExtensions.Any(file.EndsWith) || extensions.Any(file.EndsWith)));

                    if (checkSumss.Any())
                    {
                        var tasks = new ConcurrentBag<Task<Tuple<string, byte[]>>>();
                        Parallel.ForEach(checkSumss, (t) =>
                        {
                            var task = Task.Factory.StartNew(() => new Tuple<string, byte[]>(t, ComputeChecksum(t)), CancellationToken.None, TaskCreationOptions.None, md5Scheduler);
                            tasks.Add(task);
                        });

                        files = tasks.Count;
                        Parallel.ForEach(tasks.Where(t => t.Result.Item2 != null), (t) =>
                        {
                            if (!checkSums.ContainsKey(t.Result.Item1))
                            {
                                checkSums.Add(t.Result.Item1, t.Result.Item2);
                            }
                        });
                    }
                    foreach (var currentProject in currentProjects)
                    {
                        if (!openedProjects.Contains(currentProject))
                        {
                            openedProjects.Add(currentProject);
                        }
                    }
                    log.Info($"Checksums of {files} files computed in {watch.ElapsedMilliseconds} ms");
                }, CancellationToken.None, TaskCreationOptions.HideScheduler, scheduler);
            }
        }

        private void FileSystemChanged(object sender, FileSystemEventArgs e)
        {
            if (IsBuildByproduct(e.FullPath))
            {
                return;
            }

            started = true;

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

            StartCheck();
        }

        private void StartCheck()
        {
            timer.Stop();
            timer.Start();
        }

        private void Timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            Task.Factory.StartNew(() =>
            {
                started = false;
                CheckFiles();
            }, CancellationToken.None, TaskCreationOptions.HideScheduler, scheduler);
        }

        private void CheckFiles()
        {
            using (BeginSuspend())
            {
                Debug.WriteLine($"{DateTime.Now.ToString("hh:mm:ss:fff")} - start checksum");
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
                        .Where(file => !IsBuildByproduct(file))
                        .Where(file => projExtensions.Any(file.EndsWith) || extensions.Any(file.EndsWith) || file.EndsWith(".yaml"))).Distinct();

                var tasks = new ConcurrentBag<Task<Tuple<string, byte[]>>>();
                Parallel.ForEach(checkSumss, (t) =>
                {
                    var task = Task.Factory.StartNew(() => new Tuple<string, byte[]>(t, ComputeChecksum(t)), CancellationToken.None, TaskCreationOptions.None, md5Scheduler);
                    tasks.Add(task);
                });

                // A file that vanished or could not be read counts as absent.
                Parallel.ForEach(tasks.Where(t => t.Result.Item2 != null), (t) => { currentCheckSums.Add(t.Result.Item1, t.Result.Item2); });

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

                if (args.Any() && !args.All(ct => projExtensions.Any(ct.Key.EndsWith)))
                {
                    Changed?.Invoke(this, new ReloadEventArgs(args)
                    {
                        ReBuildNeeded = args.Any(ct => ct.Value == WatcherChangeTypes.Created || (ct.Value & WatcherChangeTypes.Deleted) != 0) || changed.Any(c => c.Key.EndsWith(".yaml"))
                    });
                }
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

        /// <summary>
        /// Returns null when the file is gone or cannot be read. Opens with FileShare.Delete so that
        /// hashing never makes a build's delete fail: bari's clean deletes the generated project files
        /// and the WPF markup compiler deletes its *_wpftmp.csproj while this watcher reacts to them.
        /// </summary>
        private byte[] ComputeChecksum(string path)
        {
            try
            {
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var bufferedStream = new BufferedStream(stream, 1048576))
                using (var md5 = new MD5CryptoServiceProvider())
                {
                    return md5.ComputeHash(bufferedStream);
                }
            }
            catch (IOException ex)
            {
                log.Debug($"Checksum skipped: {path}", ex);
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                log.Debug($"Checksum skipped: {path}", ex);
                return null;
            }
        }

        /// <summary>
        /// Project files the WPF markup compiler creates and deletes next to the real project during builds,
        /// e.g. Project_abcd1234_wpftmp.csproj.
        /// </summary>
        private static bool IsBuildByproduct(string path)
        {
            return Path.GetFileName(path).IndexOf("_wpftmp.", StringComparison.OrdinalIgnoreCase) >= 0;
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

                scheduler?.Dispose();
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