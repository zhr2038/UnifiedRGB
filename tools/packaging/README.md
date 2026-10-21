# Windows 安装包

构建机使用 global.json 指定的 .NET 10 SDK，以及固定 SHA256 的 NSIS 3.11（构建工具，zlib 压缩）。
从仓库根目录运行 PowerShell 7：

```powershell
dotnet build src/UnifiedRgb.sln -c Release -warnaserror
dotnet test src/UnifiedRgb.sln -c Release --no-build -m:1
./tools/packaging/Build-Windows.ps1 -Output .artifacts-windows-release
```

生成的 EXE 是 Windows x64 自包含安装程序，无需另装 .NET。安装时由用户确认 Windows UAC；不会关闭或自动代点。
默认安装到 Program Files，下设带校验清单的版本目录；升级保留 appsettings 的完整字节及 ProgramData 中的场景、素材和本机设置。
界面未退出时拒绝升级，保护草稿。卸载保留用户数据及未识别、被修改的文件。
开始菜单提供光域、设备配置和卸载入口。设备配置仅针对本机，默认演示模式，不自动停止或卸载厂商程序。
设备驱动与 PawnIO/PawnIOLib 属于外部前提，不在此包内分发。没有代码签名证书时，安装包是未签名的，不能声称通过 SmartScreen 信誉认证。

NSIS portable URL: https://downloads.sourceforge.net/project/nsis/NSIS%203/3.11/nsis-3.11.zip?download=1

SHA256: C7D27F780DDB6CFFB4730138CD1591E841F4B7EDB155856901CDF5F214394FA1

许可证：https://nsis.sourceforge.io/License 。不使用 LZMA 插件或外部插件。
