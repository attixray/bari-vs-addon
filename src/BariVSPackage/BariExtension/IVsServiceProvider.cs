using EnvDTE;

namespace KOTEM.BariVSPackage.BariExtension
{
    public interface IVsServiceProvider
    {
        DTE GetDte();
        T GetService<T>();
        Project GetProject(string name);
    }
}
