# ARCHITECTURE — 架构与实现细节

---

## 1. 进程模型

**单进程、单线程 UI + 线程池轮询，无服务、无驱动、无管理员权限。**

```
wscript.exe  (run-hidden.vbs)
    └─ powershell.exe -WindowStyle Hidden -File MouseMeter.ps1
          └─ 加载编译好的 DLL，同进程内 [MouseMeter.Program]::Main()
                └─ WinForms 消息循环（单 UI 线程）
                      ├─ NotifyIcon（托盘）
                      ├─ HudForm（无边框置顶窗口）
                      ├─ SettingsForm（按需创建）
                      └─ ThreadPool 上的轮询线程（每个 tick 一条）
```

- **不需要管理员权限**：HID 特征报文读写对普通用户开放
- **不安装任何东西**：注册表只写 `HKCU\...\Run` 一个值
- **单实例**：命名 Mutex `MouseMeter.SingleInstance.v2`，第二个实例直接 `return`

### 编译与加载

`MouseMeter.ps1` 只做三件事：

```powershell
. Compiler.ps1
$build = Build-MouseMeter -Root $root     # 合并 → 哈希 → 命中缓存或重新编译
[void][System.Reflection.Assembly]::LoadFrom($build.Path)
[MouseMeter.Program]::Main([string[]]$argv)
```

`Compiler.ps1` 的关键设计：

1. **合并**：把 `src/*.cs`（按文件名排序）合成一个编译单元。逐行扫描，把所有 `using X;` **提到文件最顶部**并去重，其余内容追加。
   ——不做这一步，第二个文件的 `using` 会出现在命名空间之后，编译直接失败（上一轮踩过）。
2. **缓存**：对合并结果算 SHA256，取前 16 位做 DLL 文件名 → `%LOCALAPPDATA%\MouseMeter\cache\MouseMeter-<hash>.dll`。命中就直接返回，14 ms。
3. **清理**：只保留最新 2 个 DLL，避免无限堆积。
4. **引用集**：`System.dll`、`System.Core.dll`、`System.Drawing.dll`、`System.Windows.Forms.dll` —— 就这四个。

> **C# 语言等级 = 5**（CodeDOM 默认）。见 `START-HERE.md §2.1`。

---

## 2. 类职责表

### 数据层

| 类型 | 位置 | 职责 |
|---|---|---|
| `HidCollection` | Hid.cs:10 | 一个 HID 顶层集合的元数据：Path / Vid / Pid / UsagePage / Usage / FeatureLength / ProductName / Manufacturer，`Id` 返回 `"XXXX:YYYY"` |
| `Hid`（static） | Hid.cs:26 | P/Invoke 封装：`Enumerate()` 枚举全部集合、`Open(path, write)` 拿句柄、`SetFeature()` / `GetFeature(h, len, reportId)` |
| `Reading`（struct） | Providers.cs:7 | 一次读数：Level / Charging / DeviceName / Transport / HardwareId / At |
| `IBatteryProvider` | Providers.cs:23 | 扩展点。`Name` / `IsPresent()` / `TryRead(out Reading)` |
| `McHoseProvider` | Providers.cs:38 | 迈从实现。`FindCollection()` 定位集合，`TryRead()` 执行 8 轮重试 |
| `DeviceManager` | Providers.cs:149 | 持有 provider 列表，`Refresh()` 按顺序尝试；维护「最后一次有效读数」、`IsStale`、统计 `Polls`/`Hits` |

### 配置层

| 类型 | 位置 | 职责 |
|---|---|---|
| `Settings` | Settings.cs:13 | 全部配置字段 + `Load()` / `Save()`，路径 `%APPDATA%\MouseMeter\settings.json` |
| `MiniJson` | Settings.cs:110 | 手写 JSON。只支持**扁平对象** + 字符串数组，够用且零依赖。解析失败静默回退默认值 |

### 视觉层

| 类型 | 位置 | 职责 |
|---|---|---|
| `Palette` | Theme.cs:12 | 一组颜色 token + `IsDark` |
| `Theme`（static） | Theme.cs:30 | `Light()` / `Dark()` / `For(mode)` / `SystemUsesLight()`（读注册表 `SystemUsesLightTheme`）；字体族选择与缓存 `F(pt, bold)` / `FC(pt, bold)`；`RoundRect()` 圆角路径；DWM 特效（圆角、深色标题栏） |
| `IconFactory`（static） | IconFactory.cs:14 | `State` 枚举（Normal/Low/Critical/Charging/Offline）、`StateOf()` 决策、`Render()` 逐像素绘制、`ToIcon()` 转 HICON、`WriteIco()` 生成多分辨率 .ico |
| `Controls.cs` | 9~ | 四个自绘控件：`ToggleSwitch`（iOS 开关，带弹性动画）、`MMSlider`、`Segmented`、`FlatButton`，加 `Card` 静态绘制助手（卡片底 + 分隔线） |
| `AppleMenuRenderer` | MenuRenderer.cs:9 | 继承 `ToolStripRenderer`，把原生右键菜单改成圆角 + 苹果配色 |
| `MenuBuilder`（static） | MenuRenderer.cs:110 | `Create()` / `Item()` / `Caption()` / `Sep()` 快捷构造 |

### 窗口层

| 类型 | 位置 | 职责 |
|---|---|---|
| `HudForm` | HudForm.cs:13 | 桌面挂件。逻辑尺寸 **178×112**，`Height * 0.175` 圆角，`TopMost`、`ShowInTaskbar=false`、`AutoScaleMode.None`（自己按 DPI 算）。拖动移动、双击进设置、右键菜单、悬停微动效 |
| `SettingsForm` | SettingsForm.cs:10 | 设置窗口。无边框 + 自绘标题栏，卡片式分组，全自绘控件。`Changed` 事件通知外部应用配置，`SyncFromConfig()` / `SyncReading()` 反向同步 |
| `TrayContext` | Program.cs:123 | 继承 `ApplicationContext`，是整个应用的**协调者**：托盘、轮询、挂件生命周期、设置联动、低电量通知、退出清理 |
| `AutoStart`（static） | Program.cs:11 | 读写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `MouseMeter` 值 |
| `Program`（static） | Program.cs:54 | 入口。Mutex、DPI 感知、全局异常钩子、配置加载、`--settings` 参数 |

---

## 3. 线程模型（最容易改错的地方）

```
UI 线程（STA）                       ThreadPool
─────────────────                  ─────────────────────
TrayContext 构造
  └─ _dm.Refresh()  ← 同步！首次读一次，避免挂件闪「未检测到」

Timer(period) ──── 每 60s ─────►  Poll()
                                    ├─ if (_polling) return;  ← 防重入
                                    ├─ _polling = true
                                    ├─ _dm.Refresh()   ← 阻塞，最多 ~1.6s
                                    ├─ _polling = false
                                    └─ Post(OnPolled)
                                            │
                    _marshal.BeginInvoke ───┘
                          │
  OnPolled()  ←───────────┘
    ├─ UpdateTray()            重绘图标 + 更新 tooltip
    ├─ _hud.UpdateReading()    挂件 Invalidate
    ├─ _form.SyncReading()     设置窗口刷新
    └─ 低电量判定 → ShowBalloonTip
```

**三条铁律：**

1. **HID 操作绝不在 UI 线程做**（除了构造时那一次同步读，因为它决定了首帧内容）。
2. **跨线程更新 UI 必须走 `_marshal.BeginInvoke`**。`_marshal` 是个隐藏 `Control`，构造时 `IntPtr handle = _marshal.Handle;` 强制创建句柄。
   **不要改成 `SynchronizationContext.Current`** —— `ApplicationContext` 构造时它还是 `null`，会导致回调根本发不出去。
3. **`_polling` 用 `volatile bool`** 做重入保护，避免慢轮询叠加。

---

## 4. 生命周期

### 启动

1. `Main` 拿 Mutex → 已存在则直接返回
2. `SetProcessDpiAwarenessContext(-4)` = Per-Monitor V2
3. 挂全局异常钩子（`Application.ThreadException` + `AppDomain.UnhandledException` → 写 `error.log`）
4. `Settings.Load()`，异常则用默认值
5. `cfg.StartWithWindows && !AutoStart.IsEnabled` → 补写注册表（保证装到新路径后自启仍有效）
6. `new TrayContext(cfg)`：
   - 建 `_marshal` 句柄
   - 建 `NotifyIcon` + 右键菜单
   - **同步 `Refresh()` 一次**
   - `UpdateTray()`，若 `hudEnabled` 则 `ShowHud()`
   - `StartTimer()`
7. 若带 `--settings` 参数 → `OpenSettings()`
8. `Application.Run(ctx)`

### 退出

`Quit()` 顺序固定，错一步就可能残留托盘图标（幽灵图标）：

```csharp
_cfg.Save();
_timer.Dispose();
_hud.Close(); _hud.Dispose();
_form.Dispose();
_ni.Visible = false;   // ← 必须先隐藏再 Dispose，否则图标会卡在托盘里
_ni.Dispose();
_icon.Dispose();
_marshal.Dispose();
ExitThread();
```

---

## 5. 托盘图标绘制

不是缩放位图，而是**按系统请求的真实尺寸重绘**：

```csharp
int size = SystemInformation.SmallIconSize.Width;   // 125% DPI 下通常 20，150% 下 24
using (var bmp = IconFactory.Render(size, level, state, darkTaskbar))
    _ni.Icon = IconFactory.ToIcon(bmp);
```

构成：**数字居中 + 底部一条细进度条**（高度 `max(1.6, size/11)`），无背景圆底（两位数）/ 三位数改方形浅底。

状态色：

| State | 触发 | 颜色 |
|---|---|---|
| `Normal` | 100 > level > lowThreshold | 跟随任务栏深浅（深底用 #F5F5F7，浅底用 #1C1C1E） |
| `Low` | level ≤ lowThreshold | 橙 `#FF9F0A` |
| `Critical` | level ≤ 10 | 红 `#FF453A` |
| `Charging` | charging=true | 绿 `#30D158` |
| `Offline` | 无有效读数 | 灰（40% 透明）+ 显示 `--` |

`darkTaskbar` 来自 `!Theme.SystemUsesLight()` —— 注意这里读的是**系统主题**（决定任务栏颜色），不是应用主题。

---

## 6. 挂件实现要点

- 尺寸：`178 × 112` 逻辑单位 × `hudScale` × `dpi/96`
- 圆角：`Height * 0.175`，用 `Region` 裁剪窗口（**不是**画一个假圆角底）
- 内容布局（从上到下）：设备名（小字灰）→ 大号百分比 + `%` 上标 → 右下角传输方式/充电状态 → 底部细进度条
- 拖动：`MouseDown` 记录偏移，`MouseMove` 改 `Location`，`MouseUp` 后写回 `cfg.HudX/HudY` 并 `Save()`
- 位置记忆：`cfg.HudX < 0` 时首次摆到工作区右下角（距右 48、距下 150，给任务栏留位），并 `ClampToScreen()` 保证不出屏
- stale 处理：超 90 秒没新读数 → 数字与进度条降透明度，右下角写「已休眠」，**绝不清空**

---

## 7. 扩展点

### 新增品牌（推荐路径）

```csharp
// 1) src/Providers.cs
public class LogitechBoltProvider : IBatteryProvider
{
    public string Name { get { return "Logitech Bolt"; } }
    public bool IsPresent() { /* 找 collection */ }
    public bool TryRead(out Reading r) { /* 发报文、解析 */ }
}

// 2) DeviceManager 构造函数里加一行
_providers.Add(new LogitechBoltProvider());
```

`DeviceManager.Refresh()` 按注册顺序逐个 `TryRead`，**第一个成功的生效**。
所以全新品牌可以无风险地追加到末尾，不影响现有迈从路径。

### 新增接收器 ID（零代码）

`%APPDATA%\MouseMeter\settings.json`：

```json
"receiverIds": ["0BDA:4762", "2E3C:C365", "046D:C548"]
```

### 新增 UI 控件

继承 `Control`，`Palette` 用属性注入（不要单例），全部在 `OnPaint` 里自绘，
`SetStyle(ControlStyles.OptimizedDoubleBuffer | AllPaintingInWmPaint | UserPaint, true)`。

---

## 8. 代码规模

```
src/Providers.cs      205    协议与调度
src/Hid.cs            180    P/Invoke 层
src/Program.cs        428    入口 + 托盘 + 轮询
src/HudForm.cs        369    桌面挂件
src/SettingsForm.cs   564    设置窗口
src/Theme.cs          273    视觉 token
src/IconFactory.cs    254    图标绘制
src/Controls.cs       402    自绘控件
src/MenuRenderer.cs   152    菜单渲染
src/Settings.cs       228    配置 + MiniJson
──────────────────────────
合计                 3055    （另 Compiler.ps1 78 + MouseMeter.ps1 45）
```

单文件最大 <!-- AUTO:largest -->Program.cs 734 行<!-- /AUTO -->，如果要继续扩，优先把它的布局代码和绘制代码拆开。
