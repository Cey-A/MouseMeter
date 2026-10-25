# MouseMeter 工作流 —— 使用说明

> 目的：把「改完代码还要手工重新整合」变成一条命令。
> 放进项目根目录即可，不改动任何现有源码。

---

## 一、这四个脚本解决什么问题

以前的流程是三件独立的事（编译、跑测试、出 exe），**没有任何机制强制你做**。
结果就是：改动"看起来没问题"时最容易跳过验证，而恰恰那时是错的。

现在只有两个入口：

| 你要做的事 | 命令 | 做什么 |
|---|---|---|
| 改着看效果 | `dev.cmd` | 增量编译 → 旧实例退出 → 启动新版本（1~2 秒） |
| **准备提交 / 交付** | **`check.cmd`** | 编译 → 全部测试 → 出 exe → 同步文档数字 |
| **更新本机正在跑的 exe** | **`update.cmd`** | 停旧实例 → 拉取 → 验证 → 重编 exe → 重新启动 |

---

## 二、文件清单

| 文件 | 必需 | 作用 |
|---|---|---|
| `check.cmd` | ✅ | 唯一入口。双击即跑完整流程，失败就停下并说明原因 |
| `update.cmd` | 建议 | 一键换掉本机正在跑的 exe：停实例 → 拉取 → 验证 → 重编 → 启动 |
| `Verify.ps1` | ✅ | 六道检查：编码 / C#5 语法 / 依赖白名单 / 编译 / 测试 / 出包 |
| `Sync-Docs.ps1` | ✅ | 文档数字自动生成（断言数、最大文件行数） |
| `Install-Hooks.ps1` | ✅ | 一次安装，之后 `git commit` 前自动校验 |
| `.gitignore` | 建议 | 排除 `dist/`、`.verify-last`、日志等 |
| `tools\check-syntax.ps1` | 建议 | PowerShell 7 / Linux 环境下的离线编译与测试 |
| `tests\TestRunner.cs` | ✅ | 单元测试入口，「88 项断言」依赖此文件 |

`verify-last`（无点号）= 资产库里的 `.gitignore` 副本，存盘时改回 `.gitignore`。

**重要**：这些脚本放进项目根目录后，**必须一起提交到仓库**，否则 `git clone` 下来的新环境没有钩子也没有验证。

---

## 三、安装：三步

```powershell
# 1. 把这些文件放到项目根目录（和 Build.ps1 同级）
#    注意：tests\TestRunner.cs 和 tools\check-syntax.ps1 也要一起放，不能丢
# 2. 初始化版本控制
git init
git add .
git commit -m "baseline" --no-verify     # 钩子还没装，先绕过

# 3. 把这些脚本提交进仓库，否则 clone 下来的环境没有验证
git add check.cmd update.cmd Verify.ps1 Sync-Docs.ps1 Install-Hooks.ps1 .gitignore
git add tools\check-syntax.ps1 tests\TestRunner.cs
git commit -m "chore: add verification workflow"

# 4. 装钩子
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-Hooks.ps1
```

之后每次 `git commit` 会自动跑 `Verify.ps1 -Quick`（跳过出包，因为 exe 不进仓库）。
**这才是"不用记得跑验证"的保证**——钩子拦在提交之前，红的树进不了历史。

---

## 四、check.cmd 参数

| 参数 | 作用 |
|---|---|
| 无 | 完整流程 |
| `-q` | 跳过出包（快，适合纯逻辑改动） |
| `-n` | 跳过文档同步 |
| `-fix` | 自动给缺 BOM 的 .ps1 补上 |

---

## 四之二、update.cmd 参数

`check.cmd` 管「代码对不对」，`update.cmd` 管「本机跑的是不是新代码」。两者分工不同：
`check.cmd` 出 `dist\MouseMeter.exe` 时会因为**旧 exe 正在运行、文件被写锁**而失败，
所以想更新本机正在用的那份，走 `update.cmd`。

| 参数 | 作用 |
|---|---|
| 无 | 停旧实例 → `git pull` → `Verify.ps1 -Quick` → `Build.ps1` → 启动新 exe |
| `-q` | 跳过验证，直接重编（只改了资源 / 文案时用） |
| `-n` | 跳过 `git pull`（离线，或本地改动不想被覆盖） |
| `-k` | 编完不启动（想自己手动开时用） |

两个细节值得知道：

- **先 `CloseMainWindow` 再 `Stop-Process`**。程序正常退出时会存盘（挂件位置、材质滑块），
  强杀会丢这两项。给它 3 秒优雅退出的机会，超时才强杀。
- **验证在编译之前**。`Verify.ps1` 红了就直接 `exit /b 1`，此时旧 exe 一根手指都没碰过，
  不需要回滚——这是把失败挡在"覆盖"之前，而不是覆盖之后再去补救。

脚本是纯 ASCII + CRLF，无 `setlocal` / 无延迟展开，错误分支全部 `goto` 扁平平铺
（`if (...)` 里嵌套三层再用 `endlocal & exit /b` 在 cmd 里非常容易踩坑）。

---

## 五、六道检查分别防什么

每条都对应一次真实踩坑，不是凑数：

1. **编码闸门** — `.ps1` 缺 BOM 时 PowerShell 5.1 按 GBK 解码，中文注释会连带把数组初始化搞坏
2. **语法闸门** — `$/nameof/?./using static` 这类 C# 6+ 语法，CodeDOM 只认 C# 5，本地能编、真机报错
3. **依赖闸门** — `Assembly.Load` 越出四个白名单程序集（System / System.Core / Drawing / Forms）
4. **编译** — CodeDOM 编译，失败直接给行号
5. **测试** — 编出独立测试 exe 跑全套断言，红了一行给出 `FAIL` 摘要
6. **出包** — Build.ps1 生成 exe，并**校验修改时间已刷新**（只判存在会被旧文件骗过）

---

## 六、文档数字为什么是自动的

`README.md`、`START-HERE.md`、`docs/*.md` 里有这种标记：

```
<!-- AUTO:assertions -->88 项<!-- /AUTO -->
```

`Sync-Docs.ps1` 只替换标记之间的内容，标记外的所有文字保持原样。

**关键取舍**：断言数不从源码正则统计，而是读 `Verify.ps1` 写下的 `.verify-last`。
原因：正则试过，因为 `Equal<T>(...)` 泛型参数里有逗号，得到 48 而实际是 88。
一个"看起来合理但错误"的数字比没有数字更糟。

同理，`ROADMAP.md` 里"`SettingsForm.cs` 669 行"那处**故意没加标记**——
那句话讲的是特定文件，用全局"最大文件"覆盖会变成 `Program.cs`，语义就错了。

---

## 七、已知风险（首次运行请手动执行）

这四个脚本是在 Linux 环境下写的，**未在 Windows 上真实执行过**。
已做的验证：括号配对、BOM、`.cmd` 纯 ASCII + CRLF、以及用 Python 复刻 `Sync-Docs` 的统计与替换逻辑并跑通。

**未验证**：PowerShell 运行时行为（`MatchEvaluator` 委托、`$LASTEXITCODE` 跨进程传递）。

所以第一次请**手动跑 `check.cmd`**，不要直接 commit 让钩子去试错。
哪一步 FAIL，把输出贴出来按行修。

| 可能的失败点 | 症状 | 处理 |
|---|---|---|
| 测试 exe 编译 | `MainClass` 找不到入口 | 检查 `tests\TestRunner.cs` 是否在根目录，且 `TestRunner.Main` 为 public static |
| 缺测试文件 | 断言数为 0 或报错 | 确认 `tests\TestRunner.cs` 已随脚本一起复制并提交 |
| `$LASTEXITCODE` | 测试显示 PASS 但实际有失败 | 改为解析输出里的 `failed:` 计数 |
| `MatchEvaluator` | 文档标记没被替换 | 改成 `foreach` 匹配替换，不用委托 |
