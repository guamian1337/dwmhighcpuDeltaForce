using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VerifyPatch
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
            string outPath = Path.Combine(Environment.CurrentDirectory, "verify_patch.txt");

            IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
            if (hProc == IntPtr.Zero) { Console.WriteLine("OpenProcess failed: " + Marshal.GetLastWin32Error()); return; }

            // 4 个 RWX 区域
            long[] bases = { 0x17B82030000, 0x17B82060000, 0x17B82080000, 0x17B820D0000 };
            long[] sizes = { 0x1000, 0x1000, 0x43000, 0x4000 };

            // 特征码: sub edx,1; je+5; cmp edx,1
            byte[] pat = { 0x83, 0xEA, 0x01, 0x74, 0x05, 0x83, 0xFA, 0x01 };
            const int PATCH_OFFSET = 8;

            sb.AppendLine("Verify patch in dwm PID " + pid);
            sb.AppendLine();

            for (int i = 0; i < bases.Length; i++)
            {
                byte[] buf = new byte[sizes[i]];
                IntPtr read;
                if (!ReadProcessMemory(hProc, new IntPtr(bases[i]), buf, new IntPtr(sizes[i]), out read))
                {
                    sb.AppendLine("Read failed at 0x" + bases[i].ToString("X") + " err=" + Marshal.GetLastWin32Error());
                    continue;
                }

                int matchCount = 0;
                for (int idx = 0; idx + pat.Length + 2 <= buf.Length; idx++)
                {
                    bool m = true;
                    for (int j = 0; j < pat.Length; j++)
                        if (buf[idx + j] != pat[j]) { m = false; break; }
                    if (!m) continue;

                    matchCount++;
                    long jneAddr = bases[i] + idx + PATCH_OFFSET;
                    byte b0 = buf[idx + PATCH_OFFSET];
                    byte b1 = buf[idx + PATCH_OFFSET + 1];
                    string state = (b0 == 0x90 && b1 == 0x90) ? "PATCHED (90 90)" :
                                   (b0 == 0x75 && b1 == 0xEB) ? "ORIGINAL (75 EB)" :
                                   "OTHER (" + b0.ToString("X2") + " " + b1.ToString("X2") + ")";
                    sb.AppendLine("Pattern @ 0x" + (bases[i] + idx).ToString("X") +
                        "  jne@0x" + jneAddr.ToString("X") + "  -> " + state);
                }
                sb.AppendLine("Region 0x" + bases[i].ToString("X") + ": " + matchCount + " pattern match(es)");
                sb.AppendLine();
            }

            File.WriteAllText(outPath, sb.ToString());
            Console.WriteLine("Done. See " + outPath);
        }
    }
}