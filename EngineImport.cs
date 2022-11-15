using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace TQArchive_Wrapper
{
    public static class EngineImport
    {
        [DllImport("E:\\SteamLibrary\\steamapps\\common\\Titan Quest Anniversary Edition\\Engine", EntryPoint = "??0Archive@GAME@@QAE@XZ")]
        public static extern IntPtr Archive();

        [DllImport("E:\\SteamLibrary\\steamapps\\common\\Titan Quest Anniversary Edition\\Engine", EntryPoint = "?ArchiveFileMode@Archive@GAME@@QBE?AW4FileMode@12@XZ", CallingConvention = CallingConvention.ThisCall)]
        public static extern IntPtr GetArchiveFileMode(this IntPtr archive);

        [DllImport("E:\\SteamLibrary\\steamapps\\common\\Titan Quest Anniversary Edition\\Engine", EntryPoint = "??1Archive@GAME@@QAE@XZ", CallingConvention = CallingConvention.ThisCall)]
        public static extern void DisposeArchive(IntPtr archive);

        [DllImport("E:\\SteamLibrary\\steamapps\\common\\Titan Quest Anniversary Edition\\Engine", EntryPoint = "?Close@Archive@GAME@@QAEX_N@Z", CallingConvention = CallingConvention.ThisCall)]
        public static extern void CloseArchive(IntPtr archive, bool whatever = false);

        [DllImport("E:\\SteamLibrary\\steamapps\\common\\Titan Quest Anniversary Edition\\Engine", EntryPoint = "?Open@Archive@GAME@@QAE_NPBDW4FileMode@12@_N@Z", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.ThisCall)]
        public static extern bool OpenArchive(IntPtr archive, string path, int fileMode = 1, bool whatever = false);

        [DllImport("E:\\SteamLibrary\\steamapps\\common\\Titan Quest Anniversary Edition\\Engine", EntryPoint = "?AddFileFromDisk@Archive@GAME@@QAEHPBDH@Z", CallingConvention = CallingConvention.ThisCall)]
        public static extern void AddFileToArchive(IntPtr archive, string path, int compression = 0);
    }
}
