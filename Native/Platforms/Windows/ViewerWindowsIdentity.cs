using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PerformanceViewer.Native
{
    public static class ViewerWindowsIdentity
    {
        private static string Id(bool source) => source
            ? "OpenAgency.PerformanceViewer.Source"
            : "OpenAgency.PerformanceViewer.Package";

        public static void IdentifyProcess(bool source) =>
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(Id(source)));

        public static void IdentifyShortcut(string path, bool source)
        {
            var shellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))
                ?? throw new InvalidOperationException("The Windows shortcut COM class is unavailable.");
            var instance = Activator.CreateInstance(shellLinkType)
                ?? throw new InvalidOperationException("Windows did not create a shortcut object.");
            var value = new PropertyValue { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(Id(source)) };
            try
            {
                var link = (IPersistFile)instance;
                link.Load(path, 2);
                var store = (IPropertyStore)instance;
                var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                store.SetValue(ref key, ref value);
                store.Commit();
                link.Save(path, true);
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.Pointer);
                Marshal.FinalReleaseComObject(instance);
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey { public Guid Format; public uint Id; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyValue
        {
            public ushort Type, Reserved1, Reserved2, Reserved3;
            public IntPtr Pointer, Reserved;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropertyValue value);
            void SetValue(ref PropertyKey key, ref PropertyValue value);
            void Commit();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    }
}
