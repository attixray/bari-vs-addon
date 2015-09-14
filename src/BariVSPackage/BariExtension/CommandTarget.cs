using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using IServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace KOTEM.BariVSPackage.BariExtension
{
    class CommandTarget : IOleCommandTarget
    {
        private readonly IOleCommandTarget baseImpl;
        private readonly Commands commands;
        private readonly IVsServiceProvider provider;

        public CommandTarget(IOleCommandTarget baseImpl, Commands commands, IVsServiceProvider provider)
        {
            this.baseImpl = baseImpl;
            this.commands = commands;
            this.provider = provider;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {

            var actionMap = new Dictionary<VSConstants.VSStd97CmdID, Action>
                                {
                                    {VSConstants.VSStd97CmdID.BuildSln, commands.ExecuteBariBuild},
                                    {VSConstants.VSStd97CmdID.RebuildSln, commands.ExecuteBariRebuild},
                                    {VSConstants.VSStd97CmdID.CleanSln, commands.ExecuteBariClean},
                                    {VSConstants.VSStd97CmdID.StartNoDebug, commands.ExecuteStartWithoutDebugger},
                                  //  {VSConstants.VSStd97CmdID.Start, commands.ExecuteStartWithDebugger},
                                    {VSConstants.VSStd97CmdID.CancelBuild, commands.CancelAnyPreviousBariAction},
                                    {VSConstants.VSStd97CmdID.Stop, commands.StopDebugger},
                                    {VSConstants.VSStd97CmdID.BuildSel, commands.ExecuteBariBuild},
                                    {VSConstants.VSStd97CmdID.RebuildSel, commands.ExecuteBariRebuild}
                                };

            var allowedWhenDebugging = new HashSet<VSConstants.VSStd97CmdID>
                                            {
                                                VSConstants.VSStd97CmdID.Stop,
                                                VSConstants.VSStd97CmdID.Start  // this means 'Continue' when debugging...
                                            };
            if (pguidCmdGroup == VSConstants.GUID_VSStandardCommandSet97)
            {
                var vsStd97CmdID = ToVSStd97CmdID(nCmdID);
                if (vsStd97CmdID.HasValue)
                {
                    var solutionInfo = new SolutionInfo(provider.GetDte());
                    if (solutionInfo.IsBariSolution)
                    {
                        if (vsStd97CmdID != VSConstants.VSStd97CmdID.SolutionCfg)
                        {
                            Debug.WriteLine("Not slncfg: " + vsStd97CmdID.ToString());
                        }

                        if (commands.IsDebugging())
                        {
                            return baseImpl.Exec(pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
                        }
                        else
                        {
                            Action action;
                            if (actionMap.TryGetValue(vsStd97CmdID.Value, out action))
                            {
                                var dte = provider.GetDte();
                                dte.ExecuteCommand("File.SaveAll");

                                action();
                                return VSConstants.S_OK;

                            }
                            if (vsStd97CmdID.Value == VSConstants.VSStd97CmdID.Start)
                            {
                                if (normalStart > 0)
                                {
                                    normalStart--;
                                    return baseImpl.Exec(pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
                                }
                                else
                                {
                                    var dte = provider.GetDte();
                                    dte.ExecuteCommand("File.SaveAll");
                                    commands.BuildIfNeeded((g) =>
                                    {
                                        normalStart = 2;
                                        dte.ExecuteCommand("Debug.Start");
                                        return VSConstants.S_OK;

                                    }, pguidCmdGroup);
                                    return VSConstants.S_OK;
                                }
                            }
                        }
                    }
                }
                return baseImpl.Exec(pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
            }
            return baseImpl.Exec(pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        private static int normalStart;
        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            return baseImpl.QueryStatus(pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        private static VSConstants.VSStd97CmdID? ToVSStd97CmdID(uint nCmdID)
        {
            if (nCmdID > Int32.MaxValue)
            {
                return null;
            }
            var iCmdId = (int)nCmdID;
            if (!typeof(VSConstants.VSStd97CmdID).IsEnumDefined(iCmdId))
            {
                return null;
            }
            var cmdID = (VSConstants.VSStd97CmdID)iCmdId;
            return cmdID;
        }
    }
}
