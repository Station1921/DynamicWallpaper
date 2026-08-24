using System.Reflection;

// GenerateAssemblyInfo=false 关闭了 SDK 自动生成 AssemblyInfo，
// 必须手动声明程序集版本，否则程序集版本为 0.0.0.0，
// 与 WPF XAML 编译写入 BAML 的 1.0.0.0 引用不匹配，
// 导致 LoadComponent -> Assembly.Load 抛 FileNotFoundException、主窗口白屏。
[assembly: AssemblyTitle("动态桌面")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0.0")]
