# 屏幕页隔离验收工具

这是开发工具，**不属于安装包或运行时**。它直接承载主程序的 WPF 页面和主窗口，但仅注入内存中的 `FakeScreenRpc`；不引用或启动 Host，不创建 PipeClient，不访问 USB，不读取本机生产配置。工具的“应用”和“保存”只记录不同的模拟 RPC 调用。

从仓库根目录使用 .NET 8 SDK：

```powershell
dotnet build tools/windows/ui-qa/UnifiedRgb.UiQa.csproj --artifacts-path .artifacts-ui-m1-20261019 -warnaserror
./tools/windows/ui-qa/run-checks.ps1 -Round local-check
```

脚本启动可见原生 WPF 窗口，依次检查深浅页面、三种布局、长中文、忙碌、断线、错误、空状态、本机设置和分别写入流程，结束后自动关闭自己的窗口。失败会返回错误并保留检查记录。`--verify-writes=true` 只作用于内存模拟服务。

`qa-options.json` 放在工具的编译输出旁，以完整 `--name=value` 字符串数组配置尺寸、主题和状态。`--width`/`--height` 是请求的物理窗口像素，最小窗口约束可能使实际尺寸更大；以记录的有效 DPI、实际窗口/客户区像素、客户区 DIP 和页面 DIP 为准。不会修改系统 DPI。

**证据类型必须区分：**

- JSON：原生窗口的布局、数据、滚动、写入记录及断言结果。
- `.diagnostic-layout.png`：`RenderTargetBitmap` 布局诊断图，可排查绑定与裁切，**不是桌面截图，不证明 DWM 合成、实际 DPI 清晰度或键盘操作，也不计入 M2 两轮截图验收**。
- 真实桌面截图：另由 Windows 捕获工具取得，必须核对窗口身份和画面，拒收黑屏、旧帧及其他窗口。

`LegacyScreenWorkspaceView` 是本轮试点之前的页面快照，只在 `--baseline=true` 时使用。主程序不依赖它。软件渲染与无显示器渲染开关也仅限此工具，不改变主程序渲染配置。
