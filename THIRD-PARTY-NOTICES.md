# 第三方来源与分发范围

本文件记录当前仓库中主要第三方组件的来源与边界。项目尚未指定统一的项目级开源许可证；不得以某一依赖的 MIT / Apache 许可代替整个仓库的授权声明。

## NuGet 依赖

依赖版本固定在 `src/Directory.Packages.props`，由 NuGet 还原。本仓库不提交还原缓存或第三方程序集。以下许可表达式核对自对应版本 `.nuspec`：

| 包 | 固定版本 | 许可 |
| --- | --- | --- |
| StreamJsonRpc | 2.20.20 | MIT |
| Serilog | 4.1.0 | Apache-2.0 |
| Serilog.Sinks.File | 6.0.0 | Apache-2.0 |
| xUnit | 2.9.2 | Apache-2.0 |
| FluentAssertions（测试依赖） | 6.12.2 | Apache-2.0 |

完整依赖及传递依赖以还原生成的 `project.assets.json` 和各自包内许可为准；后续版本须重新核对。

## TURZX 屏幕图像传输

独立 C# 实现的协议来源、固定上游 commit 和 MIT 原文见 `src/UnifiedRgb.Screens/THIRD-PARTY-NOTICES.md`。不包含上游的存储、视频、亮度或设备固件操作实现。

## PawnIO.Modules

`tools/pawnio/SmbusPIIX4.bin` 来自官方 PawnIO.Modules 0.2.11，SHA256 固定。保留对应 `SmbusPIIX4.p` 源文件、`COPYING`、`SHA256SUMS` 和 `PROVENANCE.md`，模块许可为 LGPL-2.1-or-later。来源及对应构建文件链接见该目录。

PawnIO 系统驱动和 PawnIOLib.dll 是独立安装的依赖，不在本仓库分发。

## 联力压缩组件与协议研究材料

现有联力循环压缩适配器支持加载外部 x64 `yuz.dll`。这份 DLL 及厂商程序集、安装包、原始反编译文档和实机配置不包含在公开仓库中。代码中保留协议事实与既有固定参考数据的来源说明，不应据此认定取得厂商软件的再分发授权。

相关私有资源的存在不会作为公开构建前提；需要它们的本机测试明确跳过。新的可再分发压缩实现尚未作为已完成替代品发布。

## 图形与场景素材

公开示例为通用极光场景；测试中的完整机箱包由 `tests/Fixtures/generate_scene_package.py` 通过合成 RGB / GIF 生成，不含用户图片、私有主题或设备绑定。本机主题制作目录和原始照片不随本仓库上传。
