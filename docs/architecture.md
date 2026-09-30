# 架构与设计说明

## 模块

| 文件 | 职责 |
| --- | --- |
| `Program.cs` | 入口、单实例、命令行子命令、崩溃落盘、基准测试 |
| `Native.cs` | 全部 Win32 P/Invoke 与便捷封装（剪贴板、窗口、进程、热键、工作集） |
| `Monitor.cs` | 隐藏宿主窗口 + 剪贴板事件 + 防抖 + 归属抓取 + 后台读线程 |
| `Capture.cs` | 剪贴板读写（文本 / DIB / HBITMAP / HDROP），PNG 转换 |
| `Store.cs` | 追加式存储：`index.log` + `content.log` + 图片目录 + 淘汰 + 压缩 |
| `Item.cs` | 一条记录的元数据 |
| `Config.cs` | 配置、时间、转义、哈希等工具 |
| `MainForm.cs` | 托盘、虚拟列表、搜索、快捷键、回写 |
| `About.cs` | 关于 / 诊断信息面板 |
| `Trace.cs` `Probe.cs` | `--trace` 环节日志、`MINICLIP_DIAG=1` 计数器 |
| `tools/ResEdit.cs` | 构建期给 exe 注入 `VS_VERSIONINFO`（无 rc.exe 也能有文件属性） |

## 线程模型

```
UI 线程（隐藏宿主窗口 + 面板 + 托盘）
  ├─ WM_CLIPBOARDUPDATE(0x031D)  → 抓现场（前台窗口/进程）→ 重启 90ms 防抖定时器
  ├─ WM_HOTKEY / IPC 消息        → 唤出面板、退出、回写
  └─ 面板隐藏时：所有定时器停止，SetProcessWorkingSetSize(-1,-1) 归还内存

读线程（miniclip-io，BelowNormal，单条任务）
  └─ WaitAny(stop, wake) 无限等待 → OpenClipboard → 只拷原始字节 → CloseClipboard
     → 解码/转 PNG/算哈希/落盘（全在剪贴板锁外）→ BeginInvoke 通知 UI
```

关键点：**剪贴板是全机独占资源**。读线程即使卡住也不会影响界面；对未响应的来源程序用
`IsHungAppWindow` 提前跳过，`OpenClipboard` 有限重试（10×6ms）后放弃。

## 存储格式

`data/index.log` —— 一行一条，制表符分隔，字段值做 `\n \r \t \\ \uXXXX` 转义：

```
A  id ts kind app title chars size off bytes hash pin hits trim fmt path preview
U  id ts hits          去重命中：移到最前、时间刷新
P  id pin              置顶状态
D  id                  删除 / 淘汰（墓碑）
```

`data/content.log` —— 只有正文，按 UTF-8 转义后的字节流顺序追加；`index.log` 里记 `(off, bytes)`，
读取时 `Seek + Read`，因此**正文永远不进常驻内存**，界面只用 `preview`（80 字）渲染。

`data/images/<hash16>.png` —— 图片正文，文件名即内容哈希，天然去重；`content.log` 里存相对路径。
`data/media/<hash16>.<ext>` —— 音频正文，以及"复制图片/音频文件"时顺带存下的文件本体。
源文件（尤其聊天软件的临时目录）被清理后，历史里的媒体依然能取回，这是快照存在的唯一理由；上限 32 MB，超出只记路径。

`Kind` 取值：1 文本、2 图片、3 文件列表、4 音频。

加载 = 顺序折叠 `index.log`（`A` 头插，`U` 移到最前，`D` 删除）；
压缩 = 按当前 MRU 顺序重写两个文件 + 删除不再被引用的图片 + `File.Replace` 原子替换。

## 来源程序不回数据怎么办

延迟渲染（`SetClipboardData(fmt, NULL)`）意味着"数据等你上门再给"。如果那个程序不泵消息，
我们的读线程就会卡在 `GetClipboardData` 上。为此：

1. 读之前先 `IsHungAppWindow(clipboardOwner)`，对方已经卡住就直接跳过这次；
2. `OpenClipboard` 有限重试（10×6 ms）后放弃，绝不无限等；
3. **看门狗**：UI 线程每秒检查一次，读线程超过 4 秒没返回就记录 `CRITICAL`、
   `TerminateThread` 掉卡死的读线程并重建（此刻它不持有任何托管锁）；
4. 若仍判定剪贴板不可用，则自我重启（`Process.Start` + `Exit`）——剪贴板被冻住比少记一条严重得多。

实测（`tests/deadlock.ps1` + `tools/ClipPut.cs hang`）：`CRITICAL 读取卡住 4840ms → 终止读线程 →
剪贴板已恢复 → 之后照常记录`，期间其它程序读写剪贴板不受影响。

## 为什么不用 XXX

| 备选 | 不用它的原因 |
| --- | --- |
| Electron / Tauri | 一个剪贴板管理器背 80–200 MB 运行时，与"轻量"目标相反 |
| WinUI 3 / .NET 8 | 需要目标机安装桌面运行时；in-box csc 能做到拷走就跑 |
| SQLite | 需要原生 DLL 或额外托管包；追加式日志 + 偏移寻址在这个规模更快更简单 |
| PowerShell + WPF | 冷启动几百毫秒、常驻内存更高，且没有单文件产物 |
| 轮询 `GetClipboardSequenceNumber` | 每秒若干次无意义唤醒；实测对照见下 |

## 实测数据（本机，可用 `tests/` 复现）

| 指标 | 事件驱动（默认） | 200 ms 轮询（对照） |
| --- | --- | --- |
| 25 秒空闲 CPU | 0 ms | 31 ms |
| 25 秒系统唤醒 | 0 次 | 125 次 |

其它：启动到就绪 64–75 ms；2000 条历史冷加载 38–40 ms；写入吞吐 1.96–2.98 万条/秒；
收起面板后 working set 0.34–0.86 MB；20 次开合 + 60 次复制后句柄稳定在 405–408、线程 12–15。

## 已知取舍

- 富文本只存纯文本（`fmt` 字段保留了当时的格式清单，将来要加 HTML/RTF 正文不用改采集端）。
- 来源归属取"复制瞬间的前台窗口"：脚本类无窗口程序改剪贴板时，会记成当时用户正看着的窗口。
- 提权窗口的剪贴板内容，普通权限进程读不到（Windows UIPI），需要本程序同样以管理员运行。
