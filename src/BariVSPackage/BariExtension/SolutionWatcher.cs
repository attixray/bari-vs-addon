using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KOTEM.BariVSPackage.BariExtension
{
    public class SolutionWatcher : IDisposable
    {
        private FileSystemWatcher watcher;
        private HashSet<string> extensions = new HashSet<string>(new string[] { ".cs", ".fs", ".xaml", ".cpp", ".xml", ".h" });

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
            }
        }

        public event EventHandler Changed;

        public void Dispose()
        {
            watcher.Dispose();
            watcher = null;
        }
    }
}