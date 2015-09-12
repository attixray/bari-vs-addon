using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
            watcher.Deleted += FileSystemChanged;
            watcher.Created += FileSystemChanged;
            watcher.Renamed += FileSystemChanged;
        }

        private void FileSystemChanged(object sender, FileSystemEventArgs e)
        {
            if (Changed != null)
            {
                var ext = (Path.GetExtension(e.FullPath) ?? string.Empty).ToLower();

                if (extensions.Contains(ext))
                    Changed(this, EventArgs.Empty);

                if ((e.ChangeType == WatcherChangeTypes.Created ||
                    e.ChangeType == WatcherChangeTypes.Deleted ||
                    e.ChangeType == WatcherChangeTypes.Renamed) && extensions.Contains(ext))
                {
                    if (ReloadNeeded != null)
                        ReloadNeeded(this, new ReloadEventArgs(e.FullPath){ReBuildNeeded = true});
                }

                if (e.ChangeType == WatcherChangeTypes.Changed && projExtensions.Contains(ext))
                {
                    if (ReloadNeeded != null)
                        ReloadNeeded(this, new ReloadEventArgs(e.FullPath));
                }
            }
        }

        public event EventHandler Changed;
        public event EventHandler<ReloadEventArgs> ReloadNeeded;

        public void Dispose()
        {
            watcher.Dispose();
            watcher = null;
        }
    }
}