using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DwmJitPatcher
{
    class Program
    {
        // ====== 特征码 ======
        // sub edx,1; je +5; cmp edx,1 — 纯指令操作码，无地址依赖
        static readonly byte[] PATTERN = { 0x83, 0xEA, 0x01, 0x74, 0x05, 0x83, 0xFA, 0x01 };
        const int PATCH_OFFSET = 8;                          // 特征码后 +8 = jne
        static readonly byte[] EXPECT = { 0x75, 0xEB };      // jne (原始)
        static readonly byte[] PATCH  = { 0x90, 0x90 };     // nop;nop (修补)

        // ====== Win32 API ======
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint newProt, out uint oldProt);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION info, IntPtr len);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool Thread32First(IntPtr snap, ref THREADENTRY32 te);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool Thread32Next(IntPtr snap, ref THREADENTRY32 te);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint SuspendThread(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint ResumeThread(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr hToken);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool LookupPrivilegeValue(string sys, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr hToken, bool d, ref TOKEN_PRIVILEGES n, int len, IntPtr p1, IntPtr p2);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetCurrentProcess();

        [StructLayout(LayoutKind.Sequential)]
        struct TOKEN_PRIVILEGES { public int PrivilegeCount; public long Luid; public int Attributes; }

        static void EnableDebugPrivilege()
        {
            IntPtr hToken;
            if (OpenProcessToken(GetCurrentProcess(), 0x0028, out hToken))
            {
                long luid;
                if (LookupPrivilegeValue(null, "SeDebugPrivilege", out luid))
                {
                    TOKEN_PRIVILEGES tp;
                    tp.PrivilegeCount = 1;
                    tp.Luid = luid;
                    tp.Attributes = 0x2;
                    AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }
        }

        const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
        const uint TH32CS_SNAPTHREAD = 0x4;
        const uint THREAD_SUSPEND_RESUME = 0x2;
        const uint MEM_COMMIT = 0x1000;
        const uint PAGE_EXECUTE_READWRITE = 0x40;
        const uint MEM_PRIVATE = 0x20000;

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public uint Alignment1;   // x64 padding
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint Alignment2;   // x64 padding
        }

        [StructLayout(LayoutKind.Sequential)]
        struct THREADENTRY32
        {
            public uint dwSize, cntUsage, th32ThreadID, th32OwnerProcessID;
            public int tpBasePri, tpDeltaPri;
            public uint dwFlags;
        }

        static void Main()
        {
            // 提升 SeDebugPrivilege（dwm.exe 是系统进程，需要此权限）
            EnableDebugPrivilege();

            // 查找 dwm.exe
            Process[] procs = Process.GetProcessesByName("dwm");
            if (procs.Length == 0) { Console.WriteLine("[!] dwm.exe not found"); return; }

            uint pid = (uint)procs[0].Id;
            Console.WriteLine("[*] Target: dwm.exe (PID " + pid + ")");

            IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
            if (hProc == IntPtr.Zero)
            {
                Console.WriteLine("[!] OpenProcess failed (error " + Marshal.GetLastWin32Error() + ")");
                Console.WriteLine("[!] Run as Administrator");
                return;
            }

            // 暂停所有线程
            Console.WriteLine("[*] Suspending threads...");
            var threads = SuspendThreads(pid);

            try
            {
                // 枚举可执行私有内存
                var regions = FindExecPrivateRegions(hProc);
                Console.WriteLine("[*] Found " + regions.Count + " RWX+Private regions");

                bool patched = false;
                for (int r = 0; r < regions.Count; r++)
                {
                    IntPtr baseAddr = regions[r].Key;
                    IntPtr size = regions[r].Value;

                    byte[] buf = new byte[size.ToInt64()];
                    IntPtr read;
                    if (!ReadProcessMemory(hProc, baseAddr, buf, size, out read))
                        continue;

                    int idx = FindPattern(buf);
                    if (idx < 0) continue;

                    long patchAddr = baseAddr.ToInt64() + idx + PATCH_OFFSET;
                    IntPtr target = new IntPtr(patchAddr);

                    // 验证原始字节
                    byte[] verify = new byte[2];
                    ReadProcessMemory(hProc, target, verify, new IntPtr(2), out read);
                    if (verify[0] != EXPECT[0] || verify[1] != EXPECT[1])
                    {
                        Console.WriteLine("[!] Byte mismatch: expected " + EXPECT[0].ToString("X2") + EXPECT[1].ToString("X2") + ", got " + verify[0].ToString("X2") + verify[1].ToString("X2"));
                        continue;
                    }

                    // 写入修补
                    uint oldProt;
                    VirtualProtectEx(hProc, target, new IntPtr(2), PAGE_EXECUTE_READWRITE, out oldProt);
                    WriteProcessMemory(hProc, target, PATCH, new IntPtr(2), out read);
                    VirtualProtectEx(hProc, target, new IntPtr(2), oldProt, out oldProt);

                    Console.WriteLine("[+] Patched at 0x" + patchAddr.ToString("X") + ": 75 EB -> 90 90");
                    patched = true;
                    break;
                }

                if (!patched)
                    Console.WriteLine("[-] Pattern not found. Shellcode may have changed.");
            }
            finally
            {
                Console.WriteLine("[*] Resuming threads...");
                foreach (var hT in threads) { ResumeThread(hT); CloseHandle(hT); }
                CloseHandle(hProc);
            }
            Console.WriteLine("[*] Done.");
        }

        static List<IntPtr> SuspendThreads(uint pid)
        {
            var threads = new List<IntPtr>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            if (snap == IntPtr.Zero) return threads;

            var te = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(THREADENTRY32)) };
            if (Thread32First(snap, ref te))
            {
                do
                {
                    if (te.th32OwnerProcessID == pid)
                    {
                        IntPtr hT = OpenThread(THREAD_SUSPEND_RESUME, false, te.th32ThreadID);
                        if (hT != IntPtr.Zero) { SuspendThread(hT); threads.Add(hT); }
                    }
                } while (Thread32Next(snap, ref te));
            }
            CloseHandle(snap);
            return threads;
        }

        static List<KeyValuePair<IntPtr, IntPtr>> FindExecPrivateRegions(IntPtr hProc)
        {
            var regions = new List<KeyValuePair<IntPtr, IntPtr>>();
            long addr = 0;
            while (true)
            {
                MEMORY_BASIC_INFORMATION mbi;
                int ret = VirtualQueryEx(hProc, new IntPtr(addr), out mbi,
                    new IntPtr(Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))));
                if (ret == 0) break;

                if (mbi.State == MEM_COMMIT &&
                    mbi.Protect == PAGE_EXECUTE_READWRITE &&
                    mbi.Type == MEM_PRIVATE)
                {
                    regions.Add(new KeyValuePair<IntPtr, IntPtr>(mbi.BaseAddress, mbi.RegionSize));
                }

                addr = mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
                if (addr <= 0 || addr >= 0x7FFFFFFFFFFF) break;
            }
            return regions;
        }

        static int FindPattern(byte[] buf)
        {
            for (int i = 0; i + PATTERN.Length + 2 <= buf.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < PATTERN.Length; j++)
                {
                    if (buf[i + j] != PATTERN[j]) { match = false; break; }
                }
                if (match && buf[i + PATCH_OFFSET] == EXPECT[0] && buf[i + PATCH_OFFSET + 1] == EXPECT[1])
                    return i;
            }
            return -1;
        }
    }
}
