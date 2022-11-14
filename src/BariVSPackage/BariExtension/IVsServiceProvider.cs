using EnvDTE;
using Microsoft.VisualStudio.Shell.Interop;

namespace KOTEM.BariVSPackage.BariExtension
{
    public interface IVsServiceProvider
    {
        DTE GetDte();
        T GetService<T>();
        Project GetProject(string name);
    }
}
