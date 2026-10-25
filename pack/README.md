# pack\ —— 用 dotnet SDK 编译同一个 exe

`Build.ps1` 走 CodeDOM，而 CodeDOM 只存在于 Windows PowerShell 5.1。换台机器（或 CI）就没有第二条出包路径了。`pack\` 补上这条：**同样的 `src\*.cs`，同样产出 .NET Framework 4.8 的 WinExe**，但用 `dotnet build`。

## 构建

```powershell
dotnet build pack\MouseMeter.Sdk.csproj -c Release
```

产物在 `pack\bin\Release\net48\`：

| 文件 | 说明 |
|---|---|
| `MouseMeter.exe` | 138 KB，单文件，托管代码 |
| `MouseMeter.exe.config` | 169 字节，声明 `supportedRuntime v4.0 / sku=.NETFramework,Version=v4.8` |

两个文件放在同一目录即可运行。`.config` 不要丢——它告诉 CLR 这个程序要 4.8 运行时。

## 和 Build.ps1 的关系

**故意保持一致的两点**，否则这次构建就失去意义：

- `LangVersion=5`。CodeDOM 只认 C# 5，若这里放开到 C# 6+，本地绿了真机照样报错。
- **编译 `src\AssemblyInfo.cs`，不生成**。版本号 `3.0.0.0` 和那句中文 `AssemblyDescription` 只来自这个文件。`GenerateAssemblyInfo=false` 若不配合 `GenerateTargetFrameworkAttribute=false`，SDK 会插一份 `0.0.0.0` 的空壳，Explorer 属性页里就是空白——这个坑踩过。

**故意不同的两点**，都因为工程文件在 `pack\` 而不是根目录：

- Compile / Icon 路径上跳一级（`..\src\*.cs`、`..\MouseMeter.ico`）。
- 图标通过 `IconResource.cs` 里的字节字面量嵌入，而不是作为资源编译。这样 exe 一定带图标，不依赖资源节是怎么打进去的。

## 已验证

| 项目 | 结果 |
|---|---|
| PE 子系统 | `IMAGE_SUBSYSTEM_WINDOWS_GUI`（双击不弹控制台） |
| 资源节 | `RT_ICON` / `RT_GROUP_ICON` / `RT_VERSION` / `RT_MANIFEST` 四项齐全 |
| 版本信息 | `ProductName=MouseMeter`、`FileVersion=3.0.0.0`、中文 Comments |
| 清单 | `asInvoker`、perMonitorV2 DPI、Win10/11 `supportedOS` |
| 测试 | 88 项断言全通过（同一套 `tests\TestRunner.cs`，`net48` 目标） |

> `RT_MANIFEST` 由 `ApplicationManifest` 写入，`RT_MANIFEST` 资源同时携带 `asInvoker`
> 与 DPI 声明——这两项和 Build.ps1 走 CodeDOM 时的默认行为一致。

## 已知差异

- **体积**：SDK 版 138,752 字节，CodeDOM 版 117,760 字节。差异来自编译器版本与元数据布局，功能一致。
- **校验和**：两个编译器产物字节不同，`dotnet` 侧是确定性构建（同输入同输出），CodeDOM 侧不具备这一保证。
- **.NET 8 SDK 需要额外引用包**：`net48` 目标的引用程序集（`Microsoft.NETFramework.ReferenceAssemblies`）在非 Windows 上不会自动落盘，首次构建需要联网 restore。该包由 SDK 自动拉取，前提是能访问 NuGet。

## 未验证

**没有在 Windows 真机上双击运行过。** 这个环境是 Linux，只做到了语法与目标框架层面的等价（`net48` + C# 5 + 四个白名单程序集）。运行时行为——挂件的毛玻璃、托盘图标、HID 读取——必须由真机确认。若 `Build.ps1` 在本机可用，优先用它的产物。
