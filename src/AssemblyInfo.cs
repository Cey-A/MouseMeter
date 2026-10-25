using System.Reflection;

// Shown in Explorer's Properties → Details for MouseMeter.exe.
// CodeDOM has no csproj to read a version from, so this file is the single source of truth;
// keep it in step with the "3.0" badge in SettingsForm.
//
// Note: CodeDOM derives the assembly *identity* from the output file name (MouseMeter.exe),
// so there is no AssemblyName attribute here — adding one would be ignored.
[assembly: AssemblyTitle("MouseMeter")]
[assembly: AssemblyDescription("桌面外设电量挂件：直读 2.4G 接收器 HID 报文，并汇总系统电池接口")]
[assembly: AssemblyProduct("MouseMeter")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyVersion("3.0.0.0")]
[assembly: AssemblyFileVersion("3.0.0.0")]
