# PROTOCOL — HID 报文逆向记录

> 这份文档是**实测得出的**，不是从厂商文档抄的。所有数字都来自真机探测。
> 改动任何偏移量之前，请先读完全文——这套报文一旦搞错，就彻底读不到数，且症状和「设备休眠」完全一样，极难区分。

---

## 1. 目标设备

| 项 | 值 |
|---|---|
| 鼠标 | 迈从 MCHOSE A5 Ultra |
| 接收器 | `VID_0BDA & PID_4762`（Realtek 4K Nano dongle） |
| 设备路径 | `\\?\hid#vid_0bda&pid_4762&mi_02&col01#...` |
| Top-Level Collection | Usage Page `0xFF01`，Usage `0x0001`（vendor-defined） |
| Feature Report 长度 | **65 字节**（含 1 字节 report ID） |
| Input / Output Report 长度 | 不含在内，与电量无关，忽略 |

同一接收器还挂了多个 HID 集合（`mi_00` / `mi_01` / `mi_02` 的键盘、鼠标、consumer control）。**只有 `mi_02` 的 `0xFF01/0x0001` 集合能回答电量查询**，其余会直接返回失败。

---

## 2. 报文格式

### 2.1 请求帧（write，65 字节）

```
offset  value   说明
------  -----   ------------------------------------------
[0]     0x12    Feature Report ID（写入时用的 ID）
[1]     0xF9    操作码 0x06 按位取反（0x06 ^ 0xFF）
[2..64] 0xFF    填充，全 1
```

对应代码（`Providers.cs`）：

```csharp
var frame = new byte[65];
for (int i = 0; i < frame.Length; i++) frame[i] = 0xFF;
frame[0] = 0x12;                      // RequestReportId
frame[1] = (byte)(0x06 ^ 0xFF);       // 0xF9
Hid.SetFeature(h, frame);
```

> 为什么是 `0x06` 取反：这套协议**数值域整体取反传输**（见 2.3），操作码也遵循同一规则。

### 2.2 回包（read，65 字节）

**关键：读取用的 Report ID 是 `0x11`，不是 `0x12`。写入和读取是两个不同的 ID。**
实测把 `0x12` 填进 `buf[0]` 再读，会返回 `ERROR_INVALID_PARAMETER (87)`。

```
offset  value        说明
------  -----------  ------------------------------------------
[0]     0x11         Feature Report ID（读取时预置，设备不覆盖）
[1]     0xF9         回包头 == 请求魔数，不等则无效
[2..64] ...          payload，逐字节取反的应答体
```

判定有效：

```csharp
var reply = Hid.GetFeature(h, 65, 0x11);
if (reply == null || reply[1] != 0xF9) { /* 无效，重试 */ }
```

### 2.3 payload 布局（取反后）

把 `reply[2..64]` **逐字节 `^ 0xFF`**，得到真实应答体：

```
payload  raw索引  值（实测）      含义
-------  -------  -------------   ---------------------------
[0]      [2]      0x01            应答类型
[1]      [3]      0x00            reserved
[2..3]   [4..5]   0x47 0x62       ← 接收器 PID 0x4762（小端），用于校验通信用对了设备
[4..7]   [6..9]   0x00 0x00 0x00 0x02   reserved / 固件信息
[8]      [10]     0x09            传输标记：0x09 = 走 2.4G 接收器
[9]      [11]     0x24            电量百分比，0x24 = 36%
[10]     [12]     0x00            充电状态：非 0 = 充电中
[11..]   [13..]   0x00 ...        保留
```

所以直接对原始 buffer 索引就是：

```
reply[10] == 0x09   → 走接收器
reply[11] ^ 0xFF    → 电量
reply[12] ^ 0xFF    → 充电中
```

代码里的常量与之对应：

```csharp
public const int LevelIndex  = 11;
public const int ChargeIndex = 12;
int level  = reply[LevelIndex]  ^ 0xFF;
int charge = reply[ChargeIndex] ^ 0xFF;
```

> 实测原始回包样本（前 13 字节）：
> `12 F9 FE FF 9D B8 FF FF FF FD F6 DB FF ...`
> 取反 payload：`01 00 62 47 00 00 00 02 09 24 00`
> —— 其中 `62 47` 是 PID `0x4762` 的小端写法，`09` 是接收器标记，`24` = 36，正是当时实测电量。

---

## 3. 通信时序

必须**在同一个句柄内**完成 write → sleep → read。实测用两个独立句柄会稳定失败。

```
CreateFile(路径, GENERIC_READ|GENERIC_WRITE, FILE_SHARE_READ|WRITE)
   │
   ├─ HidD_SetFeature(65B, [0]=0x12)      ← 发起查询
   │
   ├─ Sleep(60ms)                          ← 给接收器时间填缓冲区
   │
   ├─ HidD_GetFeature(65B, buf[0]=0x11)   ← 取回
   │      └─ 失败(err=31/87) 或 buf[1]!=0xF9 → 失败
   │
   └─ 解析 → CloseHandle
```

**重试策略**（`McHoseProvider.TryRead`）：最多 8 轮，每轮 = 写 + 60 ms + 读 + 140 ms 退避。
原因是鼠标闲置时接收器**不填缓冲区**，返回 `ERROR_GEN_FAILURE (31)`。这不是错误，是休眠。
用户只要动一下鼠标，下一次重试就成功了。

---

## 4. 错误码对照表

| `GetLastError` | 常量 | 真实含义 | 该怎么处理 |
|---:|---|---|---|
| `31` | `ERROR_GEN_FAILURE` | 接收器暂时无应答（**鼠标休眠中**） | 重试，不要报错；保留上次读数 |
| `87` | `ERROR_INVALID_PARAMETER` | report ID 或长度不对 | 检查 `buf[0]` 是否预置为 `0x11`、长度是否 65 |
| `1167` | `ERROR_DEVICE_NOT_CONNECTED` | 接收器被拔掉 / 通道被独占 | 重新枚举设备 |
| `5` | `ERROR_ACCESS_DENIED` | 另一进程独占（**网页驱动开着**） | 提示用户关掉网页驱动 |

第 4 条特别重要：**厂商网页驱动（WebHID）会独占这个通道**。这就是用户「必须进网页驱动才能看电量」的根因。
本工具和网页驱动**不能同时工作**。

---

## 5. 设备发现逻辑

`McHoseProvider.FindCollection()` 分两级，顺序不能变：

```csharp
// 第一级：白名单精确匹配（settings.json 的 receiverIds）
foreach (var c in all)
    if (Matches(c) && c.UsagePage == 0xFF01 && c.Usage == 0x0001) return c;
foreach (var c in all)
    if (Matches(c) && c.FeatureLength >= 65) return c;

// 第二级：发现模式 —— 白名单全部落空时
foreach (var c in all)
{
    bool vendorPage = c.UsagePage == 0xFF01 || c.UsagePage == 0xFFFF || c.UsagePage == 0xFFA0;
    if (vendorPage && c.FeatureLength >= 65) return c;
}
```

**发现模式的意义**：白名单是「已知能用的」，但同一套 Realtek 固件会换 VID 出货（这台就是 `0x0BDA`，
而 HaloBattery 只认迈从自己的 `0x2023/0x5253/0x3837`）。所以留了这条兜底：任何 vendor 集合、只要 feature report ≥ 65 字节，就用同一套报文试一次。
试错成本很低（失败只是 `err=87` 或超时），成功率却不低。

新增已知设备 → 往 `receiverIds` 加一条 `"XXXX:YYYY"` 即可，不用改代码。

---

## 6. 蓝牙路径（尚未实现）

同一只鼠标的蓝牙端暴露了标准 BLE GATT 电池服务：

```
Service   0x180F  Battery Service
  └─ Char 0x2A19  Battery Level  (Notify + Read, uint8 0-100%)
```

理论上比私有协议简单得多。**但为什么没做**：

1. 用户的接收器是 2.4G，蓝牙链路实测 `ConnectionStatus == Disconnected`，当时根本测不了；
2. 通过 Windows 读 BLE GATT 需要 WinRT API（`Windows.Devices.Bluetooth`），而**当前编译环境只有 .NET Framework 的四个程序集**，拿不到 WinRT 投影；
3. 走 P/Invoke `BluetoothGATTGetCharacteristicsValue` 也可行，但要额外引入 `bthprops.cpl` / `BluetoothAPIs`，工作量和不确定性都远超 2.4G 路径。

如果要做，见 `ROADMAP.md` 的 P0-2，那里给了两条技术路线和取舍。
好消息：`IBatteryProvider` 接口就是为这种情况留的，新增一个 provider 不改任何现有代码。

---

## 7. 如果你想复现逆向过程

探测脚本的思路（用 PowerShell 调 hid.dll 即可，无需第三方库）：

1. **枚举**：`SetupDiGetClassDevs(HidD_GetHidGuid())` → 遍历接口 → `HidD_GetAttributes` 拿 VID/PID → `HidP_GetCaps` 拿 UsagePage / FeatureReportByteLength
2. **筛选**：只看 `FeatureReportByteLength >= 65` 的 vendor collection
3. **试写**：`HidD_SetFeature` 发 65 字节请求帧，看返回值
4. **试读**：`HidD_GetFeature`，**记得先 `buf[0] = 0x11`**
5. **比对**：连续读，看 `buf[1]` 是否恒等于 `0xF9`、取反后数字是否在 0~100 且随使用时间缓慢下降

第 5 步是关键校验：如果取反后 `payload[2..3]` 正好等于接收器 PID，基本可以确定协议对上了。

**一个必须注意的陷阱**：探测时如果开着厂商网页驱动，`SetFeature` 会返回 `ERROR_ACCESS_DENIED`。
先关掉它，或者干脆把接收器换一个 USB 口（有时能绕开独占）。
