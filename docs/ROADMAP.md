# ROADMAP — 优化任务清单

> 按优先级排。每项都有**验收标准**，做完能自己判断对没对。
> 「现状」写的是真实代码状态，不是理想。

---

## P0 — 值得优先做

### P0-1 真毛玻璃（Acrylic / Mica）—— 已实现（v4，待真机截图确认）

**做法**：三级材质，按系统能力自动选层级，全部集中在 `src/Material.cs`（纯函数，可测）+ `Dwm`（P/Invoke）。

| 层级 | 触发条件 | 实现 |
|---|---|---|
| `Solid`（0） | 滑块 100%，或系统「透明效果」关闭，或 Win10 < 1803 | 原来的实色卡片，`Form.Opacity` 换算不透明度 |
| `Backdrop`（2） | Win11 build ≥ 22621 | `DwmSetWindowAttribute(38, DWMSBT_TRANSIENTWINDOW)` |
| `Accent`（1） | Win10 build ≥ 17134 | `SetWindowCompositionAttribute(WCA_ACCENT_POLICY)` + `ACCENT_ENABLE_ACRYLICBLURBEHIND` |

关键决策（都是踩过的坑，改之前先读）：

- **`Region` 全部去掉**。`Window.Region` 由 USER32 在 DWM 之前裁剪，会把圆角切成硬边、还会在材质边缘留 1 px 缝。
  现在窗口保持矩形，圆角交给 `DwmSetWindowAttribute(33)`（合成器圆角），实色路径则用 `FillPath` 自己画轮廓。
- **`Form.Opacity` 只留给实色路径**。`WS_EX_LAYERED` 与 backdrop 同时存在时，材质被压在最底层，看起来是「灰白糊了一层」。
  模糊路径把 `Opacity` 固定在 1.0，透明度改由 tint alpha 表达。
- **`BackColor = Color.Transparent`** 只在模糊路径生效，否则自绘会盖掉要透出来的材质。
- **tint 双份**：DWM 的 tint（高字节 alpha）负责整体浓淡，GDI 再叠一层 `alpha × 0.55` 的卡面色，
  这样白字压在亮壁纸上仍然可读（对应原验收里的「深色挂件在浅色壁纸上仍要可读」）。
- **失败必须回退**：`Backdrop()` 返回 HRESULT，非 0 就当场降到 `Accent`；`Accent` 再不行就是实色。
  只判断版本号不够——有些显卡驱动接受属性但不出材质，那会得到一块全透明的窗口，比实色更糟。
- **`WM_THEMECHANGED` / `WM_DWMCOMPOSITIONCHANGED`** 里重新判定，用户中途开关「透明效果」不用重启。

滑块语义也变了：设置页显示「挂件材质 — 毛玻璃 强/中/弱 / 实色」，不再是容易误解的百分比。


**做法**（Windows 11 22H2+）：

```
DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ...)
  DWMSBT_TRANSIENTWINDOW = 3  → Acrylic（带噪点，最适合浮动挂件）
  DWMSBT_MAINWINDOW      = 2  → Mica（跟随壁纸，适合窗口）
```

配套要做：

- 窗口加 `WS_EX_LAYERED` 之外的合成方式要谨慎，`SystemBackdrop` 与 `Opacity` 可能冲突 → 半透明改用**自绘 alpha**，不要用 `Form.Opacity`
- 背景改为 `Color.Transparent` 或极低 alpha，让 DWM 材质透出来
- 需要 `DwmExtendFrameIntoClientArea(hwnd, { -1,-1,-1,-1 })` 才能让客户区吃到材质
- 系统版本 `< 22621` 时降级回当前实色方案（用 `Environment.OSVersion.Version.Build` 判断）
- **深色挂件在浅色壁纸上仍要可读** → 材质之外再叠一层 `rgba(0,0,0,0.28)` 保底

**验收对照**：

| 验收项 | 状态 |
|---|---|
| 挂件压在彩色壁纸/网页上能看到背景模糊 | ⏳ 需真机截图（本机无 Windows 桌面） |
| 关闭系统透明效果后不崩、自动降级实色 | ✅ 逻辑已实现 + `Material.Pick` 矩阵测试覆盖 |
| 截图对比可见差异 | ⏳ 同上 |
| 编译通过（C# 5，含测试） | ✅ `tools/check-syntax.ps1` 0 error，88/88 断言通过 |

**下一步（在 Windows 上）**：`dev.cmd` 起来 → 鼠标移到彩色壁纸/网页上截图 → 设置里把「挂件材质」从
「毛玻璃 强」拖到「实色」再截图 → 系统设置里关掉「透明效果」确认回落实色。三张图存进
`docs/screenshots/`，把上表两个 ⏳ 改成 ✅。

---

### P0-2 蓝牙 provider —— 已完成（v3，走的是 PnP 路线而非 BLE GATT）

**当前实现**：`SystemBatterySource` + `PnpBattery.cs`。
不走 `BluetoothAPIs.dll` 的 GATT 直读，而是从系统的电池/设备枚举接口取百分比——
Windows 自己已经跟 BLE 设备建立了 GATT 会话，我们复用它，省掉链路维护和连接状态判定。

**验收对照**：

| 验收项 | 状态 |
|---|---|
| ① 蓝牙模式能读到电量，`Transport == "BLE"` | ✅ `DeviceHeuristics.TransportOf()` 从设备路径识别 |
| ② 休眠时不显示假数据（>90s 降级） | ✅ `Freshness.IsStale()`，读数保留但淡显 |
| ③ 2.4G 优先，接收器插着时不走蓝牙 | ✅ `DeviceManager` 按来源合并，私有协议读到即覆盖系统值 |
| ④ 关掉蓝牙开关不崩 | ✅ `config.UseBle / UsePnp` 开关在来源构造时注入 |

**仍然缺的**：`Charging` 字段。系统电池接口不报充电状态，所以蓝牙设备只能显示电量、显示不出「充电中」。
要补这一项才需要走 GATT 直读（原方案）。

---

### P0-3 打包成独立 exe

**现状**：分发 = 一个目录 + `wscript` 拉起 PowerShell。启动要经过 PowerShell 宿主（约 120~200ms 额外开销），
杀毒软件偶尔会拦「隐藏窗口的 PowerShell」，开机自启也依赖 `.vbs` 这种老技术。

**三条路子**：

| 方案 | 优点 | 代价 |
|---|---|---|
| **A. 迁 .NET 8 + WPF/WinUI3，`PublishSingleFile`** | 真原生 exe、能上 WinRT（解锁 P0-2 路线 A）、真 Acrylic 开箱即用、可用现代 C# | **等于重写 UI 层**（3 千行），必须用户点头 |
| **B. 保持 .NET Framework，用 `csc.exe` 直接编 exe** | 改动最小，源码几乎不动 | 上一轮环境 `csc` 被安全策略拦了；换环境后大概率可行 |
| **C. 保留现状，只把 `.vbs` 换成 exe 快捷方式** | 零成本 | 治标不治本 |

**推荐顺序：先试 B，B 不行再问用户要不要 A。**

方案 B 具体做法：

```
csc.exe /target:winexe /out:MouseMeter.exe /optimize+ /win32icon:MouseMeter.ico
        /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
        <合并后的单文件 .cs>
```

然后 `MouseMeter.ps1` 和 `Compiler.ps1` 就可以删掉（或者保留为「开发模式」）。

**验收**：双击一个 `MouseMeter.exe` 就能起来（无 PowerShell 窗口闪一下）；开机自启项指向 exe 而不是 wscript；`Program.Main` 的 `--settings` 参数仍然可用；exe 体积可接受（.NET Framework 依赖系统已有，应该 < 1 MB）。

**注意**：迁到 .NET 8 后，`CodeDOM` 那套编译缓存要整块删掉——它的存在理由就消失了。

---

## P1 — 体验提升

### P1-1 多设备支持 —— 已完成（v3）

**当前实现**：`DeviceManager` 收集所有 `IBatterySource` 的 `List<Reading>`，按 `Id` 合并去重后排序
（鼠标优先，其余按连接方式），挂件每行一个设备，最多显示 3 行，超出可用滚轮翻页（页脚显示 `1–3 / 4`）。

| 验收项 | 状态 |
|---|---|
| 插两个设备能看到两条读数 | ✅ 挂件按行展开，高度随设备数增长 |
| 拔掉其中一个，另一个不受影响 | ✅ 每个设备独立 `attempt budget`，互不拖累 |

**后续可做**：超过 3 台时改成横向卡片，或把行高压缩到 52 px 塞 4 行。
现在用的是滚动，好处是不改布局、坏处是不易发现——页脚提示已经补上。

### P1-2 电量历史

**现状**：只存最后一次读数，无历史。

**要做**：在 `%LOCALAPPDATA%\MouseMeter\history.csv` 追加 `timestamp,level,charging`（每 60s 一条，一天 1440 行，一年 ~5 MB，可接受）。
设置窗口加一个小折线图（自绘，用 `GraphicsPath` + 半透明渐变填充）。

**验收**：能看到过去 24 小时的曲线；切换设备/重启后曲线连续；文件超过 2 MB 时自动按天滚动（保留最近 30 天）。

### P1-3 自适应轮询

**现状**：固定 60 秒。鼠标休眠时白白重试 8 次（约 1.6 秒阻塞工作线程）。

**要做**：按状态调频率

| 状态 | 间隔 |
|---|---|
| 充电中 | 30 s（要尽快看到充满） |
| 电量 ≤ 阈值 | 30 s |
| 正常 | 60 s |
| 连续 3 次失败（休眠/拔掉） | 180 s，且失败超过 10 分钟后不再重试，直到检测到 `WM_DEVICECHANGE` 或鼠标移动事件 |

**验收**：拔掉接收器后 CPU 占用下降；插回后 3 秒内自动恢复显示（不需要手动刷新通过）。

### P1-4 左键点击面板

**现状**：左键单击托盘图标无反应，双击才开设置。这是 WinForms `NotifyIcon` 的常见困惑点。

**要做**：左键单击弹出一个**类似 macOS 控制中心的小面板**（无边框、圆角、深色、带 Acrylic）：
大号电量环 + 设备名 + 连接方式 + 三个快捷开关（挂件显隐 / 立即刷新 / 设置）。

**验收**：点一下就在托盘附近弹出，点面板外任意处自动关闭；Esc 关闭。

---

## P2 — 工程健壮性

### P2-1 无宿主依赖的日志

**现状**：`Program.Log()` 只写 `error.log`，且**只在异常时写**，无轮转（长期跑会一直变大，虽然通常很小）。

**还缺一件事**：**没有轮询日志**。排查「到底读到没有」只能靠肉眼截图，效率很低。
`%LOCALAPPDATA%\MouseMeter\log.txt` 里那些 `ok level=29 charging=0` 是 **v1 遗留脚本**写的，v2 完全不碰这个文件——
接手时很容易被它误导（以为在读，其实进程早没了）。

**要做**：

1. 删掉/忽略遗留的 `log.txt`，不要复用它
2. 加 `--verbose` 启动参数：开启后每次轮询写一行
   `2026-10-20 18:20:01 poll provider=MCHOSE 2.4G ok level=36 charge=0 elapsed=74ms`
   失败时写 `reason=err31(休眠)` / `reason=err87(report id)` / `reason=no collection`
3. 日志分级（info/warn/error）+ 按大小轮转（1 MB × 3）
4. 设置窗口的「复制诊断信息」按钮确保包含：接收器 ID、provider 名、`Polls`/`Hits`、最后一次读数时间、`GetLastError`

**验收**：加上 `--verbose` 后能仅凭日志判断出「没读到」的具体原因（四种原因可区分，见 CONTEXT.md §7.3）；不开 verbose 时零日志开销。

### P2-2 设置窗口拆分

**现状**：`SettingsForm.cs` 669 行，混了布局常量、绘制、事件绑定三件事。

**要做**：拆成 `SettingsForm.cs`（生命周期与事件）+ `SettingsForm.Layout.cs`（尺寸常量与控件定位）+ `SettingsForm.Paint.cs`（绘制）。
纯重构，行为不变。**验收**：重构前后截图逐像素一致（可以写个对比脚本）。

### P2-3 单元测试

**现状**：零测试，全靠截图人工验收。

**可测且值得测的部分**（不依赖 UI、不依赖真硬件）：
- `MiniJson` 的解析与序列化（含畸形输入）
- `IconFactory.StateOf()` 的状态判定矩阵
- `McHoseProvider` 的解析逻辑 —— 把「字节数组 → Reading」抽成纯函数 `ParseReply(byte[] reply)`，就能用真实抓到的回包做断言
- `Theme.SystemUsesLight()` 的降级行为

**验收**：`ParseReply` 对实测回包 `12 F9 FE FF 9D B8 FF FF FF FD F6 DB FF ...` 返回 `Level=36, Charging=false`。

> 注意：如果继续用 CodeDOM 构建（C# 5），测试框架只能用最朴素的 `static void Main` 断言，没法上 xUnit。
> 迁到 .NET 8（P0-3 方案 A）后才有正常测试条件。

### P2-4 i18n

**现状**：所有文案硬编码中文。

**要做**：抽一个 `Strings` 静态类（C# 5 下不能用资源文件生成器，手写字典即可），中/英两套。
检测 `CultureInfo.CurrentUICulture`。**验收**：系统语言切英文时全部 UI 变英文，没有漏网的硬编码。

### P2-5 自动更新

**现状**：`Settings.CheckUpdates` 字段存在但**完全没被使用**（死字段）。

**要做**：要么删掉这个字段，要么真做——最有性价比的是「检查 GitHub Releases 的 tag，比本地版本新就弹个通知 + 打开下载页」。
**不要**做静默自替换（会踩文件占用、权限、签名一堆坑）。

---

## 已知但暂不打算做的

| 项 | 原因 |
|---|---|
| 支持 macOS / Linux | 协议层是 Windows HID + setupapi，等于重写 |
| 支持有线模式 | 有线时电量意义不大（一直在充），且报文不同 |
| 托盘图标支持右键快捷调阈值 | 已有设置窗口，再加一层交互反而乱 |
| 系统级深色模式跟随的实时切换 | 需要监听 `WM_SETTINGCHANGE`，成本低于收益，暂缓 |

---

## 建议的动手顺序

```
1. ~~P0-3 方案 B（编 exe）~~      ← 已完成
2. ~~P0-1 真毛玻璃~~              ← 代码完成，只差真机截图
3. P1-1 左键面板                ← 一天，壳子做好后挂件可以顺便复用材质
4. ~~P0-2 蓝牙 provider~~         ← 已完成（PnP 路线）
5. P1-3 自适应轮询              ← 半天，降低后台开销
6. 其余按需
```

**每一步做完都要截图给用户看**，不要一口气改完再汇报——这是个审美驱动的项目，方向偏了要及时纠。
