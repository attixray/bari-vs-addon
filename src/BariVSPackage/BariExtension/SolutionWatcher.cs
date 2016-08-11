using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Timers;

namespace KOTEM.BariVSPackage.BariExtension
{
    internal class SolutionWatcher : IDisposable
    {
        private readonly IEnumerable<string> openedProjects;

        public class ReloadEventArgs : EventArgs
        {
            public IList<string> ItemsToReload { get; set; }
            public bool ReBuildNeeded { get; set; }
            public ReloadEventArgs(IList<string> item)
            {
                ItemsToReload = item;
            }

            public ReloadEventArgs(string item)
            {
                ItemsToReload = new List<string> { item };
            }
        }

        private FileSystemWatcher watcher;
        private FileSystemWatcher yamlWatcher;
        private readonly HashSet<string> extensions;
        private readonly HashSet<string> projExtensions;
        private Timer delAddTimer;
        private readonly IList<string> delAddFiles = new List<string>();

        public event EventHandler<ReloadEventArgs> Changed;
        public event EventHandler<ReloadEventArgs> ReloadNeeded;

        public SolutionWatcher(string srcDir, IEnumerable<string> extension, IEnumerable<string> projectExtension, IEnumerable<string> openedProjects)
        {
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

            yamlWatcher = new FileSystemWatcher(Directory.GetParent(srcDir).FullName)
            {
                EnableRaisingEvents = true,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024, // this is max
                Filter = "*.yaml",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };

            watcher.Changed += FileSystemChanged;
            watcher.Deleted += FileSystemChangedDelRenameCreated;
            watcher.Created += FileSystemChangedDelRenameCreated;
            watcher.Renamed += FileSystemChangedDelRenameCreated;

            yamlWatcher.Changed += FileSystemChanged;
            yamlWatcher.Deleted += FileSystemChangedDelRenameCreated;
            yamlWatcher.Created += FileSystemChangedDelRenameCreated;
            yamlWatcher.Renamed += FileSystemChangedDelRenameCreated;

            delAddTimer = new Timer(501);
            delAddTimer.Elapsed += deleteTimerOnElapsed;
        }

        private void FileSystemChanged(object sender, FileSystemEventArgs e)
        {
            var ext = (Path.GetExtension(e.FullPath) ?? string.Empty).ToLower();
            if (CheckProjects(e.FullPath.ToLower(), ext))
            {
                return;
            }

            Debug.WriteLine("{0} - {1}", e.FullPath, e.ChangeType);

            if (extensions.Contains(ext) && Changed != null)
            {
                Changed(this, new ReloadEventArgs(e.FullPath));
                CheckFakeDelete(sender, e, ext);
            }
            if (e.ChangeType == WatcherChangeTypes.Changed && projExtensions.Contains(ext))
            {
                if (ReloadNeeded != null)
                {
                    ReloadNeeded(this, new ReloadEventArgs(e.FullPath) { ReBuildNeeded = ext.EndsWith("yaml") });
                }
            }
        }

        private void FileSystemChangedDelRenameCreated(object sender, FileSystemEventArgs e)
        {
            var ext = (Path.GetExtension(e.FullPath) ?? string.Empty).ToLower();
            if (CheckProjects(e.FullPath.ToLower(), ext))
            {
                return;
            }

            Debug.WriteLine("{0} - {1}", e.FullPath, e.ChangeType);

            if ((e.ChangeType == WatcherChangeTypes.Deleted || e.ChangeType == WatcherChangeTypes.Created) && (extensions.Contains(ext) || projExtensions.Contains(ext)))
            {
                delAddFiles.Add(e.FullPath);
                delAddTimer.Stop();
                delAddTimer.Start();
                return;
            }

            CheckFakeDelete(sender, e, ext);
        }

        private bool CheckProjects(string e, string ext)
        {
            if (string.IsNullOrEmpty(ext))
            {
                return true;
            }

            if (!openedProjects.AsParallel().Any(p => e.StartsWith(p)) && !ext.EndsWith("yaml"))
            {
                return true;
            }
            return false;
        }

        private void CheckFakeDelete(object sender, FileSystemEventArgs e, string ext)
        {
            if ((e.ChangeType == WatcherChangeTypes.Renamed || e.ChangeType == WatcherChangeTypes.Changed) && (extensions.Contains(ext) || projExtensions.Contains(ext)))
            {
                var fakeDeletes = delAddFiles.Where(d => Path.GetFileName(d).ToLower().StartsWith(Path.GetFileName(e.FullPath.ToLower()))).ToList();
                foreach (var fakeDelete in fakeDeletes)
                {
                    delAddFiles.Remove(fakeDelete);
                    if (e.ChangeType != WatcherChangeTypes.Changed)
                    {
                        FileSystemChanged(sender, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath), e.FullPath));
                    }
                }
            }
        }

        private void deleteTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            delAddTimer.Stop();

            if (ReloadNeeded != null && delAddFiles.Any())
            {
                ReloadNeeded(this, new ReloadEventArgs(delAddFiles.Where(f => extensions.Contains((Path.GetExtension(f) ?? string.Empty).ToLower())).ToList()) { ReBuildNeeded = !delAddFiles.Any(f => projExtensions.Contains(f)) });
            }

            delAddFiles.Clear();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (watcher != null)
                {
                    watcher.Changed -= FileSystemChanged;
                    watcher.Deleted -= FileSystemChangedDelRenameCreated;
                    watcher.Created -= FileSystemChangedDelRenameCreated;
                    watcher.Renamed -= FileSystemChangedDelRenameCreated;
                    watcher.Dispose();
                    watcher = null;
                }

                if (yamlWatcher != null)
                {
                    yamlWatcher.Changed -= FileSystemChanged;
                    yamlWatcher.Deleted -= FileSystemChangedDelRenameCreated;
                    yamlWatcher.Created -= FileSystemChangedDelRenameCreated;
                    yamlWatcher.Renamed -= FileSystemChangedDelRenameCreated;
                    yamlWatcher.Dispose();
                    yamlWatcher = null;
                }

                if (delAddTimer != null)
                {
                    delAddTimer.Stop();
                    delAddTimer.Elapsed -= deleteTimerOnElapsed;
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