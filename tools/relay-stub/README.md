# relay-stub：只读采集验证用最小宿主桩

仅为独立 `SoulLink.Agent.exe --relay <pipe>` 提供命名管道生命周期，不执行任何键鼠或输入操作。
它创建 `<pipe>` 和 `<pipe>_Events`，对命令通道的每行请求只返回一个 `Ok=true` 的协议响应。

```powershell
dotnet run --project tools/relay-stub/RelayStub.csproj -c Release -- --pipe SoulLink.Verify --duration 30
```

`--record-on-connect` 可在连接后只发送一次 `record` 命令，启动代理自己的只读抓屏；`--fps N` 默认 15，可降到 3 做低负载验证。`--save-after N` 在 N 秒后发送一次 `save`，用于录像验收。没有这些开关时不会主动发送命令。`--duration` 最大 1800 秒，到期自动退出；日志为 `%TEMP%\soullink-relay-stub-<PID>.log`。
此工具不是生产宿主，不得连接真实账号的主管道，也不得用来下发输入。
