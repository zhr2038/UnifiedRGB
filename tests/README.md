# 回归测试

在 Windows x64 / .NET 8 SDK 环境从仓库根目录执行：

```powershell
dotnet build src/UnifiedRgb.sln -c Release -warnaserror
dotnet test src/UnifiedRgb.sln -c Release --no-build -m:1
```

五个测试项目分别覆盖 Core、Devices、Safety、Host 和 Screens。测试默认使用 fake 传输和合成数据，不启动第二个真实设备后台。

`-m:1` 将测试程序集串行执行，减少实时调度测试与其他程序集的 WPF 编码负载相互干扰；没有放宽计时阈值或像素断言。

`Golden` 为效果帧固定参考数据；`Fixtures/usbpcap` 的 `.rec` 是项目构建器产生的协议样例，目录说明已明确其不是原始抓包；OpenRGB/NVIDIA 样例同样用于协议回归。

场景包回归中的 `generic-two-role-v1.zip` 是通用小样例，`generic-nine-five-v1.zip` 为9条灯光轨道、5个动画屏幕、精确12秒的合成数据。后者可用标准库脚本重建：

```powershell
python tests/Fixtures/generate_scene_package.py
```

该样例替代测试对开发者私有 `work/evidence` 内容的依赖，不改写任何原始用户主题包。文件和清单哈希仍在测试中独立固定核对。

需要厂商压缩 DLL / 原始厂商配置的检查使用 `LocalCodecFact`、`LocalOwnedCodecFact`、`LocalCaptureFact` 等条件属性。资源缺失时 xUnit 将它们记为 **Skipped**；不能把此结果解释成完成了实机测量。私有资源由开发者自行合法取得，CI不下载厂商程序。

`URGB_YUZ_DLL` 只供可用本机资源的离线压缩检查；固定预算检查还要求本机 `tools/lianli-codec/yuz.dll` 与记录的 SHA 一致。测试不会自动将该 DLL 复制进公开仓库。

`LConnectGroupTests` 始终验证内嵌表的长度和固定 SHA；原始反编译参考文件存在时附加逐字节来源对照。公开仓库不携带这份原始文件。

软件回归与物理播放验收分开：构建/测试通过不能证明所有硬件型号、实际帧率或接收设备播放均已验证。
