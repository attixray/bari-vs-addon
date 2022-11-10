using System;
using System.ComponentModel;
using Microsoft.VisualStudio.Shell;
using System.Runtime.InteropServices;

namespace KOTEM.BariVSPackage.BariExtension.Option
{
    [ComVisible(true)]
    public class BariOptionsDialog : DialogPage
    {
        protected override void OnApply(PageApplyEventArgs e)
        {
            Properties.Settings.Default.Save();
            base.OnApply(e);
        }
    }
}
