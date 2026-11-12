# 看护指示灯

这个可选示例把床铺区域的活动量显示在 ROG Azoth X 的数字行上。NAS 从已有 RTSP 音画流取样，Windows 收到检测状态后通过光域 Host 的本机命名管道临时叠加逐键灯效。键盘 OLED 的 HA 页可以开启或关闭提示。

| 键位 | 含义 |
| --- | --- |
| `·` 至 `7` | 绿色随局部动作增减 |
| `8` 至 `=` | 动作较大或持续时延伸到黄色 |
| Backspace 和 Ins | 确认哭声时亮红，哭声结束后按当前动作回缩 |

NAS 以 12 fps 分析床铺灰度画面的变化，用 TensorFlow Lite 版 YAMNet 对声音分类。人进入床铺范围也可能增加活动量。短促、较轻或被盖住的哭声可能漏判。推理在 NAS 内运行，Windows 读取的是活动量、哭声状态与音画可用性。 [YAMNet](https://www.tensorflow.org/tutorials/audio/transfer_learning_audio)

关闭提示时，监听器请求撤掉覆盖，光域恢复当前场景的灯效。重新开启时读取最新状态。音画或 HA 开关连接失效时，监听器释放覆盖。Host 也会在本机管道断开或五秒未续期后撤掉覆盖。

## 所需环境

- 用当前源码构建的 Windows 光域 Host，以及已经在该 Host 中验证的 Azoth X 逐键灯光映射。USB 和 2.4 GHz 均使用各自的已验证映射。
- 可访问的 Home Assistant。键盘 OLED 上的 HA 页需要相应的键盘固件和本机配置。
- Linux x86-64 NAS 或服务器上的 Docker，以及已经接入的 RTSP 视频和音频流。
- Windows Python 3.11。Host 与监听器可用同一个 Windows 用户下的 Home Assistant 凭据。

## 在 NAS 运行检测

在本目录下载并校验上游模型，再构建检测镜像。模型和分类表由脚本下载。 [模型来源](https://developers.google.com/edge/litert/conversion/tensorflow/metadata_writer_tutorial#audio_classifiers)

```sh
python3 fetch_model.py
docker build -t unifiedrgb-child-monitor .
```

把示例变量换成自己的局域网地址和 RTSP 来源。ROI 是床铺的左、上、右、下坐标，取值范围为 0 到 1。

```sh
export NAS_LAN_IP='NAS 的局域网 IP'
export WINDOWS_LAN_IP='Windows 电脑的局域网 IP'
export CAMERA_RTSP_URL='rtsp://RTSP 主机:端口/流名称'

docker run -d --name unifiedrgb-child-monitor --restart unless-stopped \
  --network host --cpus 1 --memory 512m --read-only --cap-drop ALL \
  --security-opt no-new-privileges unifiedrgb-child-monitor \
  --bind "$NAS_LAN_IP" --allow "$WINDOWS_LAN_IP" \
  --source "$CAMERA_RTSP_URL" --roi 0.20 0.15 0.90 0.85
```

检测接口只允许 `--allow` 指定的地址访问。Windows 将使用 NAS 的 `/status` 接口。摄像头移动后重新划定床铺区域。

## 配置 HA 和键盘开关

1. 将 [HA 示例包](home-assistant-package.yaml)加入自己的 HA packages。也可以把其中的 `input_boolean` 和 `script` 项合并进现有配置。首次部署时，可以从 HA 或键盘开启「看护指示灯」。HA 会保存之后的开关状态。
2. 在运行 Host 的 Windows 账户下创建通用凭据 `UnifiedRgb/HomeAssistant/<HA 根地址>`，密码填 HA 访问令牌。HTTPS 地址可以使用域名，HTTP 使用私有 IPv4 地址。令牌保存在 Windows 凭据管理器。令牌所属的 HA 账号需要能读取开关并运行该脚本。
3. 在 Host 本机配置的 `UnifiedRgb.KeyboardHomeAssistant.Scenes` 数组追加这一项。现有场景继续保留，重启 Host 后键盘 HA 页会显示「看护指示灯」。

```json
{
  "EntityId": "script.unifiedrgb_child_monitor_lights",
  "Label": "看护指示灯",
  "ReadScriptResult": true,
  "SwitchEntityId": "input_boolean.unifiedrgb_child_monitor_lights"
}
```

OLED 上下拨选择项目，中按设置画面提示的开启或关闭状态。键盘显示的是 HA 返回的开关状态，光域的灯条由 Windows 监听器单独刷新。

## 在 Windows 运行监听器

在本目录运行。将命令里的 `NAS_LAN_IP` 和 `HA_LAN_IP` 换成自己的地址，HA 地址和 Windows 凭据目标一致。

```powershell
py -3.11 -m venv .venv
& .\.venv\Scripts\python.exe -m pip install -r requirements-windows.txt
& .\.venv\Scripts\python.exe keyboard.py --status-url "http://NAS_LAN_IP:18772/status" --ha-url "http://HA_LAN_IP:8123"
```

默认从索引 13 开始（索引从 0 计数），预期键序为 `·`、`1` 至 `0`、`-`、`=`、`Bksp`、`Ins`。默认映射用反引号表示 `·` 键。如果自己的已验证映射顺序或键名不同，用 `--start-index` 和 `--key-names` 提供对应的连续键序。Host 会先核对键序，再写灯。常用时可以把监听器配置成当前 Windows 用户的登录任务。

## 验证范围

- Host 测试覆盖 USB 和 2.4 GHz 的已验证键映射、覆盖回缩和原色恢复。
- Windows 监听器测试覆盖 NAS 等待期间关灯、关闭期间忽略事件，以及开启时只使用最新状态。
- 这项功能的实机联动已在 Azoth X 的 2.4 GHz 模式确认。USB 的看护灯开关联动仍待实机复测。

```powershell
py -3.11 -m unittest discover -s . -p test_keyboard.py
```
