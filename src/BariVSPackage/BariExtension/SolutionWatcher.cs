using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Timer = System.Timers.Timer;

namespace KOTEM.BariVSPackage.BariExtension
{
    internal class SolutionWatcher : IDisposable
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

        private readonly ChangeCompare changeComparer = new ChangeCompare();
        private readonly Predicate<string> isFileOpenedInSln;
        private readonly IEnumerable<string> openedProjects;
        private readonly MD5 md5 = MD5.Create();
        private readonly HashSet<string> extensions;
        private readonly HashSet<string> projExtensions;
        private readonly Semaphore sema = new Semaphore(1, 1);
        private readonly IList<string> changedFiles = new List<string>();

        private IDictionary<string, byte[]> checkSums;
        private FileSystemWatcher watcher;
        private FileSystemWatcher yamlWatcher;
        private Timer delAddTimer;

        public string YamlPath { get; }

        public event EventHandler<ReloadEventArgs> Changed;

        public SolutionWatcher(string srcDir, IEnumerable<string> extension, IEnumerable<string> projectExtension, IEnumerable<string> openedProjects, Predicate<string> isFileOpenedInSln)
        {
            this.isFileOpenedInSln = isFileOpenedInSln;
            this.openedProjects = openedProjects.Where(p => projectExtension.Any(p.EndsWith)).Select(p => Directory.GetParent(Path.GetDirectoryName(p)).FullName.ToLower()).ToList();
            extensions = new HashSet<string>(extension);
            projExtensions = new HashSet<string>(projectExtension);

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


            Task.Factory.StartNew(() =>
            {
                sema.WaitOne();
                checkSums = this.openedProjects.AsParallel().SelectMany(p => Directory
                    .EnumerateFiles(p, "*.*", SearchOption.AllDirectories).AsParallel()
                    .Where(file => projExtensions.Any(e => file.ToLower().EndsWith(e)) || extensions.Any(e => file.ToLower().EndsWith(e))))
                    .Distinct().AsParallel().ToDictionary(f => f.ToLowerInvariant(), ComputeChecksum);

                checkSums.Add(YamlPath.ToLowerInvariant(), ComputeChecksum(YamlPath));
                sema.Release();
            });
            watcher.Changed += FileSystemChanged;
            watcher.Deleted += FileSystemChanged;
            watcher.Created += FileSystemChanged;
            watcher.Renamed += FileSystemChanged;
            yamlWatcher.Changed += FileSystemChanged;
            yamlWatcher.Deleted += FileSystemChanged;
            yamlWatcher.Created += FileSystemChanged;
            yamlWatcher.Renamed += FileSystemChanged;

            delAddTimer = new Timer(501);
            delAddTimer.Elapsed += DeleteTimerOnElapsed;
        }

        private void FileSystemChanged(object sender, FileSystemEventArgs e)
        {
            var ext = (Path.GetExtension(e.FullPath) ?? string.Empty).ToLower();
            if (CheckProjects(e.FullPath.ToLower(), ext))
            {
                return;
            }

            Debug.WriteLine("FileSystemChanged {0} - {1}", e.FullPath, e.ChangeType);

            lock (changedFiles)
            {
                changedFiles.Add(e.FullPath);
            }
            delAddTimer.Stop();
            delAddTimer.Start();
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

        private void DeleteTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            delAddTimer.Stop();
            sema.WaitOne();
            CheckFiles();
            sema.Release();
        }

        private void CheckFiles()
        {
            IList<string> files;
            lock (changedFiles)
            {
                files = changedFiles.Distinct()
                                    .Select(f => f.ToLowerInvariant())
                                    .Where(file => IsFolder(file) || projExtensions.Any(file.EndsWith) || extensions.Any(file.EndsWith) || file.EndsWith(".yaml"))
                                    .ToList();
                changedFiles.Clear();
            }

            var oldCheckSums = files.SelectMany(p => checkSums.AsParallel().Where(f => Path.GetDirectoryName(f.Key).Equals(IsFolder(p) ? p : Path.GetDirectoryName(p)))).ToList().Distinct();

            var currentCheckSums = files.AsParallel().SelectMany(p => Directory
                .EnumerateFiles(IsFolder(p) ? p : Path.GetDirectoryName(p), "*.*", SearchOption.TopDirectoryOnly)
                .Where(file => projExtensions.Any(e => file.ToLower().EndsWith(e)) || extensions.Any(e => file.ToLower().EndsWith(e))))
                .Distinct().AsParallel().ToDictionary(f => f.ToLowerInvariant(), ComputeChecksum);

            var added = new List<KeyValuePair<string, byte[]>>();
            var deleted = new List<KeyValuePair<string, byte[]>>();
            foreach (var currentCheckSum in currentCheckSums)
            {
                if (!checkSums.ContainsKey(currentCheckSum.Key))
                {
                    added.Add(currentCheckSum);
                }
            }

            foreach (var keyValuePair in oldCheckSums)
            {
                if (!currentCheckSums.ContainsKey(keyValuePair.Key))
                {
                    deleted.Add(keyValuePair);
                }
            }

            var changed = currentCheckSums.Except(oldCheckSums, changeComparer).ToList();

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
                Changed?.Invoke(this, new ReloadEventArgs(args) { ReBuildNeeded = args.Any(ct=> ct.Value == WatcherChangeTypes.Created || (ct.Value & WatcherChangeTypes.Deleted) != 0)|| changed.Any(c => c.Key.EndsWith(".yaml")) });
            }
        }

        private byte[] ComputeChecksum(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var bufferedStream = new BufferedStream(stream, 1048576))
            {
                return md5.ComputeHash(bufferedStream);
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

                if (delAddTimer != null)
                {
                    delAddTimer.Stop();
                    delAddTimer.Elapsed -= DeleteTimerOnElapsed;
                    delAddTimer.Dispose();
                    delAddTimer = null;
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}