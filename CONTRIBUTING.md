# 贡献指南

## 环境要求

**只需要 Windows。** 不装 SDK、不装 NuGet、不联网：

- 编译器：`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（Windows 自带）
- 运行库：.NET Framework 4.x（Windows 10/11 自带）
- 构建：`build.cmd` 或 `powershell -ExecutionPolicy Bypass -File build.ps1`（约 0.15 秒）

## 硬性约束：这是 C# 5

in-box 的 `csc.exe` 只支持到 C# 5，**没有** 这些语法：

| 不能用 | 替代写法 |
| --- | --- |
| `$"..."` 字符串插值 | `string.Format` / `+` |
| `a?.b` | `if (a != null)` |
| `switch` 匹配字符串 | `if / else if` |
| `nameof(X)` | 字面量 |
| 表达式体成员 `=> ` | 完整方法体 |
| `out var x` | 先声明再 `out x` |
| 元组、模式匹配、`using` 声明 | 回避 |

这不是保守，是"零依赖单文件"这个目标的直接代价。想改用 Roslyn / .NET 8 请先在 issue 里讨论，因为它会把"拷走就能跑"变成"先装运行库"。

## 目录

```
src/            程序源码（一个 csc 调用全量编译）
tools/ResEdit.cs  构建期小工具：给 exe 注入文件属性（无需 rc.exe/SDK）
tools/ClipPut.cs  测试期小工具：用原生 Win32 往剪贴板放 CF_WAVE / CF_DIB / 延迟渲染内容
                  （.NET 的 Clipboard 没有 SetAudioStream，而且会立即 flush，造不出这些场景）
tests/          可执行的验收脚本，见下
build.ps1       编译 → 图标 → 清单 → 版本资源
release.ps1     构建 + 组装发布目录 + zip + SHA256
docs/           架构与隐私说明
```

## 代码风格（本项目的特定取向）

- 采集路径上不做无谓分配：不用 LINQ / 正则 / `Trim()` 拷贝，UTF-16 就地解码，自写 FNV-1a。
- 剪贴板句柄的持有时间越短越好：`Cap.Read()` 里只拷字节，转换与落盘都在 `CloseClipboard` 之后。
- 空闲必须为 0：任何新增的定时器都要在窗口隐藏时停掉，任何新增线程都不能轮询。
- 中文注释解释"为什么"，不解释"是什么"。
- 源文件保存为 **UTF-8 with BOM**（`build.ps1` 会自动规范化，因为 GBK 环境下 csc 无 BOM 会读错中文）。

## 提 PR 前自测

```
powershell -ExecutionPolicy Bypass -File tests\e2e.ps1     # 功能与元数据正确性
powershell -ExecutionPolicy Bypass -File tests\soak.ps1    # 淘汰/压缩/句柄线程平台/空闲 CPU
powershell -ExecutionPolicy Bypass -File tests\media.ps1   # 音频与图片内容、文件快照、不置顶、自动换行
powershell -ExecutionPolicy Bypass -File tests\deadlock.ps1 # 来源程序不回数据时的看门狗自愈
```

两个脚本都会用独立数据目录（`data\test-run`、`data\soak-run`），不污染用户历史；
`e2e.ps1` 会备份并在结束时恢复你原来的剪贴板内容。

注意：脚本靠真实剪贴板与前台窗口工作，**屏幕锁定时会整体失败**（Windows 限制，不是 bug）。

## 发布检查清单

1. `src/Version.cs` 改版本号（界面标题、`--version`、文件属性都跟着走）。
2. `LICENSE` 与 `Ver.Copyright` 里的版权主体换成你的名字/组织。
3. `README.md` 顶部的仓库地址占位符。
4. `release.ps1` 产出 `release\MiniClip-<版本>-portable.zip` 与 SHA256，附在 release 说明里。
5. 换机器实测一次：双击 `MiniClip.exe` → 复制几段文字 → `Ctrl+Alt+V` 能取回。
