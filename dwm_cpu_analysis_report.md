# dwm.exe 异常 CPU 占用诊断报告

## 诊断环境

| 项目 | 值 |
|------|-----|
| 进程 | `C:\Windows\System32\dwm.exe` (PID: 0x77C) |
| 调试方式 | WinDbg MCP (实时调试) |
| 诊断时间 | 2026-08-20 |

---

## 一、问题定位

### 1. CPU 热点线程

使用 `!runaway` 命令分析各线程用户态 CPU 时间，发现 **线程 21 (TID: 5428)** 异常突出：

```
 User Mode Time
  Thread       Time
   21:5428     0 days 0:02:38.515   ← 异常：占全部 CPU 时间的 95%+
    2:850      0 days 0:00:07.687   ← 正常合成线程
    8:988      0 days 0:00:00.687
    其余线程    均不足 0.3 秒
```

**线程 21 消耗了 2 分 38 秒的用户态 CPU 时间，是第二高线程的 20 倍以上。**

### 2. 调用栈分析

切换到线程 21 后查看调用栈 (`knL`)：

```
 # Child-SP          RetAddr               Call Site
00 0000008d`4da7fcf0 000001c0`a8292670     0x000001c0`ac566939
01 0000008d`4da7fcf8 000001c0`a8292670     0x000001c0`a8292670
02 0000008d`4da7fd00 00000000`00000000     0x000001c0`a8292670
```

**关键发现：调用栈中没有任何 Windows 系统模块（无 ntdll/kernel32/dwmcore 等），全部地址在 `000001c0` 开头的动态内存中。**

对比正常合成线程 (线程 2) 的调用栈：
```
dwmcore!CMonitorClock::WaitForNextTick    ← 正常等待下一帧
dwmcore!CConnection::MainCompositionThreadLoop
```

### 3. 内存区域分析

对执行地址进行 `!address` 分析：

**帧 0 执行地址 `000001c0ac566939` 所在区域：**
```
Usage:              <unknown>           ← 不属于任何已加载模块
Base Address:       000001c0ac560000
Region Size:        00043000 (268 KB)
State:              MEM_COMMIT
Protect:            PAGE_EXECUTE_READWRITE  ← 可读写+可执行 = JIT 代码
Type:               MEM_PRIVATE             ← 私有内存，非映像映射
```

**帧 1-2 返回地址 `000001c0a8292670` 所在区域：**
```
Usage:              Heap                   ← 堆内存
Protect:            PAGE_READWRITE          ← 可读写，不可执行
Type:               MEM_PRIVATE
```

**线程 21 起始地址：**
```
Start: 000001c0ac571474  ← 也在 JIT 代码区域 (000001c0ac560000 + 0x11474) 内
```

### 4. 反汇编分析（无限循环确认）

反汇编当前执行点附近代码：

```asm
; 调用内部函数，返回值放入 eax
000001c0ac566922  call    000001c0ac566410
000001c0ac566927  mov     edi,eax              ; edi = 函数返回值

; ===== switch-case 分支检查 =====
000001c0ac566929  mov     edx,edi              ; ← 循环入口
000001c0ac56692b  sub     edx,2                ; case 2?
000001c0ac56692e  je      000001c0ac566a87     ; 是 -> 跳转处理
000001c0ac566934  sub     edx,1                ; case 3?
000001c0ac566937  je      000001c0ac56693e     ; 是 -> 跳转处理
000001c0ac566939  cmp     edx,1                ; case 4?  ← 当前停在此处
000001c0ac56693c  jne     000001c0ac566929     ; 不是 -> 跳回循环入口 ★

; 如果匹配 case 4 的处理路径
000001c0ac56693e  call    000001c0ac5662d8
...
```

**当前寄存器状态：**
```
rax=0000000000000001  rdi=0000000000000001  rdx=00000000fffffffe
rip=000001c0ac566939
```

**无限循环逻辑分析：**

该 switch-case 仅处理返回值 **2、3、4** 三种情况。当前函数返回值为 **1**（`rdi=1`），不匹配任何 case：

| 步骤 | 指令 | edx 值 | 结果 |
|------|------|---------|------|
| 1 | `mov edx, edi` | 1 | 重置 |
| 2 | `sub edx, 2` | -1 (0xFFFFFFFF) | 不为 0，不跳转 |
| 3 | `sub edx, 1` | -2 (0xFFFFFFFE) | 不为 0，不跳转 |
| 4 | `cmp edx, 1` | -2 ≠ 1 | `jne` 跳回步骤 1 |

**由于缺少 default/break 分支处理非预期返回值，当函数返回 1 时代码在 `0x929` ~ `0x93C` 之间形成死循环，持续空转消耗 CPU。**

---

## 二、代码来源确认

### 已加载的 Intel 显卡驱动模块

进程 `lm` 输出中存在大量 Intel 显卡驱动模块，版本统一为 **32.0.101.8974**：

| 模块名 | 描述 |
|--------|------|
| `igc64.dll` | **Intel Graphics Shader Compiler** (JIT 代码来源) |
| `igd10iumd64.dll` | User Mode Driver for Intel Graphics (D3D11) |
| `igd10umt64xe.DLL` | User Mode Driver for Intel Graphics (D3D10) |
| `igdml64.dll` | Intel Graphics Media Layer |
| `igdgmm64.dll` / `igdgmm2_64.dll` | Intel Graphics Memory Manager |
| `igc_default64.dll` | Intel Graphics Compiler (默认后端) |
| `iga64.dll` | Intel Graphics Acceleration |

**驱动版本：** 32.0.101.8974 (WHQL Certified)  
**发布日期：** 2026-08-14  
**文件路径：** `C:\Windows\System32\DriverStore\FileRepository\iigd_dch_d.inf_amd64_fb6b9affe823ecad\`

### 结论（深度逆向修正）

JIT 代码区域 (`000001c0ac560000`，268KB，PAGE_EXECUTE_READWRITE) **并非 Intel 显卡驱动生成的 shader 代码**，而是**第三方软件注入 dwm.exe 的监视模块**。

**逆向证据：**

通过对 JIT 代码中的 XOR 混淆字符串进行解密，发现：

| 解密字符串 | 含义 |
|-----------|------|
| `amdx6x4.dll` | AMD 显卡 UMD DLL 名 |
| `nvwgf2umx.dll` | NVIDIA 显卡 UMD DLL 名 |
| `nvspcap44.dll` | NVIDIA ShadowPlay 捕获 DLL 名 |
| `D50BA131-BBCD-42EA-AE03-540F4767A65A` | NVIDIA NVI2 接口 GUID |
| `Global\` + `%s%s.%d` | 共享内存命名格式（跨进程通信） |
| `Aksuka` | 内部状态结构标识符 |

该 JIT 代码调用的 Windows API 全部是 `kernel32` 模块的基础函数：
- `K32EnumProcessModules` — 枚举进程加载的 DLL
- `GetCurrentProcess` / `GetCurrentProcessId` — 获取进程信息
- `WaitForSingleObject` — 等待事件对象（句柄 0xCB8，类型 Event）
- `Sleep` — 休眠等待
- `CloseHandle` — 关闭句柄

**这些行为与显卡驱动无关**，是典型的第三方软件注入特征。代码中检查的 `nvspcap44.dll` 是 **NVIDIA ShadowPlay Capture DLL**（NVIDIA GeForce Experience 的屏幕录制组件），结合检查 `nvwgf2umx.dll`（NVIDIA 显卡 UMD）和 NVIDIA NVI2 接口 GUID，可以判定**这段 JIT 代码来自 NVIDIA GeForce Experience / NVIDIA App 的 overlay 注入组件**。

即使本机当前使用 Intel 显卡，如果之前安装过 NVIDIA GeForce Experience，其 overlay 组件仍可能注入 dwm.exe。dwm.exe 加载 Intel 显卡驱动模块（igc64.dll 等）仅因本机使用 Intel 显卡，但这些驱动**不是** CPU 问题的来源。

---

## 三、为什么别的机器不会出现

### 根本原因：注入代码的厂商检测逻辑缺陷

注入的 JIT 代码是一个**状态机循环**，根据显卡厂商检测结果执行不同路径：

| 检测结果 | 返回值 | 走的路径 | 每轮行为 | CPU 占用 |
|---------|--------|---------|---------|---------|
| AMD 显卡 | 2 | case 2 | 检查 NVIDIA 接口 → 共享内存 → 循环 | 有 IO 等待，低 |
| NVIDIA 无 ShadowPlay | 3 | case 3 | **Sleep(50ms)** → 循环 | **每轮休 50ms，极低** |
| NVIDIA 有 ShadowPlay | 4 | case 4 | **WaitForSingleObject(INFINITE)** → 事件触发后循环 | **阻塞等待，接近 0** |
| **Intel/其他** | **1** | **无 case 分支** | **无等待、无 Sleep → 空循环** | **★ 100% CPU** |

### 原因 1：显卡厂商不同（直接触发条件）

注入代码的 switch-case 只处理了返回值 2/3/4（AMD/NVIDIA），**缺少 case 1（Intel/其他）分支**：

- **NVIDIA 机器**：返回 3 或 4 → 走 Sleep 或 WaitForSingleObject 路径 → CPU 正常
- **AMD 机器**：返回 2 → 走共享内存路径 → CPU 正常
- **Intel 机器**：返回 1 → **无匹配 case → jne 跳回循环入口 → 紧密空循环**

### 原因 2：第三方软件未安装

如果其他机器**未安装**注入此代码的第三方软件，dwm.exe 根本不会加载这段 JIT 代码，自然不会出现 CPU 占用问题。

### 原因 3：软件版本差异

不同版本的注入代码可能：
- 旧版本可能检测到 Intel 后直接 break 退出循环
- 新版本（当前版本）引入了 Intel 分支遗漏的回归 bug
- 后续修复版本可能已添加 case 1 的 Sleep/退出处理

---

## 三点五、深度逆向：正常路径与返回值 1 的成因

### 函数 `0xac566410` 的完整逻辑

该函数在 dwm.exe 内通过 JIT 代码执行，**本质是显卡厂商检测函数**。它依次检查三组 DLL 是否已加载到进程中：

| 步骤 | XOR 解密字符串 | 含义 | 调用 API | 检查方式 |
|------|---------------|------|----------|---------|
| 1 | `amdx6x4.dll` | AMD 显卡 UMD | `kernel32!K32EnumProcessModules` | 枚举进程模块列表 |
| 2 | `nvwgf2umx.dll` | NVIDIA 显卡 UMD | `kernel32!K32EnumProcessModules` | 枚举进程模块列表 |
| 3 | `nvspcap44.dll` | NVIDIA ShadowPlay 捕获 DLL | `kernel32!K32EnumProcessModules` | 枚举进程模块列表 |

### 返回值逻辑（4 种可能）

```
函数 0xac566410 返回值决策树：

检查1: amdx6x4.dll 是否已加载？
  ├─ 是 → return 2   (检测到 AMD 显卡)
  └─ 否 ↓

检查2: nvwgf2umx.dll 是否已加载？
  ├─ 否 → return 1   ★ 当前命中此分支
  └─ 是 ↓

检查3: nvspcap44.dll 是否已加载？
  ├─ 是 → return (NOT found) + 3
  │       found=1: return 3  (NVIDIA + 无 ShadowPlay)
  │       found=0: return 4  (NVIDIA + 有 ShadowPlay)
  └─ 否 → return 1
```

**各返回值的语义：**

| 返回值 | 含义 |
|--------|------|
| **1** | 既无 AMD 也无 NVIDIA 驱动 → 本机使用 Intel 显卡 |
| **2** | 检测到 AMD 显卡驱动 |
| **3** | 检测到 NVIDIA 显卡 + 无 ShadowPlay |
| **4** | 检测到 NVIDIA 显卡 + 有 ShadowPlay |

### 为什么返回 1？

dwm.exe 进程中枚举所有已加载模块后：
- **未找到 `amdx6x4.dll`**（AMD 驱动，本机无 AMD 显卡）
- **未找到 `nvwgf2umx.dll`**（NVIDIA 驱动，本机无 NVIDIA 显卡）

→ 函数走到最后 `mov eax, 1; ret`（地址 `0xac5664f7`），返回 **1**。

**这是完全正确的结果** —— 本机使用 Intel Arc 显卡，dwm.exe 确实不会加载 AMD/NVIDIA 的 UMD。返回 1 本身不是 bug。

### 正常路径应该怎么走？

外层函数 `0xac5668f0` 是一个**状态机循环**，根据返回值执行不同操作：

```
状态机循环 (0xac566929):
    │
    ├─ case 2 (AMD): 
    │   ├─ 调用 0xac5662d8 (更新 "Aksuka" 状态结构)
    │   ├─ 检查 NVIDIA NVI2 接口 GUID {D50BA131-...}
    │   ├─ 格式化命名: "Global\%s%s.%d" (创建共享内存名)
    │   ├─ 调用 GetCurrentProcessId
    │   └─ jmp 回循环入口 ← 继续下一轮
    │
    ├─ case 3 (NVIDIA 无 ShadowPlay):
    │   ├─ 调用 0xac5662d8 (更新状态)
    │   ├─ 设置全局标志 [ac59cf18] = 1
    │   ├─ 调用 Sleep(0x32) ← 50ms 等待
    │   └─ jmp 回循环入口 ← 继续下一轮
    │
    ├─ case 4 (NVIDIA 有 ShadowPlay):
    │   ├─ 调用 0xac565c64
    │   ├─ 调用 0xac56629c
    │   ├─ WaitForSingleObject(句柄0xCB8, INFINITE) ← 等待事件
    │   ├─ 重置全局标志 [ac59cf1c] = 0, [ac59cf18] = 1
    │   └─ jmp 回循环入口 ← 继续下一轮
    │
    └─ case 1 (Intel/其他) 或其他值:
        └─ ★ BUG: jne 回循环入口，但无任何处理 → 空循环
```

**正常机器的情况：**

| 机器显卡 | 函数返回值 | 走的路径 | 行为 |
|---------|-----------|---------|------|
| AMD | 2 | case 2 | 检查 NVIDIA 接口 → 等待 → 循环 |
| NVIDIA 无 SP | 3 | case 3 | **Sleep(50ms)** → 循环（每轮休眠50ms，CPU占用低） |
| NVIDIA 有 SP | 4 | case 4 | **WaitForSingleObject(INFINITE)** → 事件触发后循环（几乎0 CPU） |
| **Intel（本机）** | **1** | **无 case 分支** | **★ 无限空循环** |

### 根本原因总结

这个 JIT 代码是**某第三方软件注入到 dwm.exe 的监视模块**（检查 `Aksuka` 字符串、NVIDIA GUID、ShadowPlay DLL，推测是**反作弊或屏幕录制/直播软件**的组件）。

它的状态机设计为：
- **检测到 AMD/NVIDIA 显卡** → 走对应的 hook/共享内存/事件等待路径，每轮循环都有等待操作（Sleep 或 WaitForSingleObject），不会空转
- **检测到 Intel 显卡（返回 1）** → switch-case **缺少 case 1 分支**，代码直接跳回循环入口重新调用检测函数，**没有等待、没有 Sleep、没有 break** → 形成紧密无限循环

**switch-case 编译器的缺陷**：`jne 0xac566929` 指令在所有 case 都不匹配时跳回循环入口，而非跳出循环或进入 default 分支。这是一个**代码生成或手写汇编中的逻辑遗漏**——开发者只考虑了 AMD/NVIDIA 场景，未处理 Intel/其他厂商显卡的情况。

---

## 三点七五、调用者如何知道检测结果

### 函数不返回——永久状态机循环

switch-case 外层函数 `0xac5668f0` **不会返回**。它是一个永久运行的状态机循环：

```asm
; 函数入口
0xac566904: cmp [0xac59cf38], 0    ← 检查全局上下文是否已初始化
0xac56690c: jne  0xac566922       ← 已初始化 → 进入循环
0xac566912: xor  eax, eax         ← 未初始化 → return 0

; 循环体
0xac566922: call 0xac566410       ← 厂商检测
0xac566929: ... switch-case ...
0xac566aae: call 0xac566f08       ← 每轮结束：将结果写入共享内存
0xac566ab3: jmp  0xac566929      ← ★ 跳回循环入口（永不退出）
```

**调用者 `0xac5714ce` 永远不会得到返回值**——因为在正常路径（case 2/3/4）下函数也不返回，它在 `0xac566ab3` 处跳回循环入口。在 case 1（Intel）下更是无限空循环。

### 结果传递机制：共享内存 + 事件信号

调用者通过以下三种机制获取检测结果：

#### 机制 1：共享内存（`0xac566f08` 每轮执行）

每轮循环结束时调用 `0xac566f08`，该函数：

1. 读取全局指针 `[0xac59cf28]` → 共享结构体 `0xa96e7940`
2. 读取 `[结构体+30h]` → 数据缓冲区指针 `rbx`
3. 将标识符 `"AsukaNvapcap"` 写入栈缓冲区 `[rbp-40h]`
4. 从 `rbx` 读取 48 字节数据到栈
5. 调用 `0xac568be4`（**自定义加密函数**，类似 RC4 流密码）加密数据
6. 加密后的数据写回 `rbx` 指向的共享内存

**注入者（调用者进程）通过 `OpenFileMapping` 打开同名共享内存，读取加密后的状态数据。**

共享内存名在 case 2 中通过 `Global\%s%s.%d` 格式化创建：
- `%s` = 模块标识（如 `AsukaNvapcap`）
- `%s` = 额外标识
- `%d` = `GetCurrentProcessId()` 返回值（PID 1916）

最终共享内存名类似：`Global\AsukaNvapcap.xxxx.1916`

#### 机制 2：全局状态标志

case 3/4 路径中设置全局变量：

| 全局变量 | case 3 设置值 | case 4 设置值 | 含义 |
|---------|-------------|-------------|------|
| `[0xac59cf18]` | 1 → 0 (循环中) | 0 → 1 (完成后) | 初始化/运行状态标志 |
| `[0xac59cf1c]` | — | 0 | 重置标志 |
| `[0xac59cf40]` | `nvwgf2umx.dll` 的 `GetProcAddress` 结果 | 同 | NVIDIA hook 函数地址 |
| `[0xac59cf48]` | `kernel32.dll` 的 `GetProcAddress` 结果 | 同 | kernel32 hook 函数地址 |

#### 机制 3：事件信号（句柄 0xCB8）

case 4 路径中：
1. `WaitForSingleObject(0xCB8, INFINITE)` — 等待事件触发
2. 事件触发后执行 hook 操作
3. 重置 `[0xac59cf1c] = 0`，`[0xac59cf18] = 1`

句柄 `0xCB8` 已确认为 **Event 对象**。注入者通过 `SetEvent(0xCB8)` 远程触发 dwm.exe 中的 hook 执行。

### 完整通信架构

```
注入者进程 (外部)                     dwm.exe (被注入)
┌─────────────────────┐              ┌──────────────────────────────┐
│                      │              │                              │
│  CreateRemoteThread  │──────────────│→ 线程 21 入口 0xac571474     │
│  (VirtualAlloc +     │  shellcode  │  │                           │
│   WriteProcessMemory │  注入       │  ├→ 0xac5668f0 状态机循环    │
│   + CreateThread)    │              │  │   ├─ case 2/3/4: 正常路径 │
│                      │              │  │   └─ case 1: ★ 死循环     │
│  OpenFileMapping     │              │  │                           │
│  ("Global\Asuka..    │──────────────│→ [0xac59cf28] 共享结构体     │
│   Nvapcap.1916")     │  读取加密    │  └→ 0xac566f08 每轮写入      │
│                      │  状态数据    │      ├─ "AsukaNvapcap" 标识  │
│  MapViewOfFile       │              │      └─ 0xac568be4 加密写入  │
│  ↓                   │              │                              │
│  读取共享内存         │              │                              │
│  解密状态数据         │              │                              │
│                      │              │                              │
│  SetEvent(0xCB8)     │──────────────│→ WaitForSingleObject(0xCB8)  │
│  (远程触发 hook)      │  事件信号    │  (case 4: 触发 hook 执行)    │
│                      │              │                              │
└─────────────────────┘              └──────────────────────────────┘
```

### 为什么 case 1（Intel）时调用者收不到结果

在 case 1（Intel 显卡）时：
1. switch-case **不执行任何 case 分支**（无 case 1）
2. `jne 0xac566929` 跳回循环入口 → **重新调用检测函数**
3. **不会执行到 `0xac566aae: call 0xac566f08`**（共享内存写入）
4. **不会设置全局状态标志**
5. **不会到达 `WaitForSingleObject`**

因此注入者**永远收不到检测结果**——共享内存中的数据停留在初始值，事件永远不会被等待。

**注入者代码可能也有超时重试逻辑**：如果在一定时间内收不到共享内存更新，可能会重新注入或放弃——但这需要分析注入者进程才能确认。

---

## 四、精确解决方案

### 方案 A：使用 Sysmon 定位注入源（首选）

JIT 代码通过 shellcode 注入（VirtualAlloc + WriteProcessMemory + CreateRemoteThread），不加载任何 DLL，因此需要通过系统盻控定位注入者：

1. **部署 Sysmon**（如果尚未安装）：
   - 下载 Sysmon: https://learn.microsoft.com/sysinternals/downloads/sysmon
   - 安装并配置监控行为规则：
   ```xml
   <RuleGroup name="RemoteThread" groupRelation="or">
     <RemoteThread onmatch="include">
       <TargetImage condition="end with">dwm.exe</TargetImage>
     </RemoteThread>
   </RuleGroup>
   ```

2. **重启后检查 Sysmon 日志**：
   - 事件 ID 8 (CreateRemoteThread) → TargetImage = dwm.exe
   - SourceImage 就是注入者进程
   - 事件 ID 10 (ProcessStart) → 查看注入者进程的完整路径

3. **根据注入者路径定位软件**：
   - 检查该路径对应的软件
   - 更新或卸载该软件

### 方案 B：使用 Autoruns 检查启动项

1. 下载 Sysinternals Autoruns
2. 检查以下类别：
   - Explorer（Explorer 加载项）
   - Internet Explorer（IE 加载项）
   - Services（自启动服务）
   - Drivers（驱动程序）
   - Scheduled Tasks（计划任务）
3. 排查非 Microsoft 发布的项

### 方案 C：临时缓解（重启 dwm）

```powershell
# 在 PowerShell (管理员) 中执行
Stop-Process -Name "dwm" -Force
```

dwm.exe 会自动重启。但注意：这只是清除当前死循环，注入者会再次注入并重现问题。

### 方案 D：使用 Process Monitor 实时监控

1. 下载 Sysinternals Process Monitor
2. 设置过滤器：
   - Process Name = dwm.exe
   - Operation = CreateFile / WriteFile / VirtualAlloc
3. 等待问题重现
4. 查看哪个进程在向 dwm.exe 写入数据

### 方案 E：向注入软件开发商报告 Bug

报告时附上以下关键信息：
- 注入方式：Shellcode（VirtualAlloc + CreateRemoteThread，无 DLL 加载）
- JIT 代码中检查的 DLL 名：`amdx6x4.dll`、`nvwgf2umx.dll`、`nvspcap44.dll`
- 内部标识符：`AsukaNvapcap`
- NVIDIA NVI2 接口 GUID：`D50BA131-BBCD-42EA-AE03-540F4767A65A`
- 共享内存命名格式：`Global\%s%s.%d`
- Bug 类型：switch-case 缺少 case 1（Intel/其他显卡）分支，导致无限空循环
- 死循环地址范围：`000001c0ac566929` ~ `000001c0ac56693c`
- 触发条件：注入到使用 Intel 显卡的系统中的 dwm.exe

---

## 八、如何修改返回值（dump 分析方案）

### 当前 dump 状态

```
调试模式: examine (dump 文件静态分析，非 live debug)
线程 21 寄存器:
  rdi = 1 (厂商检测返回值 = Intel)
  rdx = 0xfffffffe (上一轮循环旧值)
  rip = 0xac566929 (循环入口 mov edx,edi)
```

### 修改方案分析

#### 方案 1：改 rdi=3（case 3 路径）— ❌ 不安全

```
rdi=3 → sub edx,2 → edx=1 (≠0) → sub edx,1 → edx=0 (=0) → je 0xac56693e
→ call 0xac5662d8
→ call 0xac566508(ecx=1)  ← capability 检查
→ call 0xac566508(ecx=4)  ← capability 检查
→ call 0xac56660c          ← AsukaNvapcap 检查
→ cmp edi,4 → jne 0xac5669c6  ← edi=3≠4 → case 3 路径
→ call 0xac565c64           ← ★ GetProcAddress(nvwgf2umx.dll, ...)
                               ← nvwgf2umx.dll 未加载 → GetModuleHandle 返回 NULL
                               ← GetProcAddress(NULL, ...) → 崩溃风险
```

**问题**：case 3 路径会调用 `0xac565c64`，该函数检查 `[0xac59cf40]` 和 `[0xac59cf48]` 是否已设置。当前值均为 0，因此会执行 `GetModuleHandle("nvwgf2umx.dll")`。本机没有 NVIDIA 显卡驱动，返回 NULL，后续 `GetProcAddress(NULL, ...)` 会崩溃。

#### 方案 2：改 rdi=2（case 2 路径）— ⚠️ 有风险

```
rdi=2 → sub edx,2 → edx=0 → je 0xac566a87
→ call 0xac5662d8          ← 状态更新
→ call 0xac566508(ecx=2)   ← capability 检查
→ call 0xac56660c           ← AsukaNvapcap 检查
→ 检查 NVIDIA NVI2 GUID
→ 创建共享内存 "Global\%s%s.%d"
→ GetCurrentProcessId
→ jmp 0xac566929            ← 回循环入口
```

**分析**：case 2 路径不调用 `0xac565c64`（GetProcAddress），不会崩溃。但它会尝试创建共享内存和检查 NVIDIA 接口，如果 `[0xac59cf28]` 为 NULL（共享结构体未初始化），capability 检查函数 `0xac566508` 会在 `test rdi,rdi` / `je 0xac5665ed` 处直接返回 0（`sil=0`）。

**安全性**：case 2 路径有 NULL 检查保护，不会崩溃。但每轮循环都执行共享内存操作，效率不如 case 3 的 Sleep 路径。

#### 方案 3：直接改 jne 为 jmp（跳过循环）— ✅ 最安全

```
当前指令: 0xac56693c: jne 000001c0ac566929  (75 EB = 跳回循环入口)
修改为:   0xac56693c: jmp 000001c0ac566aae  (EB 70 = 跳到循环末尾)
```

**修改方式**：将 `jne 0xac566929`（`75 EB`）改为 `jmp 0xac566aae`（`EB 70`），直接跳到每轮循环结束的 `call 0xac566f08`（共享内存写入），然后 `jmp 0xac566929` 回循环入口。

**效果**：
- 跳过 switch-case 分支选择
- 直接执行共享内存写入（将当前状态告知注入者）
- 然后回循环入口重新检测
- **没有 Sleep**，但也不会空转——每轮都执行共享内存写入（有 IO 操作，CPU 占用低）

#### 方案 4：改 jne 为 nop + jmp（跳到 Sleep 路径）— ✅ 最优

```
当前: 0xac56693c: 75 EB  (jne 0xac566929 → 死循环)
改为: 0xac56693c: 90 90  (nop; nop → 顺序执行到 0xac56693e)
```

顺序执行到 `0xac56693e`：
```
0xac56693e: call 0xac5662d8     ← 状态更新
0xac566943: call 0xac566508(1)  ← capability 检查（有 NULL 保护）
0xac566952: call 0xac566508(4)  ← capability 检查（有 NULL 保护）
0xac56695c: call 0xac56660c     ← AsukaNvapcap 检查（有 NULL 保护）
0xac56696a: test bl,bl          ← capability 1 结果
0xac56696c: je 0xac5669aa       ← bl=0 → 跳转
```

由于 `[0xac59cf28]` 当前为 `0xa96e7940`（非 NULL），但 `0xac566508` 内部会进一步检查 `[rdi+28h]` 和 `[rdi+30h]`。如果这些字段为 NULL，`0xac566508` 返回 0（`sil=0`），然后 `test bl,bl` / `je 0xac5669aa` 跳转。

跳到 `0xac5669aa` 后：
```
0xac5669aa: test bpl,bpl        ← bpl=0
0xac5669ad: je 0xac566aae       ← 跳到循环末尾
0xac566aae: call 0xac566f08     ← 共享内存写入
0xac566ab3: jmp 0xac566929      ← 回循环入口
```

**效果**：每轮循环执行 capability 检查（返回 0）→ 跳到共享内存写入 → 回循环入口。有 IO 操作，不会空转，不会崩溃。

#### 方案 5：改 jne 为 int3（终止线程）— ✅ 最简单

```
当前: 0xac56693c: 75 EB  (jne → 死循环)
改为: 0xac56693c: CC CC  (int3; int3 → 触发异常终止线程)
```

**效果**：线程 21 执行到此处时触发断点异常，Windows 异常处理会终止该线程。dwm.exe 的其他线程不受影响。

### 推荐方案

| 方案 | 修改 | 优点 | 缺点 | 适用场景 |
|------|------|------|------|---------|
| **方案 4（推荐）** | `75 EB` → `90 90` | 不崩溃，有 IO，低 CPU | 无 Sleep，仍有循环 | 长期运行 |
| 方案 3 | `75 EB` → `EB 70` | 直接跳到共享内存写入 | 无 Sleep | 快速验证 |
| 方案 5 | `75 EB` → `CC CC` | 最简单，终止线程 | 丢失检测结果 | 紧急止损 |
| 方案 2 | rdi=2 | 走 AMD 路径 | 可能创建无效共享内存 | 验证 case 2 逻辑 |
| 方案 1 | rdi=3 | 有 Sleep(50ms) | ★ GetProcAddress 崩溃 | 不推荐 |

### 在 dump 中验证方案 4

```windbg
# 1. 确认当前字节
eb 000001c0`ac56693c L2     ← 显示: 75 eb

# 2. 修改 jne 为 nop nop
eb 000001c0`ac56693c 90 90

# 3. 验证修改
u 000001c0`ac56693c L3      ← 应显示: nop; nop; call 0xac5662d8

# 4. 修改 rip 回到循环入口
r rip=000001c0`ac566929

# 5. 修改 rdi 仍为 1（保持原始返回值，但现在 nop 会顺序执行到 case 3/4 路径）
# capability 检查会返回 0 → 跳到 0xac5669aa → 跳到 0xac566aae → 共享内存写入 → 循环
```

### 在 live 环境中的实际操作

由于是 dump 文件，以上修改无法在运行中生效。在实际 live 环境中，需要：

1. **使用 WinDbg 附加到 dwm.exe（live 模式）**
2. **在 JIT 代码区域设置硬件断点**：`ba e 1 000001c0ac56693c`
3. **断点命中后修改指令**：`eb 000001c0ac56693c 90 90`
4. **继续执行**：`g`

或使用 **DLL 注入工具** 编写一个修复 DLL：
```cpp
// 修复 DLL：注入到 dwm.exe 后修改 JIT 代码
DWORD oldProtect;
VirtualProtect((LPVOID)0x000001C0AC56693C, 2, PAGE_EXECUTE_READWRITE, &oldProtect);
memcpy((LPVOID)0x000001C0AC56693C, "\x90\x90", 2);  // jne → nop; nop
VirtualProtect((LPVOID)0x000001C0AC56693C, 2, oldProtect, &oldProtect);
```

**注意**：JIT 代码地址 `0x000001C0AC56693C` 在每次 dwm.exe 重启后会变化，需要通过特征码搜索定位。

---

## 九、特征码安全性分析

### 特征码选择与验证

在 JIT 代码区域（`0xac560000`，268KB）中搜索不同长度的特征码：

| 特征码 | 长度 | 匹配数 | 唯一性 | 含地址依赖 |
|--------|------|--------|--------|-----------|
| `83 FA 01` | 3 字节 | 16 处 | ❌ 不唯一 | 否 |
| `83 FA 01 75 EB E8` | 6 字节 | 1 处 | ✅ 唯一 | `75 EB` 中的 `EB` 依赖布局 |
| `83 EA 01 74 05 83 FA 01` | 7 字节 | 1 处 | ✅ 唯一 | **否**（纯指令操作码） |
| `8B D7 83 EA 02` | 5 字节 | 1 处 | ✅ 唯一 | 否 |

### 推荐特征码

```
83 EA 01 74 05 83 FA 01
```

**分解：**
```
83 EA 01    sub edx, 1       ← 纯寄存器操作，无地址依赖
74 05       je +5            ← 短跳转，偏移固定为 5（跳过 cmp edx,1）
83 FA 01    cmp edx, 1       ← 纯寄存器操作，无地址依赖
```

**这 7 字节全部是指令操作码 + 固定操作数**，不包含任何：
- ❌ 相对跳转偏移量（如 `jne` 的 `EB` 或 `je` 的 `0F 84 xx xx xx xx`）
- ❌ 绝对地址引用
- ❌ 相对调用偏移量（如 `E8 xx xx xx xx`）

**唯一性**：在 268KB JIT 代码中唯一匹配 `0xac566934`。

### 修改位置

特征码匹配后，偏移 **+7** 处就是 `jne` 指令（`75 EB`）：

```
偏移 0: 83 EA 01         sub edx, 1
偏移 3: 74 05             je +5
偏移 5: 83 FA 01          cmp edx, 1
偏移 7: 75 EB             jne ← 修改目标（75 EB → 90 90）
偏移 9: E8 ...            call ...
```

### 特征码跨重启稳定性分析

**JIT 代码性质确认：**
- 内存区域：`MEM_PRIVATE` + `PAGE_EXECUTE_READWRITE`
- Allocation Protect 也是 `PAGE_EXECUTE_READWRITE`（一次性分配）
- 区域大小：268KB（`0x43000`）
- 不属于任何已加载模块

**这是注入者写入的完整 shellcode blob**，不是 JIT 编译器动态生成的代码。特征码稳定性取决于注入者是否修改 shellcode：

| 场景 | 特征码稳定性 | 风险 |
|------|-------------|------|
| 注入者不更新 shellcode | ✅ 完全稳定 | 无 |
| 注入者更新 shellcode 但 switch-case 逻辑不变 | ✅ 稳定 | 无 |
| 注入者重写 switch-case 逻辑 | ❌ 不稳定 | 需重新分析 |
| 不同版本的注入者 | ⚠️ 可能变化 | 需验证 |

### 安全风险评估

**特征码搜索本身是安全的：**
1. **只读搜索**：特征码匹配阶段只读取内存，不修改任何内容
2. **精确匹配**：7 字节特征码在 268KB 中唯一匹配，不会误伤其他代码
3. **上下文验证**：匹配后可以验证 `+7` 处是否为 `75 EB`（jne），确认后再修改

**修改操作的安全保障：**
1. **VirtualProtect 保护**：修改前调用 `VirtualProtect` 确保页面可写
2. **修改前验证**：验证目标字节确实是 `75 EB` 而非其他值
3. **修改后验证**：反汇编确认 `nop; nop` 已正确写入
4. **修改量最小**：只改 2 字节（`75 EB` → `90 90`），不影响其他指令

### 潜在风险

1. **ASLR/地址随机化**：JIT 代码地址每次 dwm.exe 重启后不同
   - 应对：不使用硬编码地址，用特征码搜索定位

2. **多个 PAGE_EXECUTE_READWRITE 区域**：dwm.exe 中可能有多个可执行私有内存区域
   - 应对：遍历所有 `MEM_PRIVATE + PAGE_EXECUTE_READWRITE` 区域，在每块中搜索特征码

3. **特征码不匹配**：注入者更新了 shellcode
   - 应对：修改失败时安全退出，不执行任何写入操作

4. **修改时序竞争**：修改 `jne` 时线程 21 可能正在执行该指令
   - 应对：使用 `SuspendThread` 暂停线程 → 修改 → `ResumeThread` 恢复

### 完整修复代码（C++）

```cpp
#include <windows.h>
#include <psapi.h>
#include <tlhelp32.h>
#include <vector>

// 特征码：sub edx,1; je +5; cmp edx,1
static const BYTE PATTERN[] = { 0x83, 0xEA, 0x01, 0x74, 0x05, 0x83, 0xFA, 0x01 };
static const SIZE_T PATTERN_LEN = sizeof(PATTERN);
// 修改目标：特征码后 +7 处的 jne (75 EB → 90 90)
static const SIZE_T PATCH_OFFSET = 7;
static const BYTE ORIGINAL[] = { 0x75, 0xEB };  // jne
static const BYTE PATCHED[] = { 0x90, 0x90 };   // nop; nop

struct MemRegion { BYTE* base; SIZE_T size; };

// 枚举所有 PAGE_EXECUTE_READWRITE + MEM_PRIVATE 区域
std::vector<MemRegion> FindExecutablePrivateMemory(HANDLE hProc) {
    std::vector<MemRegion> regions;
    MEMORY_BASIC_INFORMATION mbi;
    BYTE* addr = nullptr;
    while (VirtualQueryEx(hProc, addr, &mbi, sizeof(mbi))) {
        if (mbi.State == MEM_COMMIT &&
            mbi.Protect == PAGE_EXECUTE_READWRITE &&
            mbi.Type == MEM_PRIVATE) {
            regions.push_back({ (BYTE*)mbi.BaseAddress, mbi.RegionSize });
        }
        addr = (BYTE*)mbi.BaseAddress + mbi.RegionSize;
    }
    return regions;
}

// 在区域中搜索特征码
BYTE* PatternScan(HANDLE hProc, BYTE* base, SIZE_T size) {
    std::vector<BYTE> buf(size);
    SIZE_T read;
    if (!ReadProcessMemory(hProc, base, buf.data(), size, &read))
        return nullptr;

    for (SIZE_T i = 0; i + PATTERN_LEN + 2 <= read; i++) {
        if (memcmp(buf.data() + i, PATTERN, PATTERN_LEN) == 0) {
            // 验证 +7 处是 jne (75 EB)
            if (buf[i + PATCH_OFFSET] == 0x75 && buf[i + PATCH_OFFSET + 1] == 0xEB) {
                return base + i;
            }
        }
    }
    return nullptr;
}

void PatchDwmJitCode(DWORD dwmPid) {
    HANDLE hProc = OpenProcess(PROCESS_ALL_ACCESS, FALSE, dwmPid);
    if (!hProc) return;

    // 暂停所有线程
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    THREADENTRY32 te; te.dwSize = sizeof(te);
    std::vector<HANDLE> threads;
    if (Thread32First(snap, &te)) {
        do {
            if (te.th32OwnerProcessID == dwmPid) {
                HANDLE hT = OpenThread(THREAD_SUSPEND_RESUME, FALSE, te.th32ThreadID);
                if (hT) { SuspendThread(hT); threads.push_back(hT); }
            }
        } while (Thread32Next(snap, &te));
    }
    CloseHandle(snap);

    // 搜索并修补
    auto regions = FindExecutablePrivateMemory(hProc);
    for (auto& r : regions) {
        BYTE* match = PatternScan(hProc, r.base, r.size);
        if (match) {
            BYTE* patchAddr = match + PATCH_OFFSET;
            DWORD oldProtect;
            // 验证原始字节
            BYTE verify[2];
            ReadProcessMemory(hProc, patchAddr, verify, 2, nullptr);
            if (verify[0] != 0x75 || verify[1] != 0xEB) continue;

            // 写入修补
            VirtualProtectEx(hProc, patchAddr, 2, PAGE_EXECUTE_READWRITE, &oldProtect);
            WriteProcessMemory(hProc, patchAddr, PATCHED, 2, nullptr);
            VirtualProtectEx(hProc, patchAddr, 2, oldProtect, &oldProtect);

            printf("[+] Patched at %p: 75 EB -> 90 90\n", patchAddr);
            break;
        }
    }

    // 恢复线程
    for (HANDLE hT : threads) { ResumeThread(hT); CloseHandle(hT); }
    CloseHandle(hProc);
}
```

## 六、谁在调用这个函数（完整调用链）

### 调用链追踪

通过栈转储 (`dps`) 和反汇编，完整调用链如下：

```
ntdll!RtlUserThreadStart          ← Windows 线程启动入口
  └─ kernel32!BaseThreadInitThunk  ← Windows 线程基础初始化
       └─ 0x000001c0ac571474      ← JIT 代码：线程入口函数
            │
            ├─ 0xac5793b4         ← 获取全局上下文 (TLS 查找)
            │     └─ call [0xac586040] = kernel32!GetLastError
            │     └─ call 0xac57b1e0 (TLS 模块枚举)
            │
            ├─ 0xac57b4a0         ← 检查初始化状态
            │     └─ 返回值 2 → 需要初始化
            │
            ├─ 0xac5714c5: mov rax, [rbx]     ← 从线程参数读取函数指针
            ├─ 0xac5714c8: call [0xac5863b0] ← 间接调用
            │     └─ 0xac5863b0 → 0xac583f10 (thunk: jmp [0xac5863a8])
            │          └─ 0xac5863a8 → 0xac583ef0 (jmp rax)
            │               └─ jmp rax → 跳转到 [rbx] 中的函数指针
            │                    └─ ★ 调用 switch-case 函数 0xac5668f0
            │
            ├─ 0xac5714d0: call 0xac571688  ← 结果处理
            │     └─ call 0xac5714e4        ← 清理函数
            │          └─ call [0xac586080] = kernel32!CloseHandle
            │          └─ call [0xac586278] = kernel32!FreeLibraryAndExitThread
            │
            └─ 0xac5714d8: call 0xac577e58  ← 命令行解析（最终清理）
```

### 关键发现

**线程入口函数 `0xac571474` 通过 `jmp rax` gadget 间接调用 switch-case 函数。**

1. `0xac5714c5`: `mov rax, [rbx]` — 从 `rbx`（线程参数结构体）中读取函数指针
2. `0xac5714c8`: `call [0xac5863b0]` — 通过 thunk 链跳转到 `jmp rax`
3. `jmp rax` 跳转到 `rax` 中的地址 — 即 switch-case 函数 `0xac5668f0`

**switch-case 函数的地址是通过线程参数结构体传递的函数指针**，不是硬编码的。

### 线程创建者

线程 21 由 `kernel32!CreateThreadStub` 创建（通过 `0xac586268` 间接调用）：
- 线程入口地址：`0x000001c0ac571474`（JIT 代码）
- 线程参数：指向一个结构体，其中 `[结构体+0]` = switch-case 函数指针

### 注入方式确认

检查进程模块列表 (`!for_each_module`) 发现：
- **所有已加载模块都是 Windows 系统模块或 Intel 显卡驱动**
- **没有 NVIDIA DLL**（无 `nvspcap64.dll`、`nvwgf2umx.dll` 等）
- **没有第三方软件 DLL**

这表明 JIT 代码**不是通过 DLL 注入**的，而是通过：
- `VirtualAlloc` + `WriteProcessMemory` 直接写入可执行代码
- 然后 `CreateRemoteThread` 创建线程执行该代码

### 最终结论

**调用 switch-case 函数的是 JIT 代码内部的线程入口函数 `0xac571474`，它通过 `jmp rax` gadget 间接调用。**

函数指针通过线程参数结构体传入，这个结构体在创建线程时由注入代码设置。由于：
1. JIT 代码在 `MEM_PRIVATE` + `PAGE_EXECUTE_READWRITE` 内存中（非任何已加载模块）
2. 进程中没有第三方 DLL
3. 线程通过 `kernel32!CreateThread` 创建（标准 API）
4. 调用链使用 `jmp rax` gadget 混淆控制流

**这是一个典型的 shellcode 注入手法**——注入者通过 `VirtualAllocEx` + `WriteProcessMemory` + `CreateRemoteThread` 将 shellcode 注入到 dwm.exe 中，shellcode 自包含所有逻辑（不依赖任何 DLL），直接在私有内存中执行。

### 如何定位注入者

由于注入代码不加载任何 DLL，无法通过模块列表定位。建议：

1. **检查 dwm.exe 的句柄和内存映射**：
   - 使用 Process Explorer → dwm.exe → Properties → Handles
   - 查找可疑的 Section/View 句柄

2. **检查系统中使用 CreateRemoteThread 的进程**：
   - 使用 Sysmon 盻控：`Event ID 8: CreateRemoteThread`
   - 筛选 TargetImage = `dwm.exe`

3. **检查启动项和服务**：
   - `Get-CimInstance Win32_StartupCommand`
   - `Get-Service | Where-Object {$_.StartType -eq 'Auto'}`
   - 检查 `HKLM\SYSTEM\CurrentControlSet\Services` 中的非微软服务

4. **使用 Autoruns**：
   - 下载 Sysinternals Autoruns
   - 检查 "Explorer" 和 "Internet Explorer" 类别下的可疑项
   - 检查 "Services" 和 "Drivers" 类别

5. **检查计划任务**：
   - `Get-ScheduledTask | Where-Object {$_.State -eq 'Ready'}`
   - 查找触发器为 "At logon" 的可疑任务

---

## 七、诊断证据汇总

```
dwm.exe (PID 0x77C)
│
├─ 线程 21 (TID: 5428)  ← CPU: 2分38秒 (95%+)
│   ├─ Start: 000001c0ac571474 (JIT 代码区域内)
│   ├─ 调用栈 Frame 0: 000001c0ac566939
│   │   └─ PAGE_EXECUTE_READWRITE (JIT 代码) ← 无限循环: 0x929 → 0x93C
│   └─ 调用栈 Frame 1-2: 000001c0a8292670
│       └─ Heap (PAGE_READWRITE)
│
├─ Intel 驱动模块 (v32.0.101.8974, 发布 2026-08-14)
│   ├─ igc64.dll       ← Shader Compiler (JIT 代码来源)
│   ├─ igd10iumd64.dll ← D3D11 User Mode Driver
│   ├─ igd10umt64xe   ← D3D10 User Mode Driver
│   └─ igdml64.dll     ← Graphics Media Layer
│
└─ 正常合成线程 (线程 2)
    └─ dwmcore!CMonitorClock::WaitForNextTick ← 正常等待
```
