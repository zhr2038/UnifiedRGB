# 2026-11-02 功能审核与修复

本轮以开发机当前完整源码为起点，包括尚未推送的界面、Codex OLED 和固件维护功能；GitHub 起点是 `26602ae`。审核、构建和测试在独立工作树进行。没有为运行测试停止现场 Host、覆盖实际配置、切换灯光场景或刷写固件。

## 审核方式

按功能核对输入、正常结果、取消、失败、重连和资源释放；发现实际缺陷后补行为回归。完整回归中的旧 fixture 也按当前生产契约修正：保留入口 Logo、确认熄灯后的设备动画策略、同连接代次约束、完整图像续租、真实发送后应答，以及原有拒绝条件。没有通过删除失败用例、增加超时或放宽生产校验来取得通过结果。

私有固件镜像、设备绑定、用户场景、抓包、凭据和现场日志均不进入 GitHub。缺少私有材料的条件用例单独标记跳过。软件与 fake 传输验证不代表物理亮度、画面流畅性、设备自主播放或长期无线稳定性验收。

## 实际修复

| 功能 | 问题与修改 | 验证重点 |
| --- | --- | --- |
| 界面命令 | `ICommand.Execute` 可在禁用后继续执行；执行入口重新核对条件，保留已有直接 `ExecuteAsync` 的 handler 检查语义 | 禁用、重复执行和原错误反馈 |
| 固件界面 | 旧读取错误覆盖新观察，读取可能污染尚未确认的写请求；增加读取代次及操作阶段约束 | 新旧回包顺序、未知结果不重发 |
| 固件确认 | 确认框或文件选择器期间重连后，旧确认仍可能被使用；返回后核对连接及原观察 | 跨连接确认、权限与阶段过期 |
| 保存场景 | 读取当前场景期间更改名称会保存到另一个名字；固定点击时的名称 | 在途编辑名称 |
| 场景详情 | 旧选择读取失败或同名重新选择可覆盖新详情；按请求代次提交 | 成功及失败乱序返回 |
| 场景包编辑 | 上一个包的延迟错误可禁用新包编辑器；核对当前选择和编辑请求 | 旧错误与新成功交错 |
| 开机启动偏好 | 注册表写失败后仍显示成功，且同值不能重试；写成功后才更新确认状态 | 写失败、保留原值、再次尝试 |
| 诊断导出 | 固定 `.tmp` 遗留后永久阻挡后续导出；使用唯一暂存文件并清理失败产物 | 失败后重试、原目标保留 |
| 帧动画 | `Frames × LEDs × 3` 的整数溢出可接受错误空块；在乘法前扩展类型并检查可分配长度 | 溢出维度和正常数据 |
| 素材重载 | 缓存 GIF 的非法延时或超长总时长进入索引后导致列表读取溢出；按导入契约验证缓存 | 负值、零延时、溢出、格式不匹配与合法素材 |
| 屏幕预览 | 预览 compositor 长期占用未播放的视频序列，阻挡删除；每次预览在所属 STA 内释放 compositor | 预览完成后立即删除序列 |
| OpenRGB 发送 | 同步发送不可取消、慢服务器阻塞 writer；改为串行异步发送并设置截止 | 取消、慢接收、完整包串行化 |
| OpenRGB 请求 | 半包失败或发送后取消仍复用字节流，可误收旧响应；退役旧连接，仅允许新连接继续 | 半包、在途取消、排队取消 |
| OpenRGB 生命周期 | Dispose 提前销毁请求信号量，可能掩盖真实异常；待最后请求离开后释放 | 在途 Dispose 与原异常 |
| Codex 任务缓存 | 历史 chat 累积到 512 项后生命周期缓存永久不可用；采用有界最近使用淘汰，重新访问仍完整验证 | 第 513 个 chat、被淘汰 chat 的终态 |
| Codex 任务元数据 | 元数据缓存同样累计历史 chat；先依据可信状态和精确 turn 生命周期过滤历史，再对活动候选限量，并在整批成功后提交 cursor | 历史终态、仍活动、未知/错 turn 和容量拒绝 |
| 干净仓库构建 | 固件维护功能引用的文档和 MSBuild 输入被发布白名单漏掉；只补入必需的四个通用文件 | 独立快照完整构建 |
| 自包含发布 | MSBuild 未限定发布项元数据来源，造成发布文件交叉组合和 `NETSDK1152`；显式绑定源项元数据 | 真正的自包含 Host 发布及完整安装包构建 |
| 固件验证接入 | 两个 fake-only 验证程序未进入 solution/CI；加入统一构建及显式执行，额外验证八份保留事务源码与 fake 的 SHA256 | 无设备操作的工作流与受保护运行时检查 |
| 验证源码与产物 | 外部 artifacts 的测试会沿二进制祖先误读旧工作区；改用实际编译测试源定位，并检查同次构建的 apphost/产品程序集 | 独立 worktree、仓库外 artifacts、拒绝回落其他 checkout |

## 逐功能覆盖

| 范围 | 对应验证 |
| --- | --- |
| 总览、全机开关、草稿与保存恢复 | CasePower、CenterToggle、Workbench、Profile、Preview |
| 内置效果、空间路径、时钟和上传预算 | 完整 Core 测试；真实时钟抖动单独运行，2ms 门槛不变 |
| 场景包导入、绑定、编辑、引用与清理 | Host 场景包事务及 GUI 契约；Python 作者工具与生成器一致性 |
| 屏幕素材、编码、序列、方向与生命周期 | 完整 Screens 测试及 ScreenWorkbench/Settings/Timeline |
| 键盘 RGB、OLED、原生输入和恢复 | 完整 Devices 与 Host owner/输入测试；USB、RF 的软件 fixture 分别覆盖 |
| GPU、主板、内存与虚拟设备 | 各适配器及 Safety 全套 |
| OpenRGB 可选兼容路线 | 真实本地 TCP loopback 与 fake 协议/生命周期测试 |
| Codex 额度、任务、浏览与启动 | Provider、Reader、缓存、renderer、输入和 STA launcher 测试 |
| 固件维护与受保护执行器 | fake-only 工作流、包、备份/写入事务、权限和保留源码 hash |
| IPC、重连和页面状态 | PipeClient、Readiness、连接 epoch 与实际 XAML 绑定 |
| 设置、开机启动、诊断与异常屏障 | AppLogic、偏好保存及导出失败重试 |
| 安装、升级、卸载及发布 | 两种 PowerShell fake 生命周期；实际自包含发布及安装包编译 |

八份保留固件事务源码及其 fake 保持原始字节。其历史命名、互操作分配和风格/性能建议只在对应文件精确豁免；其余 analyzer 及 `-warnaserror` 保持启用，CI 额外检查保留源码 hash。

## 复现入口

```powershell
dotnet build src/UnifiedRgb.sln -c Release -warnaserror --no-incremental
dotnet test src/UnifiedRgb.sln -c Release --no-build -m:1 --filter 'FullyQualifiedName!~UnifiedRgb.Core.Tests.ClockTests' --logger trx
dotnet test tests/UnifiedRgb.Core.Tests -c Release --no-build --filter 'FullyQualifiedName~UnifiedRgb.Core.Tests.ClockTests' --logger trx
./tools/windows/Verify-FirmwareSourcePins.ps1
dotnet run --project tests/UnifiedRgb.Firmware.Checks -c Release --no-build
dotnet run --project tests/UnifiedRgb.Firmware.ProtectedRuntime.Checks -c Release --no-build
python -m unittest discover -s tools/scene-packages -p 'test_*.py'
python tools/codegen/gen_effects.py --check
./tools/packaging/Build-Windows.ps1 -Output .artifacts-audit-release
```

安装生命周期 fake QA 入口是 `tools/windows/installation-qa/Invoke-InstallationQa.ps1`，分别以 Windows PowerShell 5.1 与 PowerShell 7 执行。真实安装/升级/卸载仅在 CI 一次性 Windows runner 运行，不在现用开发机执行。

## 最终结果

最终合并回归和无线专项交付结果将在本轮完成时补充。初次集成 Host 回归的 139 项失败、设备旧 fixture 的 53 项失败、初次发布失败及各次中间失败均保留在本机审核证据中，不能描述为首次运行全部通过。
