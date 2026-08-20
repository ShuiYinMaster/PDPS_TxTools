# TxToolsMcpBridge — Process Simulate MCP 桥

让通用智能体（opencode / Claude Code / Codex / 其它支持 MCP 的 Agent）通过
**MCP (Model Context Protocol)** 直接控制 Process Simulate。

```
智能体(opencode/claude/codex…)
   │  MCP stdio (stdin/stdout, JSON-RPC 2.0)
   ▼
TxToolsMcpBridge.exe（本桥，独立进程）
   │  命名管道 TxAgent_PS_<pid>（复用 TxAgent 既有 RPC 协议）
   ▼
PS 进程内 PsRpcServer → ToolRegistry → Tecnomatix.Engineering API
```

## 原理

Process Simulate 只有**进程内 SDK**，没有官方跨进程接口。TxAgent 插件已实现：
- 进程内 `PsRpcServer`（命名管道，收 `list_tools` / `invoke` / `ping`）
- 工具执行经 `PsContext` 封送回 PS 主线程（Tecnomatix 非线程安全）

本桥只是**协议翻译层**：把智能体发来的 MCP 请求翻译成 TxAgent RPC 帧，不碰
Tecnomatix 任何 API，纯 .NET Framework 4.8 + Newtonsoft.Json。

## 编译

```powershell
# 需要 VS 的 MSBuild（本仓库 Debug 版 TxTools.csproj 直接输出到 PS 安装目录）
& "D:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" McpBridge\McpBridge.csproj /p:Configuration=Debug
```

产物：`McpBridge\bin\Debug\TxToolsMcpBridge.exe`

> ⚠️ `PsRpcServer.list_tools` 的增强（返回 description / inputSchema）需要
> **重新编译 TxTools.dll 并重启 Process Simulate** 才会生效。否则桥仍能工作，
> 只是工具描述为空、参数为空对象。

## 使用

```powershell
# 默认：主控实例（无主控时唯一存活实例）
TxToolsMcpBridge.exe

# 指定实例（环境名或 pid）
TxToolsMcpBridge.exe --instance "PS"
TxToolsMcpBridge.exe --instance 21116

# 只读模式：只暴露只读工具，禁止任何写操作
TxToolsMcpBridge.exe --readonly
```

不要直接当普通程序跑（它在 stdin/stdout 上说话）。它由智能体的 MCP 配置启动。

## 接入配置

### opencode（opencode.json 的 mcp 段）

```json
{
  "mcp": {
    "tx-tools": {
      "type": "local",
      "command": ["D:/Program Files/Tecnomatix_2402/eMPower/DotNetCommands/TxTools/TxToolsMcpBridge.exe"]
    }
  }
}
```

### Claude Code

```bash
claude mcp add tx-tools -- "D:/Program Files/Tecnomatix_2402/eMPower/DotNetCommands/TxTools/TxToolsMcpBridge.exe"
```

### Codex CLI

```bash
codex mcp add tx-tools -- "D:/Program Files/Tecnomatix_2402/eMPower/DotNetCommands/TxTools/TxToolsMcpBridge.exe"
```

### 通用（mcp.json / .mcp.json 等支持 stdio 的客户端）

```json
{
  "mcpServers": {
    "tx-tools": {
      "command": "D:/Program Files/Tecnomatix_2402/eMPower/DotNetCommands/TxTools/TxToolsMcpBridge.exe",
      "args": []
    }
  }
}
```

## 能力说明

- `tools/list` 返回 PS 实例里 ToolRegistry 的全部工具（名称、描述、JSON Schema、
  只读标记）。`--readonly` 时只暴露只读工具。
- `tools/call` 转发到 PS 执行，返回工具输出文本。
- 写工具（`IsReadOnly=false`）同样暴露——执行时受 PS 自身 undo 保护（Ctrl+Z）。
- 多实例：默认控制主控实例；`list_environments` 工具可枚举所有实例，
  `run_in_environment` / `compare_environments` 可跨实例操作。
- 实例不可达时：`tools/list` 退化为占位工具 `ps_connection_status`，供排查连接。

## 目录

```
McpBridge/
  McpBridge.csproj    项目文件（.NET Framework 4.8）
  Program.cs          入口 / 参数解析
  InstanceDiscovery.cs 读取 %TEMP%\TxAgent.Instances\*.json 发现实例
  PipeClient.cs       命名管道客户端（帧协议与 PsRpcServer 对齐）
  McpServer.cs        MCP stdio server（JSON-RPC 2.0 / tools / ping / shutdown）
```