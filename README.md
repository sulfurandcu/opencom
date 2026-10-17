# opencom

Windows 串口调试助手，基于 **WPF + MVVM**（.NET 8）。支持串口收发、HEX/ASCII 显示、
循环发送、触发匹配发送、Shell 交互模式。

> 详细设计见 [docs/DESIGN.md](docs/DESIGN.md)。

## 功能特性

- **串口**：端口枚举（数字序）、波特率 300-921600、数据位 5-8、NONE/ODD/EVEN/MARK/SPACE 校验、
  1/1.5/2 停止位、RTS/CTS 与 XON/XOFF 流控、DTR/RTS 手动置位；打开后参数条禁用
- **发送列表**（上限 200 行）：添加/插入/删除/上移/下移、HEX/ASC 格式、双击切换、
  组颜色、右键菜单（发送该行/循环发送/设为匹配行/匹配方式/设为触发发送行/查看匹配行）
- **发送**：发送选中、双击内容格发送、循环发送（次数/间隔，0=无限）、
  **循环发送全部**（每行独立次数列 + 轮数/行间间隔/轮间间隔）
- **触发发送**：行级匹配（完整一行收完才匹配），包含/相等/前缀三种模式，
  匹配行可显式关联触发行，无组数上限
- **接收窗口**：HEX/ASCII 显示、时间戳、方向标记、发送数据开关、
  暂停/恢复监视（恢复补显）、清空/复制/保存文本
- **智能断行**：按波特率自动断行（3 字节时间空闲即断行），低波特率不误断
- **Shell 模式**：键入即发、Tab 发 `0x09`、回车 EOL 可设（CRLF/CR/LF）、Esc 退出、Ctrl+C 发 `0x03`
- **日志**：运行日志与接收数据自动落盘（见下）

## 环境要求

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- VS Code + C# Dev Kit 或 AI Agent（均无需 Visual Studio）

## 构建与运行

```bash
cd wpf
dotnet build
dotnet run
```

## 配置与日志

所有文件位于 `C:\Users\<用户名>\.opencom\`：

| 文件 | 内容 |
|---|---|
| opencom.json | 串口参数、显示选项、发送列表（退出自动保存） |
| configs/\<名称\>.json | 命名配置（文件菜单保存配置/配置管理） |
| opencom.log | 运行日志 |
| opencom.txt | 接收窗口数据（与显示逐字一致） |

## 目录结构

```
docs/       设计文档
wpf/        应用源码（Models/Services/ViewModels/Views）
```
