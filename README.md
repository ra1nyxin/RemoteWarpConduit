# 远程供给传送器 RemoteWarpConduit

《缺氧》“眼冒金星！”模组，让不同玩家的单人存档通过中继服务器互相发送资源。每个人仍在自己的世界中游玩，资源从原版供给传送器输入端进入，在对方选定的输出端流出。

An *Oxygen Not Included: Spaced Out!* mod that sends resources between separate single-player colonies through a relay server. Each player stays in their own game: materials enter a vanilla Supply Teleporter Input and leave through the selected player's Supply Teleporter Output.

这是一个仍在开发中的版本。普通液体传送已经过两名玩家的实际测试；其他物态、断线恢复和回档等情况仍需要更多测试。

This mod is under development. A liquid transfer has been tested between two players; other material states, reconnection and save rollback still need further testing.

## 支持的物资 / Supported Materials

- 普通气体、液体和固体元素，传递质量、温度、病菌种类和数量。
- 食物、种子、装备以及其他带有特殊状态的物品目前不支持。无法识别或恢复的物资不会被接收端确认为已送达。
- 对方缺少对应元素或病菌时，该投递会留在服务器队列中等待处理。

- Ordinary gas, liquid and solid elements, including their mass, temperature, disease type and germ count.
- Food, seeds, equipment and other items with special state are not currently supported. Cargo that cannot be identified or restored is not acknowledged by the receiver.
- If the receiving game lacks an element or disease, the delivery remains in the server queue.

## 使用方法 / How To Use

1. 双方安装并启用模组，进入各自的“眼冒金星！”存档。
2. 找到并激活供给传送器输入端和输出端。启用本模组后，激活仍需要复制人操作，但不再要求实地研究技能。
3. 选中输入端，在右下角配置面板中选择好友的输出端。列表显示玩家计算机名、殖民地、世界、IP 和在线状态等信息，请确认目标后再发送。
4. 将气体管道、液体管道或运输轨道连接到输入端；好友将对应管道或轨道连接到输出端。
5. 要恢复本存档内的原版星球传送，在输入端面板选择“切换到原版传送”。远程缓冲或待发送物资尚未清空时，暂时无法切换目标。

1. Both players install and enable the mod, then load their own *Spaced Out!* colonies.
2. Find and activate the Supply Teleporter Input and Output. Activation still requires a duplicant, but the Field Research skill is not required while this mod is enabled.
3. Select the input and choose a friend's output in its configuration panel. The list includes the player's computer name, colony, world, IP address and online status; check the destination before sending.
4. Connect gas pipes, liquid pipes or conveyor rails to the input. The receiving player connects the corresponding pipes or rail to the output.
5. Select the vanilla transfer option in the input panel to return to the original in-save teleporter. A destination cannot be changed while remote cargo is buffered or awaiting submission.

## 队列与存档 / Queues And Saves

发送方和接收方不必同时在线：服务器会保存待接收的投递。输出端堵塞、未激活或游戏暂停时，物资会等待；队列有容量限制，满时发送端会等待而不是无限接收。发送方退出后，接收方仍可能继续收到此前排队的物资。

接收记录会在接收方成功保存游戏后向服务器确认。已经确认的物资在接收方回到较早存档后不会自动补发，需要重新发送。发送方回档可能恢复已经发出的物资，这种情况允许发生。网络中断、异常退出及回档的所有组合尚未完成实测，重要存档建议先备份。

输入端和输出端的配置面板显示连接、缓冲及服务器队列状态。可在面板中两次点击清除当前目标的服务器排队投递；清除不可撤销，只作用于查询到的投递，不清空本地缓冲或已接收物资。输入端只能清除自己发给该目标的队列，输出端可以清除发往该输出端的队列。队列数字包含已接收但尚未存档确认的投递，输出端清除时会跳过这些投递。

Players do not need to be online at the same time: the relay stores pending deliveries. Cargo waits when the output is blocked or inactive, or while the receiving game is paused. The queue is bounded, so the sender waits when it is full. Queued cargo can continue arriving after the sender exits.

The receiver acknowledges deliveries after a successful game save. Confirmed cargo is not automatically resent when the receiving player loads an earlier save; it must be sent again. Rolling back the sender may restore already-sent materials, which is allowed. Not every combination of disconnection, unexpected exit and rollback has been tested; back up important saves before testing.

The input and output panels show connection, buffer and server queue status. Click twice to clear the displayed server queue; this cannot be undone and affects only the listed deliveries, not local buffers or received cargo. Inputs can clear their own deliveries to the selected output; outputs can clear deliveries addressed to them. Queue counts include received deliveries awaiting a save, which are excluded when clearing from the output.

## 中继服务器 / Relay Server

仓库中的 `relay.py` 使用 Python 标准库和 SQLite，可在 Windows、Debian 和 Ubuntu 上运行，不需要安装第三方 Python 包：

The repository's `relay.py` uses the Python standard library and SQLite. It runs on Windows, Debian and Ubuntu without third-party Python packages:

```bash
python relay.py
```

默认监听 TCP `53573`，并在脚本旁创建 `remote_warp.sqlite3`。服务器控制台记录连接和投递事件。可用 `--host`、`--port` 和 `--database` 覆盖服务器参数；客户端当前把默认服务器 IP 和端口写在源码中，更换服务器需要修改 `src/RelayClient.cs` 并重新编译。

日志同时写入脚本旁的 `remote_warp_relay.log`，按本地日期轮转并保留当天及之前六天；可通过 `--log` 指定日志路径。队列清除功能需要新版客户端和新版中继配合使用。

The default listener is TCP `53573`; `remote_warp.sqlite3` is created beside the script. Connection and delivery events are written to the server console. Use `--host`, `--port` and `--database` to override server settings. The client currently hardcodes its default server IP and port; changing the server requires editing `src/RelayClient.cs` and rebuilding.

Logs are also written to `remote_warp_relay.log` beside the script, rotated by local date and retained for the current day and previous six days. Use `--log` to change the log path. Queue clearing requires both the updated client and relay.

当前中继没有身份验证或加密。连接者可以查看客户端列表及其中的 IP、计算机名和世界信息，也可以冒充客户端或投递物资；请在部署和使用时考虑这一限制。

The relay currently has no authentication or encryption. Connected clients can see the client list, including IP addresses, computer names and world information, and can impersonate clients or submit cargo. Keep this limitation in mind when deploying and using it.

## 构建 / Build

项目目标为 .NET Framework 4.8，需要本机安装游戏及其托管程序集。游戏安装在其他位置时，可在项目文件中调整 `OniManagedDir`，也可以在 `dotnet build` 时通过同名 MSBuild 属性指定。

The project targets .NET Framework 4.8 and requires the game's managed assemblies. If the game is installed elsewhere, adjust `OniManagedDir` in the project file or pass it as an MSBuild property to `dotnet build`.

```powershell
.\build.ps1
```

脚本构建后会把 DLL、`mod.yaml` 和 `mod_info.yaml` 安装到当前用户的开发模组目录：

The script builds and installs the DLL, `mod.yaml` and `mod_info.yaml` into the current user's development mod directory:

```text
%USERPROFILE%\Documents\Klei\OxygenNotIncluded\mods\Dev\RemoteWarpConduit
```

创意工坊上传包只需模组 DLL、两个 YAML 文件，以及可选的 `preview.png`；不要上传游戏程序集或中继数据库。

For a Workshop upload, include the mod DLL, both YAML files and optionally `preview.png`. Do not upload game assemblies or the relay database.
