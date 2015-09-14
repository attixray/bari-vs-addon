using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Timers;

namespace KOTEM.BariVSPackage.BariExtension
{
    public class SolutionWatcher : IDisposable
    {
        public class ReloadEventArgs : EventArgs
        {
            public string ItemToReload { get; set; }
            public bool ReBuildNeeded { get; set; } 
            public ReloadEventArgs(string item)
            {
                ItemToReload = item;
            }
        }

        private FileSystemWatcher watcher;
        private HashSet<string> extensions = new HashSet<string>(new [] { ".cs", ".fs", ".xaml", ".cpp", ".xml", ".h", ".c" });
        private HashSet<string> projExtensions = new HashSet<string>(new[] {".yaml", ".csproj", ".vcxproj", ".fsproj", ".vcproj" });
        private Timer deleteTimer;
        private readonly IList<string> deletedFiles = new List<string>(); 

        public event EventHandler Changed;
        public event EventHandler<ReloadEventArgs> ReloadNeeded;

        public SolutionWatcher(string srcDir)
        {
            watcher = new FileSystemWatcher(srcDir)
                          {
                              EnableRaisingEvents = true,
                              IncludeSubdirectories = true,
                              InternalBufferSize = 64 * 1024, // this is max
                              Filter = "*.*",
                              NotifyFilter = NotifyFilters.LastAccess | NotifyFilters.LastWrite
                                             | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                          };
            watcher.Changed += FileSystemChanged;
            watcher.Deleted += FileSystemChangedDelRename;
            watcher.Created += FileSystemChanged;
            watcher.Renamed += FileSystemChangedDelRename;

            deleteTimer = new Timer(50);
            deleteTimer.Elapsed += deleteTimerOnElapsed;
        }
        
        private void FileSystemChanged(object sender, FileSystemEventArgs e)
        {
            if (Changed != null)
            {
                var ext = (Path.GetExtension(e.FullPath) ?? string.Empty).ToLower();

                if (extensions.Contains(ext))
                    Changed(this, EventArgs.Empty);

                if (e.ChangeType == WatcherChangeTypes.Created && extensions.Contains(ext))
                {
                    if (ReloadNeeded != null)
                        ReloadNeeded(this, new ReloadEventArgs(e.FullPath) {ReBuildNeeded = true});
                }

                if (e.ChangeType == WatcherChangeTypes.Changed && projExtensions.Contains(ext))
                {
                    if (ReloadNeeded != null)
                        ReloadNeeded(this, new ReloadEventArgs(e.FullPath));
                }
            }
        }

        private void FileSystemChangedDelRename(object sender, FileSystemEventArgs e)
        {
            var ext = (Path.GetExtension(e.FullPath) ?? string.Empty).ToLower();

            if (e.ChangeType == WatcherChangeTypes.Deleted)
            {
                deletedFiles.Add(e.FullPath);
                deleteTimer.Stop();
                deleteTimer.Start();
            }

            if (e.ChangeType == WatcherChangeTypes.Renamed && (extensions.Contains(ext) || projExtensions.Contains(ext)))
            {
                var fakeDeletes = deletedFiles.Where(d => Path.GetFileName(d).StartsWith(Path.GetFileName(e.FullPath))).ToList();
                foreach (var fakeDelete in fakeDeletes)
                {
                    deletedFiles.Remove(fakeDelete);
                    FileSystemChanged(sender, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath), e.FullPath));
                }
            }
        }
        private void deleteTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            deleteTimer.Stop();

            foreach (var deletedFile in deletedFiles.Where(f => extensions.Contains((Path.GetExtension(f) ?? string.Empty).ToLower())))
            {
                if (ReloadNeeded != null)
                    ReloadNeeded(this, new ReloadEventArgs(deletedFile) { ReBuildNeeded = true });    
            }

            deletedFiles.Clear();
        }

        public void Dispose()
        {
            if (watcher != null)
            {
                watcher.Dispose();
                watcher = null;
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
}