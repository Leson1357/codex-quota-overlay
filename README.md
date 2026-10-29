# Codex Quota Overlay

**额度就在眼前，专注不用打断。**

一个小巧的 Windows 悬浮条，把 Codex 的 **5 小时额度、周额度和重置倒计时** 放在聊天输入框上方。跟着窗口走，需要你操作时让开。

[下载 Windows 版](https://github.com/Leson1357/codex-quota-overlay/releases/latest) · [构建检查](https://github.com/Leson1357/codex-quota-overlay/actions/workflows/build.yml) · [MIT 许可证](LICENSE)

![白底黑字额度条，右侧为倒计时、刷新和关闭按钮](assets/preview.png)

*实际程序界面，使用示例数据。*

## 为什么用它

- **少一次点击**：5h 和周额度的剩余百分比常驻输入框上方，每 30 秒读取真实账户额度。
- **想知道还要等多久**：悬停 `⋯`，查看两项额度的重置倒计时，每秒更新。
- **让开你的操作**：鼠标进入文字区时隐藏，移开后恢复；出现待回答提问卡片时整条隐藏。
- **跟着窗口走**：每 50ms 跟随窗口位置，每 250ms 校准输入框布局，给按钮区留出空间。
- **轻量常驻**：额度读取子进程在刷新后退出，减少长期驻留的内存。
- **按需自启动**：可监听 Codex 应用启动事件，不修改快捷方式，不增加 Windows 开机或登录触发器。

## 快速开始

需要 Windows 10 1703+ / Windows 11（x64）、.NET Framework 4.8，以及已登录 ChatGPT 账户的 Codex 桌面版与本机 Codex CLI。

1. 从 [Releases](https://github.com/Leson1357/codex-quota-overlay/releases/latest) 下载 `CodexQuota-windows-x64.zip` 并解压。
2. 保留 `CodexQuota.exe.config` 与 `CodexQuota.exe` 在同一个目录。
3. 打开 Codex，双击 `CodexQuota.exe`。

额度来自当前本机 Codex 登录账号。程序通过 Codex app-server 读取数据，不需要另填 API Key。读取失败会标记“未更新”，并自动重试。

**v1.0.1 修复自动刷新**：Codex 更新并移除旧 CLI 时自动重新定位；没有显式代理环境变量时，读取进程使用 Windows 系统代理。已有 `HTTPS_PROXY` / `ALL_PROXY` 优先，连接失败的具体原因可在额度提示中查看。

同时兼容输入框聚焦时新增的 CSS 类名，避免聚焦后悬浮条消失。

| 按钮 | 功能 |
| --- | --- |
| `⋯` | 悬停查看 5h、周额度重置倒计时 |
| `↻` | 立即刷新额度 |
| `×` | 退出悬浮条 |

三个按钮所在的区域不会触发鼠标避让。再次启动会复用现有实例。

### 随 Codex 启动

把解压后的程序放到固定位置，再运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\enable-codex-autostart.ps1
```

它创建当前用户的 `CodexQuota-OnCodexStart` 计划任务，匹配 Codex 的应用进程启动事件，约 3 秒后显示悬浮条。任务不保存密码、不要求管理员运行程序。

取消此功能：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\disable-codex-autostart.ps1
```

安装后移动程序目录，需要在新位置重新执行安装脚本。只有生成对应 Windows 应用启动事件的入口才会触发自动启动。

## 从源码构建

不需要 NuGet 包或额外 SDK。使用 Windows 内置的 .NET Framework C# 编译器：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\tests\Test.ps1
```

构建结果为根目录的 `CodexQuota.exe`，运行时仍需同目录的 `.exe.config`。自动测试使用本地假 stdio 服务，不登录账户、不访问网络。

## 内存优化做了什么

额度读取进程从“整个会话常驻”改为“刷新期间存在”；同时复用前台窗口对象、减少 UI Automation 缓存内容，释放计时器、字体、提示框和子进程。

本机一次运行采样中，工具进程树的私有提交内存从旧实例约 **100.3 MiB** 降到最终版本空闲时平均约 **38.1 MiB**。刷新期间仍会有短暂峰值。读取进程需要重新初始化，这是降低常驻内存的取舍。

数值不是所有机器上的保证；原实例与新版的运行时长不同，不能视为严格基准测试。[完整验证记录](VALIDATION.md) 提供采样方法和边界。

## 实现与兼容性

- WinForms 绘制界面；Windows UI Automation 定位输入框与提问卡片。
- 通过本机 `codex app-server --stdio` 的 `account/rateLimits/read` 读取额度；依赖网络和已有账户登录。
- 原生管道以无 BOM 的 UTF-8 传输 JSON，不依赖系统控制台编码。
- 按 `windowDurationMins` 识别 5h 和周窗口，使用服务器提供的 `resetsAt` 计算倒计时。
- 启用 PerMonitorV2 DPI 支持，并使用目标 Codex 窗口的 DPI 计算位置和间距。

客户端更新可能改变无障碍界面的标记，影响定位或提问卡片识别。本机已检测到实际待回答卡片及回答后的消失；真实跨显示器拖动和完整 Codex 关闭重开流程尚未验证。

## 项目结构

```text
CodexQuota.cs                    界面、跟随、避让与额度读取
WindowsStdio.cs                  无 BOM UTF-8 管道及读取进程生命周期
CodexQuota.exe.config            每显示器 DPI 支持
app.manifest                    Windows 兼容声明
build.ps1                       构建脚本
enable-codex-autostart.ps1        应用启动事件任务
disable-codex-autostart.ps1       删除自动启动任务
tests/                          无网络回归测试
assets/preview.png              界面预览
VALIDATION.md                   检查记录和内存采样
```

本项目独立维护，与 OpenAI 无隶属关系。依据 [MIT 许可证](LICENSE) 开放使用和修改。

参考：[Codex app-server](https://learn.chatgpt.com/docs/app-server)、[Windows Forms 高 DPI 支持](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/high-dpi-support-in-windows-forms?view=netframeworkdesktop-4.8)。
