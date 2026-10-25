# START HERE — 给接手的 AI

这是一份自包含的交接包。**读完这份文件就能直接开始改代码**，不需要额外提问。
更深的背景按需读 `docs/` 下的分册。

---

## 1. 一句话

**MouseMeter** 是一个 Windows 桌面小工具：绕开鼠标厂商的网页驱动，直接跟 2.4G 接收器的 HID 特征报文对话，把电量显示在**任务栏托盘图标**和**桌面悬浮挂件**上。

- 作者身份：个人自用工具，由上一个 AI 从零写出来，已经能稳定跑
- 当前状态：**功能完整、可用**，视觉是苹果极简风；缺的是工程化（无安装包、无蓝牙、无测试）
- 你的任务：见 `docs/ROADMAP.md`。**先读第 2 节的硬约束，否则会白改**

---

## 2. 硬约束 — 请务必先看完

这些不是建议，是上一轮踩过的坑。违反任何一条都会导致「改完跑不起来」。

### 2.1 语言等级只有 C# 5

代码不是用 `dotnet build` 编译的，而是 PowerShell 里 `Microsoft.CSharp.CSharpCodeProvider`（.NET Framework 4.8 的 CodeDOM），**默认 C# 5**。

因此**不要**使用：

```csharp
$"插值字符串"          // C# 6
nameof(x)              // C# 6
obj?.Prop              // C# 6
x => y                 // 表达式体成员, C# 6
public int P => _p;    // C# 6
using static X;        // C# 6
switch (...) when ...  // 模式匹配, C# 7
(int a, int b) = ...   // 元组, C# 7
```

**要写**：

```csharp
"拼接" + level + "%"
delegate { ... }            // 代替 lambda 可以，但 lambda 其实 C# 3 就支持，可用
public int P { get { return _p; } }
```

如果你确实想升级到现代 C#/.NET，那属于「重写工程」而不是「优化」——见 ROADMAP 的 P0-3，需要先跟用户确认。

### 2.2 依赖只能是 .NET Framework 自带的

可用程序集仅这四个：`System.dll`、`System.Core.dll`、`System.Drawing.dll`、`System.Windows.Forms.dll`。
**没有** NuGet、没有 `System.Text.Json`、没有 `System.Web.Extensions`（所以配置解析是手写的 `MiniJson`）。

原环境还限制了：

- `pip install` 基本不通 → Python + PySide6 路线已试过并放弃
- `csc.exe` 和 `Add-Type -OutputAssembly` 被安全策略拦截 → 这**正是**为什么用 CodeDOM 编译
- 用 Python 的 tkinter？该环境未安装，不可用

### 2.3 所有 .ps1 必须存成 UTF-8 **带 BOM**

PowerShell 5.1 读无 BOM 的脚本时按系统 ANSI（GBK）解码，中文注释和字符串会乱码，**连带把变量/数组初始化搞坏**（上一轮就是这么炸的）。

```powershell
$enc = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($path, $content, $enc)
```

### 2.4 读取报文时 report ID 必须先写进缓冲区

`HidD_GetFeature` 要求调用方**先把 report ID 填在 `buf[0]`**，否则返回 `ERROR_INVALID_PARAMETER (87)`。
这是最容易被误判成「设备不支持」的坑。见 `Hid.GetFeature(h, len, reportId)`。

### 2.5 接收器在鼠标休眠时会拒答

返回 `ERROR_GEN_FAILURE (31)`，不是错误，是正常现象。代码里已经做了 8 次重试（每次 ~60+140ms），
失败时**保留上一次读数并标记 stale**（灰显），绝不清空成「未检测到设备」。

### 2.6 进程启动要脱离宿主

上一轮环境里，在工具调用中 `Start-Process` 起的进程会在该调用结束时被回收。
稳妥做法：通过 `run-hidden.vbs`（`wscript` + 隐藏窗口）启动，它是完全脱离的。
正常用户环境不需要这么绕。**现在开机自启已改为直接指向 `MouseMeter.exe`**
（见 `AutoStart.Command()`：exe 存在就用 exe，否则才回退到 vbs）。

---

## 3. 立即运行

工作流只有两个入口，别记别的：

| 你要做的事 | 命令 | 它做了什么 |
|---|---|---|
| 改着看效果 | `dev.cmd` | 增量编译 → 旧实例优雅退出 → 启动新版本（1~2 s） |
| **准备提交 / 交付** | **`check.cmd`** | 编译 → 全部测试 → 出 exe → 同步文档数字 |

`check.cmd` 是这条流里唯一需要记住的"整合"动作。它按顺序做六件事：

```
1. 编码闸门   所有 .ps1 是否带 BOM（缺了 PowerShell 5.1 会按 GBK 读，中文注释炸数组）
2. 语法闸门   src\*.cs 有没有混进 C# 6+ 语法（CodeDOM 只认 C# 5）
3. 依赖闸门   Assembly.Load 是否越出四个白名单程序集
4. 编译 + 测  CodeDOM 编译，再编出测试 exe 跑全套断言
5. 出包       Build.ps1 生成 dist\MouseMeter.exe，并确认文件的修改时间已刷新
6. 同步文档   把断言数 / 最大文件行数写回 README、START-HERE、docs
```

任何一步失败就**停在原地并说明原因**，文档不会被改（避免把红着的测试数写进文档）。
`-q` 跳过出包，`-n` 跳过文档，`-fix` 自动补 BOM。

**改代码：双击 `dev.cmd`**（或 `.\dev.cmd -s` 直接开设置）。

### 首次接入 git（只做一次）

```powershell
git init
git add .
git commit -m "baseline"        # 第一次用 --no-verify，钩子还没装
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-Hooks.ps1
```

装完之后每次 `git commit` 都会自动跑 `Verify.ps1 -Quick`（跳过出包，因为 exe 不进仓库）。
**这才是"不用记得跑验证"的保证**——钩子拦在提交之前，红的树根本进不去历史。
紧急情况用 `git commit --no-verify` 绕过，但要清楚跳过的是编译 + 全部断言。

`.gitignore` 已排除 `dist/` 和 `.verify-last`：前者是构建产物，后者是单机上次运行的结果，
提交它会让一个过期的绿色数字活下来。

**出包（交付形态）**：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
# 产物：dist\MouseMeter.exe，双击即用，无控制台窗口
```

**为什么用 dev.cmd 而不是直接跑 MouseMeter.ps1**：

1. `dev.cmd` 会先杀旧进程再启动，否则新实例撞上单实例互斥锁就悄悄退出了 ——
   表现为「改了代码但界面没变」，是这条链路上最容易浪费半小时的坑。
2. 它在本进程内 `LoadFrom` 编译出的 DLL，所以 `OnPaint` 里抛的异常直接打在终端上。
   `MouseMeter.ps1` 是独立进程，崩了只剩一个图标消失。
3. 编译**失败时不动**正在运行的版本，你能继续用旧界面调参数。

首次启动会用 CodeDOM 编译一次（约 0.7~1 s），之后按源码内容哈希命中缓存 DLL，**启动约 0.2 s**。

> **三个入口的关系**：`DevRun.ps1`、`Build.ps1`、`MouseMeter.ps1` 共用同一个
> `Get-MouseMeterSource` 合并逻辑，所以「跑源码」和「出 exe」编译的是同一份代码，不可能漂移。
> `run-hidden.vbs` 只是 PowerShell 方案的静默外壳；装了 exe 之后用不上它。

**重启协议**（改这块之前务必读懂）：

```
dev.cmd
  └─ DevRun.ps1
       ├─ Build-MouseMeter       哈希没变 => 直接用缓存 DLL
       ├─ 探测 Program.MutexName 被占用？
       │    └─ Set 事件 Local\MouseMeter.QuitRequest
       │         └─ 旧实例的 quit-listener 线程收到 => BeginInvoke(Quit)
       │              └─ Quit 调 _cfg.Save() 后 ExitThread
       └─ 轮询等锁释放（最多 3 s，正常 ~60 ms）=> 加载新 DLL，传 --restart
```

两点容易改坏：

- `Quit()` 必须**幂等**（`_quitting` 标志）。监听线程、托盘菜单、`Dispose()` 可能在几毫秒内同时进来，
  `NotifyIcon.Dispose()` 二次调用会抛异常。
- 等待用**轮询**而不是固定 `Start-Sleep 1.5`。健康实例 60 ms 就退出了，固定等待会让每次重编都慢一倍。

| 运行时产物 | 位置 |
|---|---|
| 配置 | `%APPDATA%\MouseMeter\settings.json` |
| 编译缓存（仅源码模式） | `%LOCALAPPDATA%\MouseMeter\cache\MouseMeter-<hash16>.dll` |
| 诊断日志 | `%LOCALAPPDATA%\MouseMeter\error.log` |

改完源码**不用手动清缓存**——哈希变了自动重新编译。

---

## 4. 代码地图

3 千行，10 个文件，一条主线。按这个顺序读：

| 文件 | 行数 | 读它的理由 |
|---|---:|---|
| `src/Providers.cs` | 205 | **核心**。`IBatteryProvider` 接口 + 迈从协议实现 + `DeviceManager` 调度 |
| `src/Hid.cs` | 180 | Win32 HID 枚举与读写（setupapi + hid.dll P/Invoke） |
| `src/Program.cs` | 428 | 入口、托盘、轮询循环、单实例、开机自启 |
| `src/HudForm.cs` | 369 | 桌面挂件（无边框圆角、拖动、右键菜单） |
| `src/SettingsForm.cs` | 564 | 设置窗口（macOS 系统设置风格，全自绘） |
| `src/Theme.cs` | 273 | 色板 + 字体 + 圆角路径 + DWM 特效 |
| `src/IconFactory.cs` | 254 | 托盘图标 / app 图标 / 多分辨率 .ico 生成 |
| `src/Controls.cs` | 402 | 自绘控件：开关、滑块、分段控件、按钮、卡片 |
| `src/MenuRenderer.cs` | 152 | 右键菜单的圆角与配色 |
| `src/Settings.cs` | 228 | 配置读写 + 手写 MiniJson |

数据流一句话：

```
Timer(60s) ─► ThreadPool ─► DeviceManager.Refresh()
                                  └─► McHoseProvider.TryRead()  ─► Hid.SetFeature / GetFeature
                                              │
                                    Reading{Level, Charging, DeviceName, Transport}
                                              │
                          _marshal.BeginInvoke ──► UI 线程
                                              │
                        ┌─────────────────────┴─────────────────────┐
                   UpdateTray()                             HudForm.UpdateReading()
               （图标 + tooltip）                        （挂件重绘 + 低电量通知）
```

线程规则：**网络/HID 一律在工作线程，任何 `Control` 操作必须经 `_marshal.BeginInvoke` 回 UI 线程**。
`TrayContext._marshal` 是个隐藏 `Control`，就是为这个存在的，别改成 `SynchronizationContext.Current`——
构造函数执行时 UI 上下文还没建立，会拿到 `null`（上一轮真踩过，挂件永远显示「未检测到设备」）。

---

## 5. 怎么验证你改对了

没有单元测试，验证靠三件事，缺一不可：

**① 编译通过**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\MouseMeter.ps1 -Settings
```
编译失败会弹 MessageBox 并把完整错误写入 `error.log`（含行号）。

**② 真的读到电量**

⚠️ 先注意一个陷阱：`%LOCALAPPDATA%\MouseMeter\log.txt` 是 **v1 遗留文件**，v2 **不写它**。
如果你在里面看到 `ok level=29 charging=0`，那是旧脚本留下的历史记录，**不代表当前进程在读**。
（这也是为什么这里本该有的一行诊断日志缺失 —— 见 ROADMAP P2-1。）

v2 的日志只有 `error.log`，且**只在异常时写入**。所以：

```powershell
Get-Content "$env:LOCALAPPDATA\MouseMeter\error.log" -Tail 20   # 空 / 不存在 = 没有异常
```

「有没有真读到」只能靠**看**：挂件上的数字、托盘图标上的百分比、或鼠标悬停时的 tooltip。
读不到最常见两个原因：厂商网页驱动开着（独占通道）；鼠标休眠中（摇一下鼠标）。

**③ 视觉没崩**

挂件和设置窗口都是自绘，**必须看截图**，不能只信代码。渲染脚本的做法：
启动进程 → 等待 → `PrintWindow` 或 `CopyFromScreen` → 存 PNG → 用图片读取工具看。
注意两点：

- 设置窗口可能被其他窗口压住，截图前先 `SetWindowPos(HWND_TOPMOST)` 再截
- 高分屏（本机 125%）下字体可能被缩放两次，检查数字有没有被裁切

---

## 6. 交接边界

**可以自由改：** `src/` 下任何文件、`docs/`、`README.md`、构建脚本。

**改之前先问用户：**

- 换技术栈（.NET 6+ / WPF / WinUI3 / Rust / Tauri）—— 这是重写，不是优化
- 引入任何第三方依赖或联网行为
- 改协议报文（`McHoseProvider`）—— 这是逆向出来的，改错就彻底读不到数
- 删掉 `receiverIds` 里的 ID（`0BDA:4762`、`2E3C:C365`）

**别碰：**

- `%APPDATA%\MouseMeter\settings.json` 里的用户配置（测试时用临时目录，别覆盖用户设置）
- 注册表 `HKCU\...\Run\MouseMeter`（用户自己开的自启）
- 把 `throw` 从 `Program.Main` 里去掉——异常会静默退出，极难排查

---

## 7. 下一步做什么

去读 `docs/ROADMAP.md`，里面有按优先级排好的 12 项任务，每项都带**验收标准**。
最该先做的三件事：

1. **P0-1 真毛玻璃**（当前是 96% 不透明度假装的，用户要的苹果感这里还差一截）
2. **P0-2 蓝牙 provider**（切到蓝牙模式就完全读不到，这是唯一的功能缺口）
3. **P1-1 打包成独立 exe**（现在依赖 PowerShell 宿主，分发和自启都不体面）

---

## 8. 文件清单

```
MouseMeter/
├─ START-HERE.md            ← 你正在读
├─ README.md                产品向说明（面向用户，可保留）
├─ dev.cmd              ★ 开发入口：双击即编译变更 + 重启
├─ DevRun.ps1           ★ dev.cmd 的内核：编译 → 停旧实例 → 本进程内启动
├─ Build.ps1                出包：重画图标 → 编译 dist\MouseMeter.exe
├─ MouseMeter.ps1           旧启动器：查哈希 → 编译/复用 → 加载并运行（仍可用）
├─ Compiler.ps1             合并 src/*.cs 为单文件 + CodeDOM 编译 + DLL 缓存
├─ run-hidden.vbs           静默启动入口（仅从源码运行时才需要）
├─ MouseMeter.ico           应用图标（也可由 IconFactory.WriteIco 重新生成）
├─ settings.sample.json     配置样例（字段含义见 docs/CONTEXT.md §5）
├─ dist/                    可分发产物：MouseMeter.exe + 使用说明
├─ src/                     18 个 .cs 源文件
├─ tests/                   TestRunner（<!-- AUTO:assertions -->88 项<!-- /AUTO -->断言）+ PreviewRenderer（离线渲染）
└─ docs/
   ├─ CONTEXT.md            ★ 完整背景、决策记录、踩坑史、用户偏好
   ├─ PROTOCOL.md           ★ HID 协议逆向全过程（报文逐字节表格）
   ├─ ARCHITECTURE.md       架构、类职责、线程模型、生命周期
   ├─ DESIGN.md             视觉规范（颜色/字体/尺寸/动效 token）
   ├─ ROADMAP.md            ★ 12 项优化任务 + 验收标准
   └─ screenshots/          5 张真实渲染截图
```
