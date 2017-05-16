using System.Runtime.InteropServices;

namespace Launchpad.Win32
{
    //https://msdn.microsoft.com/en-us/library/vs/alm/dd798451(v=vs.85).aspx
    [StructLayout(LayoutKind.Sequential)]
    internal struct MIDIINCAPS
    {
        public static readonly uint Size = (uint)Marshal.SizeOf(typeof(MIDIINCAPS));

        public ushort wMid;
        public ushort wPid;
        public ushort vDriverVersionMajor;
        public ushort vDriverVersionMinor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwSupport;
    }
}
