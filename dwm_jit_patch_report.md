# dwm.exe 死循环 CPU 占用修复报告

## 一、问题概述

dwm.exe（桌面窗口管理器）出现异常高 CPU 占用。经诊断，根因是**第三方软件注入到 dwm.exe 的 shellcode 中存在死循环**。

该 shellcode 是一个显卡厂商检测状态机，其 switch-case 只处理 AMD(2)/NVIDIA(3/4) 三种返回值，**缺少 Intel/其他显卡(返回值 1) 的分支**。当检测到 Intel 显卡时，代码在 `jne` 处跳回循环入口形成紧密空循环，持续消耗 CPU。

## 二、修复原理

死循环的关键指令是：

```asm
0x...6934: 83 EA 01    sub edx, 1
0x...6937: 74 05       je  +5
0x...6939: 83 FA 01    cmp edx, 1
0x...693C: 75 EB       jne 0x...6929   ← 死循环跳转（跳回循环入口）
```

**修复方式**：将 `jne`（`75 EB`）改为 `nop; nop`（`90 90`），使代码顺序执行到 case 3/4 路径（含 Sleep/WaitForSingleObject 等待），从而消除空转。

## 三、修复过程中发现的两个 Bug

### Bug 1：`MEMORY_BASIC_INFORMATION` 结构体 x64 布局错误

**现象**：patcher 报告 `Found 0 RWX+Private regions`，但实际存在 4 个。

**原因**：x64 下该结构体字段有对齐 padding，原定义把 `RegionSize` 放在错误偏移，导致 `State/Protect/Type` 全部错位读取，`State == MEM_COMMIT` 永远不成立，扫描恒为空。

**修复**：补上 x64 对齐字段。

```csharp
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
```

### Bug 2：`PATCH_OFFSET` 偏移量错误

**现象**：修复 Bug 1 后能找到 4 个区域，但仍报 `Pattern not found`。

**原因**：特征码是 8 字节（`sub edx,1; je+5; cmp edx,1`），`jne` 指令在特征码**之后第 8 个字节**（`+8`），原代码设成 `+7`，导致字节校验检查的是 `cmp edx,1` 的最后一个字节（`0x01`）而非 `jne`（`0x75`），校验永远失败。

**修复**：`PATCH_OFFSET` 从 `7` 改为 `8`。

## 四、修复后的验证

验证工具扫描全部 4 个 RWX 区域，确认：

| 区域基址 | 特征码匹配数 | 状态 |
|---------|------------|------|
| `0x17B82030000` | 0 | 未改动 |
| `0x17B82060000` | 0 | 未改动 |
| `0x17B82080000` | 1 | `0x17B8208693C` 已修补为 `90 90` |
| `0x17B820D0000` | 0 | 未改动 |

**结论**：整个 dwm 进程中该死循环特征码仅出现一次，只修改了那一处 `jne` 指令，其余区域零改动，不会误改其他位置。

## 五、编译指令

使用 .NET Framework 自带的 C# 编译器（`csc` 不在 PATH 中，需用完整路径）：

```powershell
# 编译修补工具
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /out:DwmJitPatcher.exe DwmJitPatcher.cs

# 编译诊断工具（枚举内存区域）
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /out:DebugRegions.exe DebugRegions.cs

# 编译特征码扫描工具
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /out:DumpJit.exe DumpJit.cs

# 编译修补验证工具
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /out:VerifyPatch.exe VerifyPatch.cs
```

## 六、运行指令

dwm.exe 是系统进程，**必须以管理员权限运行**（使用 `sudo`）：

```powershell
# 1. 执行修补（核心操作）
sudo .\DwmJitPatcher.exe

# 2. 枚举 dwm 内存区域（诊断用，结果写入 debug_regions.txt）
sudo .\DebugRegions.exe

# 3. 扫描特征码（诊断用，结果写入 dump_jit.txt）
sudo .\DumpJit.exe

# 4. 验证修补结果（结果写入 verify_patch.txt）
sudo .\VerifyPatch.exe
```

## 七、修补工具核心代码

```csharp
// ====== 特征码 ======
// sub edx,1; je +5; cmp edx,1 — 纯指令操作码，无地址依赖
static readonly byte[] PATTERN = { 0x83, 0xEA, 0x01, 0x74, 0x05, 0x83, 0xFA, 0x01 };
const int PATCH_OFFSET = 8;                          // 特征码后 +8 = jne
static readonly byte[] EXPECT = { 0x75, 0xEB };      // jne (原始)
static readonly byte[] PATCH  = { 0x90, 0x90 };     // nop;nop (修补)
```

## 八、注意事项

1. **dwm 重启后需重新修补**：注销/登录、切换用户、系统更新都会重启 dwm，注入的 shellcode 会重新注入，地址和内容可能变化，需重新运行 `DwmJitPatcher.exe`。

2. **这是临时缓解**：根本解决方法是定位并卸载/更新注入该 shellcode 的第三方软件（特征：检查 `amdx6x4.dll`、`nvwgf2umx.dll`、`nvspcap44.dll`，内部标识 `AsukaNvapcap`，NVIDIA NVI2 GUID `D50BA131-BBCD-42EA-AE03-540F4767A65A`）。

3. **特征码稳定性**：若注入者更新 shellcode 导致特征码变化，需重新分析并更新 `PATTERN`。
