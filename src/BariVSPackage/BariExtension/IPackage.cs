using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.OLE.Interop;

namespace KOTEM.BariVSPackage.BariExtension
{
    interface IPackage: IOleCommandTarget
    {
        bool IsWatcherBusy { get; }
    }
}
