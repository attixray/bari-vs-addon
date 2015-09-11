using System;
using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace KOTEM.BariVSPackage.BariExtension.Option
{
    public class AddonOptionsDialog : DialogPage
    {
        [Category("Startup")]
        [Description("Set startup project on solution loaded")]
        public bool SetStartUpProject {
            get { return Properties.Settings.Default.SetStartUpProject; }
            set { Properties.Settings.Default.SetStartUpProject = value; }
        }

        [Category("Startup")]
        [Description("Set default command line arguments")]
        public string StartArguments {
            get { return Properties.Settings.Default.StartArguments; }
            set { Properties.Settings.Default.StartArguments = value; }
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            Properties.Settings.Default.Save();
            base.OnApply(e);
        }
    }
}
