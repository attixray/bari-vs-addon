using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace KOTEM.BariVSPackage.Tools
{
    internal class LaunchSettings
    {
        public LaunchSettings(string profileName, string startProgram, string workingDirectory, string arguments)
        {
            profiles = new Setting(profileName) { Data = new Setting.Profile { commandName = "Executable", executablePath = startProgram, workingDirectory = workingDirectory, commandLineArgs = arguments } };
        }

        public LaunchSettings()
        {
        }

        public class Setting : DynamicObject
        {
            public class Profile
            {
                public Profile()
                {
                }
                public string commandName { get; set; }
                public string executablePath { get; set; }
                public string workingDirectory { get; set; }
                public string commandLineArgs { get; set; }
            }


            public string ProfileName { get; set; }

            public Setting()
            {
            }

            public Setting(string profileName)
            {
                this.ProfileName = profileName;
            }

            public Profile Data { get; set; }

            public override IEnumerable<string> GetDynamicMemberNames()
            {
                yield return ProfileName;
                foreach (var prop in GetType().GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != nameof(Data)))
                {
                    yield return prop.Name;
                }
            }

            public override bool TryGetMember(GetMemberBinder binder, out object result)
            {
                if (binder.Name == ProfileName)
                {
                    result = Data;
                    return true;
                }

                return base.TryGetMember(binder, out result);
            }

        }

        public Setting profiles { get; set; }

        internal void Save(string folder)
        {
            var fileName = Path.Combine(folder, "launchSettings.json");
            File.WriteAllText(fileName, Newtonsoft.Json.JsonConvert.SerializeObject(this, Newtonsoft.Json.Formatting.Indented));
        }

        internal static LaunchSettings Load(string folder, string profileName, string startProgram, string workingDirectory, string arguments)
        {
            var fileName = Path.Combine(folder, "launchSettings.json");
            try
            {
                if (File.Exists(fileName))
                {
                    var setting = Newtonsoft.Json.JsonConvert.DeserializeObject<LaunchSettings>(File.ReadAllText(fileName).Replace($"\"{profileName}\"", "Data"));
                    setting.profiles.ProfileName = profileName;
                    setting.profiles.Data.executablePath = startProgram;
                    setting.profiles.Data.workingDirectory = workingDirectory;
                    setting.profiles.Data.commandLineArgs = string.IsNullOrEmpty(setting.profiles.Data.commandLineArgs) ? arguments : setting.profiles.Data.commandLineArgs;
                    return setting;
                }
            }
            catch { }
            return new LaunchSettings(profileName, startProgram, workingDirectory, arguments);
        }
    }
}
