using System;
using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace KOTEM.BariVSPackage.BariExtension.Option
{
    public class BariOptionsDialog : DialogPage
    {
        protected override void OnApply(PageApplyEventArgs e)
        {
            Properties.Settings.Default.Save();
            base.OnApply(e);
        }
    }
}
