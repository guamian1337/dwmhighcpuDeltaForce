using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DumpJit
{
    class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
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

        const uint PROCESS_ALL_ACCESS = 0x1F0FFF;

        static void EnableDebugPrivilege()
        {
            IntPtr hToken;
            if (OpenProcessToken(GetCurrentProcess(), 0x0028, out hToken))
            {
                long luid;
                if (LookupPrivilegeValue(null, "SeDebugPrivilege", out luid))
                {
                    TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                    tp.PrivilegeCount = 1; tp.Luid = luid; tp.Attributes = 0x2;
                    AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }
        }

        // 搜索特征码变体
        static void ScanPatterns(byte[] buf, long baseAddr, StringBuilder sb)
        {
            // 原特征码: sub edx,1; je +5; cmp edx,1
            byte[] p1 = { 0x83, 0xEA, 0x01, 0x74, 0x05, 0x83, 0xFA, 0x01 };
            // 变体: sub edx,2; je; sub edx,1; je; cmp edx,1
            byte[] p2 = { 0x83, 0xEA, 0x02, 0x74, 0x05, 0x83, 0xEA, 0x01, 0x74, 0x05, 0x83, 0xFA, 0x01 };
            // 变体: cmp edx,1; jne
            byte[] p3 = { 0x83, 0xFA, 0x01, 0x75 };
            // 变体: sub edx,1; je; cmp edx,1; jne
            byte[] p4 = { 0x83, 0xEA, 0x01, 0x74, 0x05, 0x83, 0xFA, 0x01, 0x75 };

            sb.AppendLine("--- Pattern scan in region base=0x" + baseAddr.ToString("X") + " size=0x" + buf.Length.ToString("X") + " ---");
            ScanOne(buf, baseAddr, p1, "P1(sub edx,1;je+5;cmp edx,1)", sb);
            ScanOne(buf, baseAddr, p2, "P2(sub edx,2;je;sub edx,1;je;cmp edx,1)", sb);
            ScanOne(buf, baseAddr, p3, "P3(cmp edx,1;jne)", sb);
            ScanOne(buf, baseAddr, p4, "P4(sub edx,1;je;cmp edx,1;jne)", sb);
        }

        static void ScanOne(byte[] buf, long baseAddr, byte[] pat, string name, StringBuilder sb)
        {
            int count = 0;
            for (int i = 0; i + pat.Length <= buf.Length; i++)
            {
                bool m = true;
                for (int j = 0; j < pat.Length; j++)
                    if (buf[i + j] != pat[j]) { m = false; break; }
                if (m)
                {
                    count++;
                    if (count <= 5)
                        sb.AppendLine("  " + name + " @ 0x" + (baseAddr + i).ToString("X"));
                }
            }
            sb.AppendLine("  " + name + ": total " + count + " matches");
        }

        static void Main(string[] args)
        {
            EnableDebugPrivilege();
            uint pid = 0;
            if (args.Length > 0) pid = uint.Parse(args[0]);
            else
            {
                var procs = System.Diagnostics.Process.GetProcessesByName("dwm");
                if (procs.Length == 0) { Console.WriteLine("dwm not found"); return; }
                pid = (uint)procs[0].Id;
            }

            StringBuilder sb = new StringBuilder();
            string outPath = Path.Combine(Environment.CurrentDirectory, "dump_jit.txt");

            IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
            if (hProc == IntPtr.Zero) { Console.WriteLine("OpenProcess failed: " + Marshal.GetLastWin32Error()); return; }

            // 4 个 RWX 区域（从 debug_regions.txt 得知）
            long[] bases = { 0x17B82030000, 0x17B82060000, 0x17B82080000, 0x17B820D0000 };
            long[] sizes = { 0x1000, 0x1000, 0x43000, 0x4000 };

            for (int i = 0; i < bases.Length; i++)
            {
                byte[] buf = new byte[sizes[i]];
                IntPtr read;
                if (!ReadProcessMemory(hProc, new IntPtr(bases[i]), buf, new IntPtr(sizes[i]), out read))
                {
                    sb.AppendLine("Read failed at 0x" + bases[i].ToString("X") + " err=" + Marshal.GetLastWin32Error());
                    continue;
                }
                ScanPatterns(buf, bases[i], sb);
            }

            File.WriteAllText(outPath, sb.ToString());
            Console.WriteLine("Done. See " + outPath);
        }
    }
}