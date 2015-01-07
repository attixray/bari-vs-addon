// Guids.cs
// MUST match guids.h
using System;

namespace KOTEM.BariVSPackage
{
    static class GuidList
    {
        public const string guidBariVSPackagePkgString = "102d89df-1d64-4843-a75d-3a67bf3763a2";
        public const string guidBariVSPackageCmdSetString = "32515e1e-d81c-4ca3-8397-0c1d336a4398";

        public static readonly Guid guidBariVSPackageCmdSet = new Guid(guidBariVSPackageCmdSetString);
    };
}