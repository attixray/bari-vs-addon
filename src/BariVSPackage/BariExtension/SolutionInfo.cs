using System.IO;
using EnvDTE;

namespace KOTEM.BariVSPackage.BariExtension
{
    public class SolutionInfo
    {
        private BariSolutionConfig bariConfig;

        public SolutionInfo(string solutionFileName)
        {
            Solution = solutionFileName;
        }

        public string Solution { get; private set; }

        public string TargetWorkingDirectory
        {
            get
            {
                if (string.IsNullOrEmpty(Solution))
                {
                    return null;
                }

                var solutionDir = Path.GetDirectoryName(Solution);
                if (solutionDir == null) return null;

                return Path.Combine(solutionDir, TargetName);
            }
        }

        public string BariWorkingDirectory
        {
            get
            {
                if (string.IsNullOrEmpty(Solution))
                {
                    return null;
                }

                var solutionDir = Path.GetDirectoryName(Solution);
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
                if (string.IsNullOrEmpty(Solution))
                {
                    return null;
                }

                if (bariConfig != null)
                {
                    return bariConfig;
                }

                var yamlFileName = Path.ChangeExtension(Solution, "yaml");
                return  bariConfig = BariSolutionConfig.FromFile(yamlFileName);
            }
        }
    }
}