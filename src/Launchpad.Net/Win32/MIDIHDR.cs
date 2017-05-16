using System;
using System.Runtime.InteropServices;

namespace Launchpad.Win32
{
    //https://msdn.microsoft.com/en-us/library/vs/alm/dd798449(v=vs.85).aspx
    [StructLayout(LayoutKind.Sequential)]
    internal struct MIDIHDR
    {
        public static readonly uint Size = (uint)Marshal.SizeOf(typeof(MIDIHDR));

        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public uint dwUser;
        public uint dwFlags;
        public IntPtr lpNext;
        public uint reserved;
        public uint dwOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] dwReserverd;
    }
}
