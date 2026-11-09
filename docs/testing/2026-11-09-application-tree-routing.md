# 桌面应用树分流验收（2026-11-09）

## 结果

通过。夸克网盘、迅雷和百度网盘的主程序、辅助进程及账号客户端流量均进入 NICRoute TUN，随后通过配置应用 selector 选择“以太网 4”。15 秒真实连接事件中没有发现这三条规则的应用流量走“1G”。

测试版本为工作树中的 `v0.2.5`、VersionCode `7`，基线提交 `89cc5d6`。本报告不表示已创建标签、已上传附件或已发布更新清单。

## 环境与规则

| 项目 | 值 |
| --- | --- |
| Windows | x64，管理员令牌运行真实 TUN 与 WFP |
| 全局默认 | `1G`，`192.168.30.226`，ifIndex 9 |
| 三条应用规则首选 | `以太网 4`，`10.185.16.110`，ifIndex 27 |
| 回退 | 三条规则均为“不联网” |
| 联网探测 | 关闭 |
| 分流引擎 | sing-box `1.14.4` |

配置中的应用：

1. QuarkCloudDrive：`D:\CloudDriveMCP\clients\quark\quark_cloud_drive.exe`
2. 迅雷：`D:\CloudDriveMCP\clients\thunder\Program\Thunder.exe`
3. BaiduNetdisk：`D:\CloudDriveMCP\clients\baidu\BaiduNetdisk.exe`

## 修复前问题

旧配置只匹配三个主程序完整路径。实际观察到：

- Quark 的 `C:\Users\Administrator\AppData\Local\Programs\quark-cloud-drive\app-3.24.0\QuarkCloudDrive.exe` 未归入规则。
- 迅雷的 `D:\CloudDriveMCP\clients\thunder\Program\resources\bin\SDK\DownloadSDKServer.exe` 未归入规则，承载较大上传流量。
- 百度网盘账号客户端、`BaiduNetdiskRender.exe` 和 `BaiduNetdiskHost.exe` 未归入规则。
- 主程序命中规则并不能保证辅助进程命中；WFP 同样只保护主程序时，辅助进程仍可从物理网卡绕过 TUN。

## 修复后证据

### 引擎路径规则

运行中的 `runtime/engine.json` 已包含完整应用树：

- Quark：`QuarkCloudDrive.exe` 辅助进程与 `quark_cloud_drive.exe` 主程序在同一路径表达式。
- 迅雷：`DownloadSDKServer.exe`、`Thunder.exe` 和同产品浏览器扩展进程在同一路径表达式。
- 百度：账号客户端 `BaiduNetdisk.exe`、主程序及 `BaiduNetdiskHost.exe` 在同一路径表达式。
- `CloudDriveMCP.exe` 与 `CloudMirror.exe` 同时具备多个应用族特征，无法唯一归属；生成了一条显式 `offline` 拒绝规则，没有猜成任一应用或退回全局网卡。

### WFP 保护

`artifacts/v025-wfp-filters.xml` 显示下列桌面路径都有 IPv4/IPv6 `ALE_AUTH_CONNECT` 应用身份约束，并带下一跳网卡条件：

- `QuarkCloudDrive.exe`
- `DownloadSDKServer.exe`
- 百度账号客户端 `BaiduNetdisk.exe`
- `BaiduNetdiskHost.exe`

这些路径不再只有主程序受 WFP 保护。过滤器继续保留回环豁免。

### 15 秒真实应用流量

通过引擎只读连接订阅采集，表中总量单位为字节。三条应用规则的实际 selector 均为 `nic-bec993109bb8d8a0`，对应出口 `以太网 4`。

| 进程 | 上行 | 下行 | 连接数 | 实际出口 |
| --- | ---: | ---: | ---: | --- |
| 迅雷 `DownloadSDKServer.exe` | 785681 | 32390 | 6 | `以太网 4` |
| 夸克 `QuarkCloudDrive.exe` | 11960 | 5764 | 3 | `以太网 4` |
| 百度账号客户端 `BaiduNetdisk.exe` | 845019 | 691059 | 11 | `以太网 4` |
| 百度 `BaiduNetdiskHost.exe` | 3323 | 738 | 1 | `以太网 4` |
| 迅雷 `Thunder.exe` | 30120 | 75520 | 9 | `以太网 4` |
| 夸克 `quark_cloud_drive.exe` | 14843 | 15802 | 15 | `以太网 4` |
| 百度 `BaiduNetdisk.exe` 主程序 | 101416 | 64769 | 9 | `以太网 4` |

连接链示例为 `nic-bec993109bb8d8a0>app-<规则ID>`，同时证明物理出口和命中的应用 selector，而不是只依据规则预览。

测试启动时已存在的部分迅雷 TCP 连接仍在 `10.185.16.110`，这是同一张“以太网 4”，不是全局出口；新连接进入 `172.31.255.1` 的 NICRoute TUN，再由引擎从 `10.185.16.110` 外发。对目标应用 PID 的 TCP 表检查未发现 `192.168.30.226`（1G）承载连接。

## 自动化验证

- 常规套件：206 项断言通过。
- `--engine --integration --traffic --applications --catalog`：227 项断言通过。
- 构建：自包含发布成功，0 警告、0 错误。
- 真实应用族配置由 sing-box `check` 校验，退出码 0。
- 新辅助进程在引擎启动后出现时，集成测试确认应用树重载并生成新路径规则。

## 清理

通过 WPF 正常关闭路径停止本次测试实例。关闭后：

- 无 NICRoute 或 sing-box 进程。
- `ipconfig` 不再列出 NICRoute TUN。
- 停止后重新导出的 `artifacts/v025-wfp-after-stop.xml` 中，`NICRoute application interface guard` 与 `NICRoute ·` 匹配数为 0。

## 仍有限制

- 应用树运行时刷新需要短暂重启 sing-box；新辅助进程首次被确认前无法仅靠静态进程路径规则预判。
- 文件公司名/产品名和路径令牌用于补充进程关系，只在证据明确且归属唯一时生效；歧义路径按安全策略阻断。
- 本报告验证的是本次本机实际的三个桌面应用族，不等同于所有厂商的共享宿主、包外服务或跨用户进程都已覆盖。
