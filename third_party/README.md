# third_party — 第三方二进制依赖

> 这些是**厂商提供的闭源二进制**，不是本项目源码。
> 之所以进仓库：工控现场常需离线部署，且版本一旦变化会直接影响硬件行为。

## hik-mvs — 海康机器视觉 MVS SDK 的 .NET 封装

| 文件 | 用途 |
|---|---|
| `MvCameraControl.Net.dll` | 海康相机 .NET 接口（GigE Vision / USB3 Vision） |
| `MvCameraControl.Net.xml` | API 文档（IntelliSense 用） |
| `MvCameraControl.Net.deps.json` | 依赖描述 |

**重要**：`MvCameraControl.Net.dll` 是 **AMD64 原生库**，因此所有引用它的工程必须
`PlatformTarget=x64`（已在 `Directory.Build.props` 统一设置）。
在 x86 / ARM64 上运行会直接失败。

**注意**：本仓库只包含 .NET 封装层。相机原生驱动 `MvCameraControl.dll` 需由
**MVS 客户端安装包**安装到系统（通常在 `C:\Program Files (x86)\MVS\Development\Bin`），
并加入 PATH。未安装 MVS 时枚举会返回 `HIK_SDK_INIT_FAILED`。

## ymconnect — 机器人通讯库

| 文件 | 用途 |
|---|---|
| `YMConnect_CS.dll` | 机器人通讯 .NET 封装 |
| `YMConnect.dll` | 原生依赖，需与上者同目录 |

> P0 阶段尚未接入机器人适配器；这些文件先归档，P5 使用。

## 校验值（SHA-256）

```
898C61A2EF5A4430135D480CF0BDCD170E3109C5D5D80472BC550C9B63A78220  hik-mvs/MvCameraControl.Net.deps.json
F0490B99AC89B849BC6CB987308317CECBC18543A34EEE81D7DCBDB65D997121  hik-mvs/MvCameraControl.Net.dll
1A9E2393F08DF0E4BF7F4154E54BD2C82BCE150FA2B08C446C55A220F7F9A09C  hik-mvs/MvCameraControl.Net.xml
88A056060B877F12700F04E1498997115328ECCA4132ABE3A02549D882E83313  ymconnect/YMConnect.dll
E4F529D4B32ED71B61964CBFDA90C7007D9760320E6AC0A9057521382A1D4703  ymconnect/YMConnect_CS.dll
```

更新任何一个文件后，请同步更新本清单，并在提交信息里说明来源与版本。
