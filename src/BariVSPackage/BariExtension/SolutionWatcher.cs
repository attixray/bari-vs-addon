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
        private Timer deleteTimer;
        private readonly IList<string> deletedFiles = new List<string>();

        public event EventHandler<ReloadEventArgs> Changed;
        public event EventHandler<ReloadEventArgs> ReloadNeeded;

        public SolutionWatcher(string srcDir, IEnumerable<string> extension, IEnumerable<string> projectExtension)
        {
            extensions = new HashSet<string>(extension);
            projExtensions = new HashSet<string>(projectExtension);

            watcher = new FileSystemWatcher(srcDir)
                          {
                              EnableRaisingEvents = true,
                              IncludeSubdirectories = true,
                              InternalBufferSize = 64 * 1024, // this is max
                              Filter = "*.*",
                              NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                          };

            yamlWatcher = new FileSystemWatcher(Directory.GetParent(srcDir).FullName)
            {
                EnableRaisingEvents = true,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024, // this is max
                Filter = "*.yaml",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
            };

            watcher.Changed += FileSystemChanged;
            watcher.Deleted += FileSystemChangedDelRenameCreated;
            watcher.Created += FileSystemChangedDelRenameCreated;
            watcher.Renamed += FileSystemChangedDelRenameCreated;

            yamlWatcher.Changed += FileSystemChanged;
            yamlWatcher.Deleted += FileSystemChangedDelRenameCreated;
            yamlWatcher.Created += FileSystemChangedDelRenameCreated;
            yamlWatcher.Renamed += FileSystemChangedDelRenameCreated;

            deleteTimer = new Timer(351);
            deleteTimer.Elapsed += deleteTimerOnElapsed;
        }

        private void FileSystemChanged(object sender, FileSystemEventArgs e)
        {
            Debug.WriteLine("{0} - {1}", e.FullPath, e.ChangeType);

            var ext = (Path.GetExtension(e.FullPath) ?? string.Empty).ToLower();

            if (extensions.Contains(ext) && Changed != null)
            {
                Changed(this, new ReloadEventArgs(e.FullPath));
            }
            if (e.ChangeType == WatcherChangeTypes.Changed && projExtensions.Contains(ext))
            {
                if (ReloadNeeded != null)
                    ReloadNeeded(this, new ReloadEventArgs(e.FullPath) { ReBuildNeeded = ext.EndsWith("yaml") });
            }
        }

        private void FileSystemChangedDelRenameCreated(object sender, FileSystemEventArgs e)
        {
            Debug.WriteLine("{0} - {1}", e.FullPath, e.ChangeType);


            var ext = (Path.GetExtension(e.FullPath) ?? string.Empty).ToLower();

            if ((e.ChangeType == WatcherChangeTypes.Deleted || e.ChangeType == WatcherChangeTypes.Created) && (extensions.Contains(ext) || projExtensions.Contains(ext)))
            {
                deletedFiles.Add(e.FullPath);
                deleteTimer.Stop();
                deleteTimer.Start();
                return;
            }

            if ((e.ChangeType == WatcherChangeTypes.Renamed) && (extensions.Contains(ext) || projExtensions.Contains(ext)))
            {
                var fakeDeletes = deletedFiles.Where(d => Path.GetFileName(d).ToLower().StartsWith(Path.GetFileName(e.FullPath.ToLower()))).ToList();
                foreach (var fakeDelete in fakeDeletes)
                {
                    deletedFiles.Remove(fakeDelete);
                   // FileSystemChanged(sender, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath), e.FullPath));
                }
            }
        }
        private void deleteTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            deleteTimer.Stop();

            if (ReloadNeeded != null && deletedFiles.Any())
            {
                ReloadNeeded(this, new ReloadEventArgs(deletedFiles.Where(f => extensions.Contains((Path.GetExtension(f) ?? string.Empty).ToLower())).ToList()) { ReBuildNeeded = !deletedFiles.Any(f => projExtensions.Contains(f)) });
            }

            deletedFiles.Clear();
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

                if (deleteTimer != null)
                {
                    deleteTimer.Stop();
                    deleteTimer.Elapsed -= deleteTimerOnElapsed;
                    deleteTimer.Dispose();
                    deleteTimer = null;
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