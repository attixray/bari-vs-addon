using System.IO;
using EnvDTE;
using JetBrains.Annotations;

namespace KOTEM.BariVSPackage.BariExtension
{
    public class SolutionInfo
    {
        private DTE dte;
        public SolutionInfo(DTE dte)
        {
            if (dte != null)
            {
                this.dte = dte;
                Solution = dte.Solution;
            }
        }

        [CanBeNull]
        public Solution Solution { get; private set; }

        [CanBeNull]
        public string TargetWorkingDirectory
        {
            get
            {
                if (Solution == null) return null;

                var solutionDir = Path.GetDirectoryName(Solution.FileName);
                if (solutionDir == null) return null;

                return Path.Combine(solutionDir, TargetName);
            }
        }

        [CanBeNull]
        public string BariWorkingDirectory
        {
            get
            {
                if (Solution == null) return null;

                var solutionDir = Path.GetDirectoryName(Solution.FileName);
                if (solutionDir == null) return null;

                var parentDir = Directory.GetParent(solutionDir).FullName;
                return parentDir;
            }
        }

        public string TargetName
        {
            get
            {
                var bariSolutionConfig = BariConfig;
                if (bariSolutionConfig == null) return null;

                return bariSolutionConfig.Target;
            }
        }

        public bool IsBariSolution
        {
            get { return BariConfig != null; }
        }

        public BariSolutionConfig BariConfig
        {
            get
            {
                if (Solution == null) return null;

                var yamlFileName = Path.ChangeExtension(Solution.FileName, "yaml");
                return BariSolutionConfig.FromFile(yamlFileName);
            }
        }

        public bool IsDebugging
        {
            get
            {
                if (dte == null) return false;
                if (dte.Debugger == null) return false;

                return dte.Debugger.DebuggedProcesses.Count > 0;
            }
        }
    }
}