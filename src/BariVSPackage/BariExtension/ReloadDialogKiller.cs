using System;
using System.Runtime.InteropServices;
using System.Text;

namespace KOTEM.BariVSPackage.BariExtension
{
    public class ReloadDialogKiller : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct CWPRETSTRUCT
        {
            public IntPtr lResult;
            public IntPtr lParam;
            public IntPtr wParam;
            public uint message;
            public IntPtr hwnd;
        };

        #pragma warning disable 0618

        private HookProc messageHookProcedure;
        private IntPtr hHook;

        private const int WM_INITDIALOG = 0x0110;
        private const int WH_CALLWNDPROCRET = 12;

        private delegate int HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowsHookEx", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", EntryPoint = "UnhookWindowsHookEx", SetLastError = true)]
        private static extern IntPtr UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern int CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxLength);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseWindow(IntPtr hWnd);

        public bool IsEnabled { get; set; }

        public ReloadDialogKiller()
        {
            messageHookProcedure = MessageHookProc;
            hHook = SetWindowsHookEx(WH_CALLWNDPROCRET, messageHookProcedure, IntPtr.Zero, (uint)AppDomain.GetCurrentThreadId());
        }

        public int MessageHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return CallNextHookEx(hHook, nCode, wParam, lParam);

            if (IsEnabled)
            {
                var msg = (CWPRETSTRUCT)Marshal.PtrToStructure(lParam, typeof(CWPRETSTRUCT));

                if (msg.message == WM_INITDIALOG)
                {
                    int nLength = GetWindowTextLength(msg.hwnd);
                    var dialogName = new StringBuilder(nLength);
                    GetWindowText(msg.hwnd, dialogName, dialogName.Capacity);
                    var name = dialogName.ToString().ToLower();
                    if (name.StartsWith("file modification") || name.StartsWith("conflicting file modification"))
                    {
                        DestroyWindow(msg.hwnd);
                        CloseWindow(msg.hwnd);
                    }
                }
            }

            return CallNextHookEx(hHook, nCode, wParam, lParam);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (hHook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(hHook);
                    hHook = IntPtr.Zero;
                    messageHookProcedure = null;
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
