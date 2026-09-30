# MiniClip —— 轻量剪贴板历史（Windows）

每次复制都自动记下来，并带上"什么时候、从哪个程序、哪个窗口"这些信息。
**单文件 70 KB，零安装、零依赖、不联网**；常驻空闲 CPU 实测 0 ms。

> 仓库地址（开源前替换）：`<把你的仓库链接放这里>` · 许可证：MIT · 变更见 [CHANGELOG.md](CHANGELOG.md)

---

## 1. 拿起来就用

下载 `MiniClip-<版本>-win-x64.zip`（或 `-setup.exe`），**解压到任意文件夹，双击 `install.cmd`**：
自动创建桌面/开始菜单快捷方式、登记开机自启、并立即运行。

只想临时跑一次：直接双击 `MiniClip.exe`（或 `start-clipboard.cmd`）——**会立刻打开历史面板**，同时常驻托盘。
开机自启用的是 `--silent`，登录时只安静进托盘、不打扰你。

装好后看右下角托盘，出现剪贴板图标即在运行；首次启动会弹一条提示告诉你热键是什么。

**唤出历史：`Ctrl + Alt + V`**（或双击托盘图标）

| 面板内 | 作用 |
| --- | --- |
| 直接打字 | 搜索（内容 / 来源程序 / 窗口标题） |
| `↑ ↓` + `Enter`（或双击） | 把选中条目放回剪贴板并收起（文本/图片/音频/文件都可以） |
| `Alt + 1..9` | 不选直接取用第 N 条 |
| `Ctrl + C` | 取用但不收起面板 |
| `Del` | 删除该条 |
| `Ctrl + P` | 置顶（不被淘汰，标 ★） |
| `Ctrl + F` / `Esc` | 回到搜索框 / 收起 |

托盘右键：暂停记录、清空历史（保留置顶）、打开数据目录、**关于 / 诊断信息**、退出。
卸载：双击 `uninstall.cmd`（退出程序 + 删快捷方式与自启项，历史数据保留）。

## 2. 每条记录带什么

```
id=1  ts=1790683313515  time=2026-09-29 20:01:53  kind=1(文本/图片/文件)
chars=13  size=13  hits=1  pin=0  hash=0be6a4dc040ee454
app=chrome.exe                      ← 来源程序
title=订单页面 - Google Chrome       ← 复制瞬间的前台窗口标题
path=C:\...\chrome.exe              ← 来源程序完整路径
fmt=CF_UNICODETEXT;HTML Format;...  ← 当时剪贴板上的格式清单
preview=DIAG-XYZ-9527               ← 展平后的内容预览
```

支持的内容类型：
- **文本**：中文、多行、CRLF 原样保真
- **图片**：复制图片内容（CF_DIB/CF_BITMAP）转 PNG 存盘；复制图片**文件**时也会把文件本体快照一份
- **音频**：复制音频内容（CF_WAVE/CF_RIFF）存成 WAV；复制音频文件同样快照本体
- **文件列表**：记录路径清单

> 快照这一步很关键：聊天软件的图片/语音往往落在临时目录，源文件被清理后，只记路径的历史就成了一条打不开的死记录。

## 3. 数据在哪里

**就在程序旁边的 `data\` 文件夹，不写 C 盘**——整个文件夹拷走即迁移。
可用环境变量 `MINICLIP_DATA=<目录>` 改位置；只有程序目录不可写时才退回 `%LOCALAPPDATA%\MiniClip`。
细节与隐私说明见 [docs/privacy.md](docs/privacy.md)。

## 4. 性能实测（本机数据，第 6 节可复现）

| 指标 | 值 |
| --- | --- |
| 体积 | `MiniClip.exe` 70 KB 单文件；无安装、无运行库依赖 |
| 构建 | 系统自带 `csc.exe`，全量编译 130–210 ms |
| 启动到就绪 | 64–75 ms（含读历史）；2000 条历史冷加载 38–40 ms |
| **常驻空闲 CPU** | 30 秒三次独立采样 **0 / 15 / 0 ms**（偶发小峰来自系统消息与 GC，非持续） |
| 对照组 200ms 轮询 | 31 ms / 25s + 125 次系统唤醒 → 所以默认事件驱动，0 次唤醒 |
| 收起面板后内存 | 干净空闲 working set **3–6 MB**（未用面板时最低 0.34 MB），私有提交 ~22–26 MB |
| 句柄/线程 | 30 次开合面板 + 60 次复制：411 → 409 → 405 句柄（增长停止），空闲 7 线程 / 290 句柄 |
| 写入吞吐 | 1.96 万–2.98 万条/秒，每条即时 flush 落盘 |
| 端到端延迟 | 实测 **p50 = 94–96 ms**（min 92 / max 132，15/15 全部命中）= 防抖 90 ms + 读取落盘 <6 ms |
| 压缩回收 | 1.20 MB 写入 → 压缩后 0.64 MB，条目与正文完整可读 |

优化做了什么，见 [docs/architecture.md](docs/architecture.md) 与下面第 5 节。

## 5. 目录结构

```
MiniClip.exe              程序（构建产物）
src/                      程序源码（11 个 .cs，一次 csc 调用全量编译）
tools/ResEdit.cs          构建期小工具：注入 exe 文件属性（不需要 rc.exe / SDK）
tests/                    可执行验收脚本（e2e / soak / seed-and-shot）
docs/                     架构、隐私说明
build.ps1 build.cmd       编译
release.ps1               打包：便携目录 + zip + SHA256（-Installer 生成单文件安装器）
install.cmd install.ps1    快捷方式 + 开机自启
uninstall.cmd             卸载
data/                     运行数据（.gitignore 已排除，绝不入库）
```

## 6. 自己验证，不用信我

```
MiniClip.exe --dump [n]         最近 n 条元数据（纯 ASCII 转义，便于 diff）
MiniClip.exe --get <id> [文件]  输出某条正文
MiniClip.exe --stat             运行实例的 CPU/内存/线程/句柄/条目数
MiniClip.exe --show|--hide|--quit     唤起面板 / 收起 / 退出实例
MiniClip.exe --silent          只常驻托盘，不弹主界面（开机自启用的就是这个）
MiniClip.exe --autostart on|off
MiniClip.exe --bench-capture 20 端到端延迟（p50/p95）
MiniClip.exe --bench-store 2000 写入吞吐 + 冷加载
MiniClip.exe --trace            每个环节写 data\trace.log
```

```
powershell -ExecutionPolicy Bypass -File tests\e2e.ps1    # 功能与元数据
powershell -ExecutionPolicy Bypass -File tests\soak.ps1    # 压力与泄漏
powershell -ExecutionPolicy Bypass -File tests\media.ps1    # 音频/图片内容、文件快照、不置顶、自动换行
powershell -ExecutionPolicy Bypass -File tests\deadlock.ps1 # 来源程序卡住时的自愈（含 clipput 工具）
powershell -ExecutionPolicy Bypass -File release.ps1      # 出包 + SHA256
```

e2e 覆盖：实例启动、热键注册、事件驱动监听、来源程序与窗口标题归属、中文+CRLF 正文逐字节保真、
格式清单、去重计数、回写剪贴板、自己写回不重复记录、面板开合、图片转 PNG、内存与线程平台。
soak 覆盖：淘汰、压缩回收、20 次开合后的句柄/线程平台、60 次连续复制、空闲 CPU、冷加载。

## 7. 配置（`data\config.txt`，改完重启生效）

```
max_items=2000        历史上限（超出淘汰最旧的非置顶条目）
debounce_ms=90        连续复制合并窗口；调小更跟手，调大更省
min_len=2             短于此长度的文本不记录（过滤误触的单字符）
max_chars=200000      单条正文上限，超出截断并标记
store_mb=24           content.log 上限，超出强制压缩
poll_ms=0             >0 切到轮询模式（只用于性能对照实验）
hotkey=ctrl+alt+v     全局热键；hotkey_enabled=0 可关
capture_image=1  capture_files=1  preview=1
autostart=1  win_w=1080  win_h=640  win_x/y  trace=0
```

## 8. 编译与二次开发

只需要 Windows：`build.cmd` 用系统自带的 `csc.exe` 编译，不装 SDK、不联网。
**注意语言级别是 C# 5**（in-box 编译器只到这里），约束与代码风格见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 9. 已知边界

- 来源识别取"复制瞬间的前台窗口"：脚本/命令行改剪贴板（本身无窗口）时，会记成你当时正看着的窗口。
- 部分 UWP/沙箱进程（`ApplicationFrameHost.exe`）只能记到宿主进程名。
- 富文本只保留纯文本；`fmt` 字段会告诉你当时剪贴板上还有哪些格式。
- **屏幕锁定期间**系统剪贴板不可用，程序记一条"剪贴板被占用，本次跳过"后继续工作，不会卡死。
- 提权窗口里的复制，普通权限进程读不到（Windows UIPI 限制）。
