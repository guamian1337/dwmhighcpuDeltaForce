using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DebugRegions
{
    class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION info, IntPtr len);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool OpenProcessToken(IntPtr h, uint dwDesiredAccess, out IntPtr hToken);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out long lpLuid);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr hToken, bool d, ref TOKEN_PRIVILEGES n, int len, IntPtr p1, IntPtr p2);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetCurrentProcess();

        [StructLayout(LayoutKind.Sequential)]
        struct TOKEN_PRIVILEGES
        {
            public int PrivilegeCount;
            public long Luid;
            public int Attributes;
        }

        const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
        const uint MEM_COMMIT = 0x1000;
        const uint MEM_PRIVATE = 0x20000;
        const uint MEM_MAPPED = 0x40000;
        const uint MEM_IMAGE = 0x1000000;
        const uint PAGE_EXECUTE_READWRITE = 0x40;
        const uint PAGE_GUARD = 0x100;

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        static void EnableDebugPrivilege()
        {
            IntPtr hToken;
            if (OpenProcessToken(GetCurrentProcess(), 0x0028, out hToken))
            {
                long luid;
                if (LookupPrivilegeValue(null, "SeDebugPrivilege", out luid))
                {
                    TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                    tp.PrivilegeCount = 1;
                    tp.Luid = luid;
                    tp.Attributes = 0x2;
                    AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }
        }

        static string ProtName(uint p)
        {
            uint prot = p & 0xFF;
            string name;
            switch (prot)
            {
                case 0x01: name = "NOACCESS"; break;
                case 0x02: name = "READONLY"; break;
                case 0x04: name = "READWRITE"; break;
                case 0x08: name = "WRITECOPY"; break;
                case 0x10: name = "EXECUTE"; break;
                case 0x20: name = "EXECUTE_READ"; break;
                case 0x40: name = "EXECUTE_READWRITE"; break;
                case 0x80: name = "EXECUTE_WRITECOPY"; break;
                default: name = "0x" + prot.ToString("X"); break;
            }
            if ((p & PAGE_GUARD) != 0) name += "|GUARD";
            return name;
        }

        static string TypeName(uint t)
        {
            switch (t)
            {
                case MEM_PRIVATE: return "PRIVATE";
                case MEM_MAPPED: return "MAPPED";
                case MEM_IMAGE: return "IMAGE";
                default: return "0x" + t.ToString("X");
            }
        }

        static void Main(string[] args)
        {
            EnableDebugPrivilege();

            uint pid = 0;
            if (args.Length > 0)
            {
                pid = uint.Parse(args[0]);
            }
            else
            {
                var procs = System.Diagnostics.Process.GetProcessesByName("dwm");
                if (procs.Length == 0) { Console.WriteLine("dwm.exe not found"); return; }
                pid = (uint)procs[0].Id;
            }

            StringBuilder sb = new StringBuilder();
            string outPath = Path.Combine(Environment.CurrentDirectory, "debug_regions.txt");

            sb.AppendLine("Output: " + outPath);
            sb.AppendLine("CWD: " + Environment.CurrentDirectory);
            sb.AppendLine("IsAdmin: " + new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
            sb.AppendLine("Target PID: " + pid);
            sb.AppendLine("MBI size: " + Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION)) + " bytes");
            sb.AppendLine();

            IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
            if (hProc == IntPtr.Zero)
            {
                sb.AppendLine("OpenProcess failed: " + Marshal.GetLastWin32Error());
                File.WriteAllText(outPath, sb.ToString());
                Console.WriteLine("OpenProcess failed: " + Marshal.GetLastWin32Error());
                return;
            }

            int count = 0;
            int execCount = 0;
            int rwxPrivate = 0;
            long addr = 0;

            while (true)
            {
                MEMORY_BASIC_INFORMATION mbi;
                int ret = VirtualQueryEx(hProc, new IntPtr(addr), out mbi,
                    new IntPtr(Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))));
                if (ret == 0)
                {
                    sb.AppendLine("VirtualQueryEx returned 0 at addr=0x" + addr.ToString("X") + " (error=" + Marshal.GetLastWin32Error() + ")");
                    break;
                }

                count++;
                bool isExec = (mbi.State == MEM_COMMIT) && ((mbi.Protect & 0xF0) != 0); // 含 EXECUTE 位
                bool isRwx = (mbi.Protect & 0xFF) == PAGE_EXECUTE_READWRITE;
                bool isPriv = (mbi.Type == MEM_PRIVATE);
                bool isRwxPrivate = (mbi.State == MEM_COMMIT && isRwx && isPriv);

                if (isExec || isRwxPrivate)
                {
                    sb.AppendLine("Base=0x" + mbi.BaseAddress.ToInt64().ToString("X") +
                        " Size=0x" + mbi.RegionSize.ToInt64().ToString("X") +
                        " State=0x" + mbi.State.ToString("X") +
                        " Prot=" + ProtName(mbi.Protect) + " (0x" + mbi.Protect.ToString("X") + ")" +
                        " Type=" + TypeName(mbi.Type) +
                        (isRwxPrivate ? " *** RWX+PRIVATE ***" : ""));
                }

                if (isExec) execCount++;
                if (isRwxPrivate) rwxPrivate++;

                long next = mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
                if (next <= addr)
                {
                    sb.AppendLine("Stuck at addr=0x" + addr.ToString("X") + " next=0x" + next.ToString("X"));
                    break;
                }
                addr = next;
            }

            sb.AppendLine();
            sb.AppendLine("Total regions: " + count);
            sb.AppendLine("Executable regions: " + execCount);
            sb.AppendLine("RWX+Private regions: " + rwxPrivate);
            File.WriteAllText(outPath, sb.ToString());
            Console.WriteLine("Done. See " + outPath);
        }
    }
}