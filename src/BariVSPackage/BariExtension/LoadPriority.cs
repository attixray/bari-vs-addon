using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KOTEM.BariVSPackage.BariExtension
{
    public enum LoadPriority
    {
        DemandLoad,
        BackgroundLoad,
        LoadIfNeeded,
        ExplicitLoadOnly,
    }
}
