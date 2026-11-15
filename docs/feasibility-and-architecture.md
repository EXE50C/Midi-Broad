# Midi Broad (MBD) — 可行性与 v1 架构

> 状态：2026-11 实现记录
> 目标平台：Windows 11 25H2+（本机 10.0.26300，x64）
> 初代目标：把一只闲置 USB 键盘变成可被 DAW 使用的 MIDI 控制器

## 实现状态（2026-11-15）

已按本文件第 2.2 节的 WMS 路线实现第一版：

- .NET 10 / WPF，Windows 11 25H2+；
- Windows MIDI Services `Windows.Devices.Midi2 0.99.92-preview.11`；
- 虚拟设备名为 `Midi Broad`，WinMM 客户端可见；
- 输入先用 Raw Input + 全局低级钩子的**软独占**，不安装内核驱动；
- 固定释放热键 `Ctrl + Alt + Shift + F12`；
- 窗口、钢琴键盘、主题、设置、开机自启、随机力度、弯音轮均已实现。

v1 暂不包含：Interception 真独占、Windows 10 支持、自研虚拟 MIDI 驱动。WMS 虚拟设备的预览版生命周期问题见 `README.md` 的 Known issues。

---

---

## 1. 结论摘要

1. **“在设备管理器里把某只 PnP 键盘设成应用独占”不可行。**
   Windows 的 Win32 输入子系统会以独占方式打开所有键盘设备，普通应用只能通过 Raw Input 区分“哪个设备产生了输入”，但不能只接收某一只键盘，也不能阻止其他应用收到它。
2. **真正的“按设备独占”需要键盘上层过滤驱动。**
   用户态的低级键盘钩子只能全局吞键，无法可靠判断按键来自哪只键盘；它还会影响其他键盘，并且拦不住高权限窗口和 Secure Attention Sequence（Ctrl+Alt+Del）。
3. **虚拟 MIDI 驱动不需要自己写。**
   在 Windows 11 25H2+ 上，Windows MIDI Services（WMS）自带 **Virtual Device App Transport**。应用可在用户态创建虚拟 UMP 端点，DAW 可通过 WMS 或 WinMM 看到它；设备名可配置，端点生命周期跟随应用。
4. **本机条件满足 WMS 路线。**
   `midisrv` 正在运行，注册表中 `Midi2VirtualMidiTransport` 已启用。SDK 仍是 preview（`Windows.Devices.Midi2 0.99.92-preview.11`），需要按 Microsoft 的 go-live 条款分发。
5. **推荐 v1 采用两档输入后端：**
   - **兼容模式**：Raw Input + 低级钩子，无需驱动，先跑通完整产品；
   - **独占模式**：Interception 键盘过滤驱动，真正按设备拦截，作为满足“独占”承诺的模式。
   两档共用同一套映射引擎和 UI，避免返工。
6. **延迟需要实测，不能凭感觉承诺。**
   虚拟路由本身通常远小于 1 ms；端到端主要花在 USB 轮询、应用调度、MIDI 服务转发和 DAW 缓冲上。以“按键到 DAW 收到 Note On”为指标，目标 < 10 ms，并在 M0 阶段用时间戳实测。

---

## 2. 两个核心不确定性的答案

### 2.1 能不能让某只键盘被 MBD 独占？

#### 标准 Win32：不能

Microsoft 的驱动文档明确说明：

> The Microsoft Win32 subsystem opens all keyboard and mouse devices for its exclusive use. For each device class, the Win32 subsystem treats input from all the devices as if the input came from a single input device. An application cannot request to receive input from only one particular device.

也就是说：

- `Raw Input` 能拿到 `hDevice`，能区分设备，适合“只听这只键盘”；
- 但 Raw Input **不提供“阻止其他应用收到这只键盘”的能力**；
- `RIDEV_NOLEGACY` 只会让注册它的进程不再收到旧式消息，不会替其他进程屏蔽；
- `HidHide` 官方 FAQ 明确说不能隐藏键盘/鼠标，也拦不住 Raw Input；
- 低级钩子 `WH_KEYBOARD_LL` 能吞键，但回调里没有可靠的设备来源，只能靠扫描码/时间窗猜，容易误伤其他键盘。

因此，纯用户态方案只能叫 **“软独占 / best effort”**：在普通桌面应用中大多数时候可用，但产品文案不能承诺“系统级独占”。

#### 真正独占：键盘上层过滤驱动

可行路线是安装一个位于 `kbdclass` 之上的键盘过滤驱动，由驱动把“选中设备”的按键交给 MBD，其余设备照常走系统链路。

候选：

| 方案 | 真独占 | 安装成本 | 授权 | 风险 |
|---|---|---|---|---|
| Raw Input + LL 钩子 | 否 | 无 | 自有 | 误伤、漏键、权限窗口拦截不了 |
| **Interception** | 是 | 管理员安装驱动 + 重启 | 非商业 LGPL；商业需购买授权 | 驱动兼容性、反作弊软件可能拦截、应用挂起有锁键盘风险 |
| 自研过滤驱动 | 是 | 签名、安装、维护 | 自有 | 周期和成本最高，v1 不建议 |

**建议**：v1 的“独占模式”用 Interception；同时保留无需驱动的兼容模式。产品必须把两种模式的区别写在 UI 上，不能默认假装已经独占。

#### 独占模式必须处理的故障

这是比“能不能拦”更容易被忽略的问题：

- MBD 崩溃 / 被结束 / 无响应时，选中的键盘不能变成“死键盘”；
- 需要一个独立的看门狗（watchdog）进程或服务：MBD 定期发心跳，心跳丢失就强制释放过滤；
- 固定热键必须在**过滤层之前**判断，按下后立即停止拦截；
- 释放时要发送 All Notes Off、Pitch Bend 回中、Sustain Off；
- 设备拔出、虚拟 MIDI 端点失效、DAW 退出，都应自动释放；
- 建议在驱动模式之外保留一个“紧急恢复”说明：安全模式或卸载驱动。

### 2.2 自带虚拟 MIDI 驱动与路由延迟

#### Windows 11 25H2+：用 WMS，不写驱动

WMS 的 **Virtual Device App Transport** 允许应用创建一个虚拟 UMP 设备：

- 设备侧端点归应用所有，应用退出则设备消失；
- 其他应用看到的是普通 MIDI 端点；
- WinMM / WinRT MIDI 1.0 客户端也能看到它（MIDI 1.0 消息会被转换）；
- 设备名、描述、制造商、Function Block 都可在创建时配置；
- 还可通过 `UpdateEndpointName` 运行时改名。

这正好覆盖“自带虚拟 MIDI 键盘”和“支持更改设备名”两个需求，而且**无需自研内核驱动**。

限制：

- 仅 Windows 11 25H2 及更高版本；Windows 10 不支持；
- SDK 目前是 preview，需遵守 go-live 条款，并随 API 更新；
- WinMM 客户端面对动态端点时，端口号可能变化，老 DAW 的端口记忆可能不稳定；
- 设备名虽然可改，但已经连接它的 DAW 可能需要重新选择端口。

#### Windows 10：没有免费的系统级方案

可选：

- `teVirtualMIDI`（Tobias Erichsen）：成熟、稳定，商业使用需授权；
- `loopMIDI`：同样基于 teVirtualMIDI，通常要求用户另行安装；
- 自研内核 MIDI 驱动：需要 EV 证书、签名、WHQL/兼容性测试，v1 不现实；
- 只支持 Win11 25H2+，把 Win10 排除在 v1 之外。

#### 延迟与稳定性判断

路径：

```
USB 键盘 → kbdclass → 过滤驱动/Raw Input → MBD 映射
        → WMS 虚拟端点 → MIDI 服务 → DAW
```

- USB 轮询：常见 1–8 ms（取决于键盘和 USB 配置）；
- 应用映射：通常 < 1 ms；
- 虚拟端点 + MIDI 服务：通常亚毫秒到数毫秒；
- DAW 输入缓冲/调度：可能再增加数毫秒。

**不要承诺“零延迟”**。M0 阶段用同一时钟记录“按键事件时间”和“DAW/MIDI 监听端收到时间”，给出实测分布（P50/P95/P99），再决定是否需要优化。

稳定性方面：

- WMS 是系统服务，成熟度高于自研驱动；
- preview API 仍有变更风险，需要版本探测和降级提示；
- Interception 驱动本身稳定，但“应用挂起导致键盘被吞”是产品必须自保的场景。

---

## 3. 产品范围（v1）

### 3.1 输入与设备

- 列出当前可用的物理键盘，显示名称、VID/PID、连接类型、实例路径；
- 热插拔：`WM_DEVICECHANGE` + 重新枚举，列表实时更新；
- 选中设备按**设备实例**持久化，不按列表序号；
- 设备拔出：立即释放所有音符，等待重新插入；可选“同设备重新插入后自动恢复监听”；
- 不显示/不支持：PS/2 键盘、屏幕软键盘、注入输入；
- 支持多键盘环境：只拦截选中设备，其他键盘正常使用。

### 3.2 映射

- 预设模式：
  - 钢琴（默认，两排键覆盖约两个八度）；
  - 半音阶；
  - GM 鼓垫（可选）。
- 每个按键可映射到：Note、Pitch Bend、Sustain、Modulation、Program Change（v1 至少 Note + Pitch Bend）。
- 全局参数：MIDI 通道、基准八度、移调、力度范围；
- 力度：固定值或每次 Note On 在 `[min, max]` 内随机；
- 弯音：按住指定键产生弯音，松开回中；可配置弯音范围（默认 ±2 半音）；
- Panic：All Notes Off + Pitch Bend 回中 + Sustain Off；
- 连击/重复：默认忽略系统自动重复，只认真实按下/松开。

### 3.3 独占与释放

- 固定热键：`Ctrl+Alt+Shift+F12`（代码常量，UI 只展示，不允许修改）；
- 热键在映射之前处理，任何模式下都有效；
- 释放后：停止拦截、Panic、UI 状态回到“未监听”；
- 独占模式额外提供看门狗；
- 关闭窗口默认最小化到托盘，不直接退出；托盘菜单提供“释放键盘并退出”。

### 3.4 虚拟 MIDI 设备

- 启动时创建名为 `Midi Broad` 的虚拟端点；
- 设置页可改设备名，保存后重建端点；
- 显示端点状态：可用 / 服务不可用 / API 版本不兼容；
- Win11 25H2+ 使用 WMS；不满足时给出明确提示，不静默失败；
- 预留 `IMidiOutput` 抽象，后续可接 WinMM/loopMIDI/teVirtualMIDI。

### 3.5 GUI

- 主界面：
  - 顶部：设备选择、监听/释放按钮、虚拟端点状态；
  - 中部：当前预设、八度、移调、力度、弯音设置；
  - 底部：钢琴键盘，显示当前按下的音符与力度；
- 主题：
  - 跟随系统浅色/深色；
  - 自定义强调色；
  - 自定义背景图（暗化/模糊处理，保证可读性）；
- 设置页：虚拟设备名、开机自启、映射编辑、关于；
- 托盘：显示状态、释放键盘、打开主界面、退出；
- 单实例运行；第二次启动唤起已有窗口。

### 3.6 开机自启

- 使用当前用户 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`；
- 启动参数 `--minimized`，只驻留托盘，不自动开始监听；
- 不需要管理员权限（独占驱动安装除外）。

---

## 4. 技术选型

### 推荐：C# / .NET 10 / WPF

理由：

- 本机已有 .NET 10 SDK，无额外运行时安装；
- WPF 对自定义主题、背景、钢琴键盘绘制足够成熟，且不需要 MSIX 打包；
- P/Invoke Raw Input、低级钩子、SetupAPI 都直接可用；
- 可通过 C#/WinRT 引用 `Windows.Devices.Midi2`；
- 后续接 Interception 只是多一个 P/Invoke 后端。

不选 WinUI 3 的原因：打包/部署和窗口定制成本更高，v1 不需要。

不选 C++/Qt 的原因：开发速度慢，UI 主题和背景定制成本高；除非后续要自研驱动，否则没有必要。

### 项目结构

```
Midi Broad/
  MidiBroad.sln
  src/
    MidiBroad.App/          WPF 界面、托盘、设置
    MidiBroad.Core/         映射引擎、状态机、配置模型
    MidiBroad.Input/        Raw Input、低级钩子、Interception 后端
    MidiBroad.Midi/         WMS 虚拟端点、WinMM 后备
  tests/
    MidiBroad.Core.Tests/   映射与状态机单元测试
  docs/
```

### 关键接口

```csharp
interface IKeyboardCapture : IAsyncDisposable
{
    event EventHandler<KeyTransition> KeyChanged;
    Task StartAsync(KeyboardDevice device, CancellationToken ct);
    Task StopAsync();
    bool IsExclusive { get; }
}

interface IMidiOutput : IAsyncDisposable
{
    Task StartAsync(string deviceName, CancellationToken ct);
    void Send(in MidiMessage message);
    Task RenameAsync(string newName);
    MidiOutputStatus Status { get; }
}
```

映射引擎是纯函数 + 状态机，不依赖 UI 和驱动，便于单元测试和后续复用。

---

## 5. 风险清单

| 风险 | 影响 | 缓解 |
|---|---|---|
| 应用崩溃/挂起导致键盘被吞 | 用户无法输入，产品事故 | 看门狗、心跳超时释放、固定热键、启动时检测残留过滤 |
| 低级钩子误判设备 | 其他键盘被吞/漏键 | 不作为独占承诺；默认兼容模式明确标注 |
| Interception 与反作弊/安全软件冲突 | 无法安装或游戏拒绝启动 | 安装前说明；提供兼容模式；不静默装驱动 |
| WMS preview API 变更 | 构建/运行失败 | 版本探测、启动自检、清晰错误提示、锁定依赖版本 |
| WinMM 动态端口号 | 老 DAW 记不住端口 | 文档说明；优先用 WMS 原生支持；提供固定 loopback 后备 |
| 键盘无 anti-ghosting | 同时按键丢失 | UI 提示；映射设计避免大量同按；不承诺全键无冲 |
| 设备重插后实例路径变化 | 自动恢复失败 | 按序列号/VID-PID 匹配，失败则提示重选 |
| 安全桌面/SAS 无法拦截 | 独占不覆盖 Ctrl+Alt+Del | 明确说明；这反而是安全底线 |

---

## 6. 里程碑

### M0 — 技术验证（先做，1–2 天）

- 用最小 C# 程序创建 WMS 虚拟端点，确认 DAW/MIDI 监听端能看到 `Midi Broad`；
- 用 Raw Input 区分两只键盘，验证只映射选中设备；
- 测量“按键 → 虚拟端点 → 监听端”的端到端延迟；
- 验证 WMS 端点创建/销毁/改名；
- 输出一份实测记录，决定是否继续 WMS 路线。

### M1 — 可用原型

- 映射引擎 + 配置；
- 兼容模式输入；
- WPF 主界面 + 钢琴键盘；
- 虚拟端点输出；
- 固定热键、Panic。

### M2 — 独占模式

- Interception 集成；
- 驱动安装/检测/卸载引导；
- 看门狗与故障释放；
- 热插拔与自动恢复。

### M3 — 产品化

- 主题/背景/系统颜色；
- 开机自启、托盘、单实例；
- 安装包、签名、错误上报；
- 延迟与稳定性回归测试。

---

## 7. 待确认

1. **只支持 Windows 11 25H2+，还是必须兼容 Windows 10？**
   - 只支持 Win11：可用系统 WMS，无需自研虚拟驱动；
   - 兼容 Win10：需要 teVirtualMIDI/loopMIDI 或自研签名驱动，成本和授权完全不同。
2. **是否接受安装内核过滤驱动来实现真独占？**
   - 接受：走 Interception，满足“设备级独占”；
   - 不接受：v1 只能做软独占，产品承诺需要相应调整。
3. **自用还是商业分发？**
   - 商业分发涉及 Interception 商业授权、WMS preview 分发条款和代码签名。
4. **默认钢琴布局用哪套键位？**
   - 建议 `Z S X D C V G B H N J M` + `Q 2 W 3 E R 5 T 6 Y 7 U`，两排约两个八度；可后续做成可编辑映射。

---

## 8. 参考资料

- Microsoft 驱动文档：*Open and Close a Keyboard and Mouse Device*  
  https://learn.microsoft.com/en-us/previous-versions/ff542441(v=vs.85)
- Windows MIDI Services：Virtual Device App Transport  
  https://microsoft.github.io/MIDI/kb/virtual-device-app/
- Windows MIDI Services：最低系统要求  
  https://microsoft.github.io/MIDI/kb/minimum-requirements/
- Windows MIDI Services：App SDK 概览  
  https://microsoft.github.io/MIDI/sdk-overview/
- HidHide FAQ（明确不支持键盘/鼠标）  
  https://docs.nefarius.at/projects/HidHide/FAQ/
- Interception（键盘上层过滤驱动，双许可证）  
  https://github.com/oblitum/Interception
