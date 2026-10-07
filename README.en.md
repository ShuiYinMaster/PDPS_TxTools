# TxTools — Siemens Tecnomatix Process Simulate Plugins

[English](README.en.md) · [中文完整手册](README.md) · [Installation](#installation) · [Plugin directory](#plugin-directory)

TxTools is a C# plugin collection for **Siemens Tecnomatix Process Simulate (PDPS)**, focused on robotic welding simulation and offline programming. It includes robot weld path planning, reachability checks, weld spot allocation, CATIA and Excel export, an in-process AI assistant, and an MCP stdio bridge for external agents.

## Common workflows

- **Robot welding path planning:** generate approach, retract and via locations with [AutoPath](AutoPath/README.md).
- **Robot reachability checks:** inspect welding locations and joint-limit conditions with [RobotReachabilityChecker](RobotReachabilityChecker/README.md).
- **Weld spot allocation:** update coordinates, reuse reference operations or mirror weld points with [WeldSpotAllocator](WeldSpotAllocator/README.md).
- **CATIA export:** export weld guns and point clouds with [ExportGun](ExportGun/README.md), or export layout resources as CGR and mesh files with [ExportByColor](ExportByColor/README.md).
- **Excel annotation:** export viewport screenshots and weld-point labels with [WeldAnnotator](WeldAnnotator/README.md).
- **AI and automation:** query actual scene data and run reusable recipes with [TxAgent](Agent/README.md); expose its tools to MCP clients with [McpBridge](McpBridge/README.md).

## Requirements

- Windows and a licensed Process Simulate installation. The primary target is **Process Simulate 2402**; other versions require validation.
- **.NET Framework 4.8**, an x64 host and Visual Studio/MSBuild with the relevant build tools.
- Local Tecnomatix SDK assemblies. CATIA integrations also require the local CATIA COM environment; Excel annotation requires an active writable workbook.
- NuGet packages from `packages.config`. The JT decoder additionally needs the third-party DLLs described in [ExportByColor](ExportByColor/README.md#jt-解码器构建依赖).
- WebView2 for TxAgent; a configured model endpoint for chat and a local Python environment for Python execution.

The main project uses C# 8.0. Runtime C# scripts and recipes should remain compatible with C# 7.3.

## Installation

1. Clone [ShuiYinMaster/PDPS_TxTools](https://github.com/ShuiYinMaster/PDPS_TxTools).
2. Open `TxTools.slnx` in a compatible Visual Studio version, or open `TxTools.csproj` directly. Restore NuGet dependencies and prepare the required local SDK / COM references.
3. Set `ProcessSimulateDllDir` to the SDK directory. Build the **Release / AnyCPU** configuration; the main DLL targets **x64**.
4. The current project defaults to a machine-specific G: drive deployment directory. Override `OutputPath` and `OutDir` for a separate build output, as shown in the [Chinese build instructions](README.md#十三更新日志).
5. Copy the DLL, dependencies and companion resources to your Process Simulate `eMPower/DotNetCommands/TxTools/` directory.
6. Use the installation's `CommandReg.exe` to register the required command classes for Process Simulate, then add the commands to a toolbar through Customize.

Full registration and removal instructions are in the [Chinese installation guide](README.md#三快速安装与注册).

## Plugin directory

The linked module guides contain an English summary followed by detailed Chinese usage instructions.

| Module | Purpose |
|---|---|
| [Agent](Agent/README.md) | AI assistant, tools and reusable recipes |
| [AutoFance](AutoFance/README.md) | Fence generation from curves |
| [AutoPath](AutoPath/README.md) | Robot welding path planning |
| [AutoRecorder](AutoRecorder/README.md) | Simulation video recording |
| [CatiaPartTree](CatiaPartTree/README.md) | CATIA product-tree inspection |
| [CrossEnvIO](CrossEnvIO/README.md) | Cross-instance scene-data transfer |
| [DeviceZAligner](DeviceZAligner/README.md) | Experimental device Z alignment; known issue |
| [ExportByColor](ExportByColor/README.md) | CGR and mesh export by color |
| [ExportGun](ExportGun/README.md) | Weld-gun and point-cloud export |
| [LibPathSync](LibPathSync/README.md) | Library-directory synchronization |
| [LineToSolid](LineToSolid/README.md) | Curve-to-solid geometry generation |
| [McpBridge](McpBridge/README.md) | MCP stdio bridge for external agents |
| [MechArena](MechArena/README.md) | Mech-arena demonstration |
| [RobotBaseChecker](RobotBaseChecker/README.md) | Robot BASE0 validation and correction |
| [RobotReachabilityChecker](RobotReachabilityChecker/README.md) | Robot reachability and joint-limit checks |
| [SelectButton](SelectButton/README.md) | Point-selection shortcuts |
| [SnakeGame](SnakeGame/README.md) | Snake-game demonstration |
| [ThemeTuner](ThemeTuner/README.md) | Shared UI-theme customization |
| [WeldAnnotator](WeldAnnotator/README.md) | Weld-point screenshots and Excel annotation |
| [WeldGunDefiner](WeldGunDefiner/README.md) | X-type weld-gun kinematics wizard |
| [WeldPointMatrix](WeldPointMatrix/README.md) | Multi-operation weld-point matrix |
| [WeldSpotAllocator](WeldSpotAllocator/README.md) | Weld-coordinate updates and allocation |
| [WeldSpotGrouper](WeldSpotGrouper/README.md) | Weld grouping by assigned parts |

## TxAgent and MCP

TxAgent runs inside Process Simulate and marshals SDK calls to the host UI thread. It supports scene queries, tool execution, reusable parameterized C# / Python recipes, source-workspace tools, history and knowledge stores, and discovery of multiple Process Simulate instances.

Model providers and endpoints are configured in the UI. Model-driven modifications follow the tool approval path; direct recipe execution uses the existing execution lock and supported undo blocks. External file and application changes have their own recovery limits.

`TxToolsMcpBridge.exe` is a separate .NET Framework process. It translates **Model Context Protocol (MCP)** requests over stdio to TxAgent's named-pipe RPC service. See the [bridge guide](McpBridge/README.md) for client configuration, instance selection and read-only mode.

## Validation and project status

The 2026-10-07 source synchronization passed a full Release build, embedded-recipe verification and the recipe, focus, display-session and startup regressions recorded in the [changelog](README.md#十三更新日志). Existing compiler warnings remain. These checks do not establish that every plugin has been validated in a real PS / CATIA / Excel session.

The standalone DeviceZAligner plugin retains its documented unresolved runtime issue. See [its status](DeviceZAligner/README.md) before use.

## Development and license

See [CONTRIBUTING.md](CONTRIBUTING.md) for contribution guidance, the [Chinese developer manual](README.md) for SDK and geometry conventions, and [Issues](https://github.com/ShuiYinMaster/PDPS_TxTools/issues) for bug reports. Include the host version, reproduction steps and relevant logs when reporting a problem.

TxTools source is licensed under [MIT](LICENSE). Siemens SDK assemblies and other third-party components retain their own licenses; see [NOTICE.md](NOTICE.md) and the relevant component notices.


## Undo and recovery

Persistent scene edits use validated PS 2402 transaction grouping. A failed edit is not automatically rolled back: inspect its result and use Ctrl+Z in the target PS document when needed. Files, CATIA/Excel output and registry changes require their own recovery methods. Save/reload boundaries and legacy gun ownership are described in the [undo safety guide](docs/undo-safety.md).
