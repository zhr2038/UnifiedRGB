# 第三方来源与分发范围

本文件记录当前仓库中主要第三方组件的来源与边界。项目尚未指定统一的项目级开源许可证；不得以某一依赖的 MIT / Apache 许可代替整个仓库的授权声明。

## NuGet 依赖

依赖版本固定在 `src/Directory.Packages.props`，由 NuGet 还原。本仓库不提交还原缓存或第三方程序集。以下许可表达式核对自对应版本 `.nuspec`：

| 包 | 固定版本 | 许可 |
| --- | --- | --- |
| StreamJsonRpc | 2.26.150 | MIT |
| MessagePack / MessagePack.Annotations | 2.5.307 | MIT |
| Serilog | 4.3.0 | Apache-2.0 |
| Microsoft.Extensions / ServiceController | 10.0.13 | MIT |
| Serilog.Extensions.Hosting / Configuration | 10.0.0 / 10.0.1 | Apache-2.0 |
| Serilog.Sinks.File | 6.0.0 | Apache-2.0 |
| xUnit | 2.9.2 | Apache-2.0 |
| FluentAssertions（测试依赖） | 6.12.2 | Apache-2.0 |
| Vortice.MediaFoundation / Vortice.DirectX | 3.6.2 | MIT |
| Vortice.Mathematics | 1.9.2 | MIT |
| SharpGen.Runtime / SharpGen.Runtime.COM（传递依赖） | 2.2.0-beta | MIT |

完整依赖及传递依赖以还原生成的 `project.assets.json` 和各自包内许可为准；后续版本须重新核对。

## TURZX 屏幕图像传输

独立 C# 实现的图片传输、易失背光控制和类型化 H.264 流协议来源、固定上游 commit 与 MIT 原文见 `src/UnifiedRgb.Screens/THIRD-PARTY-NOTICES.md`。不包含存储、持久设备设置、重置或固件写入操作。Windows Media Foundation 使用系统媒体组件；Vortice/SharpGen 许可文本随程序复制，不打包 FFmpeg 或厂商媒体 DLL。SharpGen 的固定传递版本为 beta，具体兼容性仍以本轮回归和设备验收为准。

## PawnIO.Modules

`tools/pawnio/SmbusPIIX4.bin` 来自官方 PawnIO.Modules 0.2.11，SHA256 固定。保留对应 `SmbusPIIX4.p` 源文件、`COPYING`、`SHA256SUMS` 和 `PROVENANCE.md`，模块许可为 LGPL-2.1-or-later。来源及对应构建文件链接见该目录。

安装载荷还附带同一固定源码 commit 的 `sleepmode.inc` 与 `include/`，其中 PawnIO Headers 保留 0BSD 原始声明。不把模块及头文件的授权表述为对整个应用的授权。

PawnIO 系统驱动和 PawnIOLib.dll 是独立安装的依赖，不在本仓库分发。

## 联力压缩组件与协议研究材料

默认路径是 `ManagedTinyuzEncoder`：独立托管 C# 匹配搜索、贪心/延迟双路径选择和上传前无损自检。线格式、变长整数和受检解码逻辑参考/移植自 [sisong/tinyuz](https://github.com/sisong/tinyuz) **v1.1.1**，固定 commit `1d74ffa4d453796df352df470733f45dfa099bb1`，MIT。参考源为 `compress/tuz_enc_private/tuz_enc_code.cpp`、`decompress/tuz_dec.c`、`decompress/tuz_types_private.h`；源文件版权为 HouSisong / housisong，2012–2025，仓库 LICENSE 原文为2012–2022。

完整许可见 `src/UnifiedRgb.Devices.LianLi/Codec/tinyuz-LICENSE.txt`，构建/发布自动复制为 `Licenses/tinyuz-LICENSE.txt`。托管运行路径不需要 HDiffPatch、C/C++工具链、厂商二进制或运行时下载。不得将上游算法/解码逻辑认作本项目完全自行发明。

可选 `VendorYuz` 仅在明确配置时加载外部 x64 `yuz.dll`，没有目录或注册表自动发现。这份 DLL 及厂商程序集、安装包、原始反编译文档和实机配置不包含在公开仓库中，不据此认定取得厂商软件的再分发授权。额外原厂对照及历史实机资料测试缺少私有资源时明确跳过；默认压缩器和通用预算回归始终执行。

## 图形与场景素材

公开示例为通用极光场景；测试中的完整机箱包由 `tests/Fixtures/generate_scene_package.py` 通过合成 RGB / GIF 生成，不含用户图片、私有主题或设备绑定。本机主题制作目录和原始照片不随本仓库上传。

## .NET 自包含载荷与安装器

Windows 安装包包含 .NET 10 / WindowsDesktop 运行库，不包含 SDK。`DEPENDENCIES.json`、`Licenses/NuGet/` 中的原始包元数据、许可和通知随安装载荷分发；不把这些库称为项目原创代码。
安装器使用 NSIS 3.11 的标准 MUI2 / nsExec 与 zlib 压缩，相关 zlib/libpng 许可全文随包保留在 `Licenses/NSIS/COPYING`。NSIS 是构建工具，不是运行程序的外部前提。

历史厂商动画体及采样表的移除、兼容标识与新数学内容的界限见 [SOURCE-PROVENANCE.md](SOURCE-PROVENANCE.md)。不声称 clean room，不重写已有 Git 历史。
