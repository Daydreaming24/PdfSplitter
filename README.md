# PDF 分片工具

Windows Forms 桌面应用，使用 .NET Framework 4.8 和本机 qpdf。双击 **PdfSplitter.exe** 即可使用，全部处理在本机完成。

## 下载使用

在 [Releases](../../releases) 页面下载最新版本。每个版本提供两种压缩包：

| 压缩包 | 内容 | 适合 |
| --- | --- | --- |
| `PdfSplitter-v版本号-win-with-qpdf.zip` | 程序 + qpdf 12.4.2，解压即用 | 大多数用户 |
| `PdfSplitter-v版本号-win.zip` | 仅程序 | 已安装 qpdf，或希望自行管理 qpdf 版本 |

解压后双击 `PdfSplitter.exe`。请保持压缩包内文件放在同一文件夹：`PdfSplitter.exe.config` 提供长路径支持，`qpdf.exe` 及其 DLL 需与程序位于同一目录才会被优先使用。

- 程序未进行数字签名，首次运行时 Windows SmartScreen 可能提示“Windows 已保护你的电脑”，点击“更多信息 → 仍要运行”即可。
- 每个 Release 附有压缩包的 SHA256 校验值，可用 `Get-FileHash .\压缩包.zip` 核对。
- 含 qpdf 的压缩包中附有 `THIRD-PARTY-NOTICES.txt` 和 `licenses\` 文件夹，列出 qpdf 及其依赖库的版权与许可证。

## 环境要求

- Windows 10/11，已安装 .NET Framework 4.8（系统通常自带）。构建使用系统自带的 `csc.exe`，无需安装 Visual Studio。
- [qpdf](https://github.com/qpdf/qpdf/releases)。含 qpdf 的压缩包已自带；否则可通过 `choco install qpdf` 安装，或将 `qpdf.exe` 放入 PATH、设置环境变量 `QPDF_EXECUTABLE`，也可在程序“设置”中手动选择。
- 仅运行测试时需要 Python 3 及 `reportlab`、`pypdf`。

## 使用方法

1. 点击“选择 PDF…”，或将一个 PDF 拖入窗口。也可复制文件后点击“粘贴文件”，或粘贴完整路径。支持 Ctrl+O / Ctrl+V。
2. 启动时起始页和结束页留空。选择 PDF 后，主页自动显示 **PDF 总页数**。有打开密码时填写密码框，页数会自动重新读取；读取期间可继续操作界面。每次载入 PDF，成功读取页数后，分片范围自动初始化为 **1–PDF 总页数**。
3. 在主页“分片模式”旁点击 **每页分片 / 块状分片** 切换，模式立即保存，输出位置和文件名预览同步更新。在下方“分片范围”填写 **起始页、结束页**，两种模式均只处理这个范围，有效范围自动保存并更新文件名预览。点击右上角“设置”可修改输出位置和同名处理方式；输出路径为只读展示，可选中复制。
4. 点击“开始分片”。完成后可查看实际生成的文件名并打开输出文件夹。

主页的输出路径以源 PDF 所在目录为基准，使用 Windows 相对路径：子文件夹显示为 `.\讲义_clips\`，源 PDF 所在目录显示为 `.\`，上级目录使用 `..\`。指定目录跨盘符或网络共享时显示完整路径；尚未载入 PDF 时，指定目录也显示完整路径。

分片模式、页码、命名规则、输出目录和冲突策略保存到程序旁的 `settings.xml`。下次启动恢复设置，主页页码保持空白；载入 PDF 并成功读取总页数后，范围重新初始化为整份 PDF。设置窗口的“取消”放弃该窗口内的修改；“恢复默认”重置该窗口内的选项，在保存后生效。分片模式和分片范围始终在主页调整，设置窗口的“恢复默认”保留这两项。旧版配置文件可以直接读取，新增选项使用默认值。

## 分片与输出

| 模式 | 输出内容 | 默认位置 | 默认文件名 |
| --- | --- | --- | --- |
| 每页分片 | 所选范围内每页一个 PDF，文件名保留原 PDF 页码 | 源 PDF 同级的 `{name}_clips` 文件夹 | `原文件名_page1.pdf`、`原文件名_page2.pdf`… |
| 块状分片 | 指定连续范围合成一个 PDF | 源 PDF 所在目录 | `原文件名_pages2-5.pdf` |

在主页“分片范围”一行填写**起始页、结束页**。页码从 1 开始，包含两个端点，起始页可等于结束页。离开页码输入框、按 Enter 或开始分片时，会自动修正输入：小于 1 的整数设为 1，大于 PDF 总页数的整数设为总页数，起始页大于结束页时交换两者。支持粘贴超大整数。载入成功后，空值和非整数显示警告，并禁用“开始分片”；修正后的范围同步保存并更新预览。总页数尚在读取时点击开始，会先完成页数读取和范围校正，再生成文件。正常情况下隐藏范围说明和输出策略小字，只保留输入错误、覆盖同名文件等必要警告。两种模式下均可编辑范围，切换模式时保留当前输入。每页分片按原 PDF 页码命名，例如范围 3–5 生成 page3、page4、page5；块状分片将这 3 页合成一个 PDF。载入新 PDF 或重新选择同一 PDF 时，范围默认初始化为 1 至 PDF 总页数；加密 PDF 在成功解锁后初始化。之后可按需调整范围。

块状文件后缀默认为 `pages{起始页}-{结束页}`（存为 `pages{start}-{end}`），例如 2 至 5 页生成 `原文件名_pages2-5.pdf`，页码外没有括号。也可设置为 `课堂重点` 或 `章节_{起始页}-{结束页}`；程序自动添加原文件名、下划线和 `.pdf`，已有 PDF 扩展名时只保留一次。

两种模式都可在设置中选择“指定文件夹”。该目录需要存在，可以包含已有文件和子目录。切换默认位置和指定位置时，设置窗口保留已输入的路径；保存默认位置后，下一次打开设置使用默认位置。

每页分片的默认文件夹使用已有行为：选“自动终止”时，自动创建的文件夹路径已存在就停止；选择自动编号或覆盖时，可复用已有文件夹，按文件执行相应策略。同名路径是文件时始终停止。使用“指定文件夹”可直接向已有目录输出，并按具体分片文件名检查冲突。

## 同名文件处理

“设置 → 同名文件”中，**每页分片**和**块状分片**各自选择：

- **自动终止**（默认）：任一输出与文件或文件夹重名时，整次操作停止，保留已有内容。
- **添加编号**：在 PDF 扩展名前添加空格和半角括号编号，例如 `讲义 (1).pdf`。

自动编号还可选择以下两种方式：

| 编号方式 | 规则 | 示例 |
| --- | --- | --- |
| 逐个递增（默认） | 仅重名的输出添加编号；从已有末尾编号的下一个数字开始，跳过占用的编号 | 已有 `讲义 (2).pdf`，继续尝试 `讲义 (3).pdf` |
| 整批从最大编号 + 1 开始 | 分片前检查本批输出；任一文件重名时，扫描所有相关文件名的编号，整批从最大值 + 1 起步 | 第 1 页已有 `(2)`，第 2 页已有 `(5)`，本批所有页面均从 `(6)` 开始 |

整批方式只统计与本次输出名称对应的文件和文件夹，忽略无关名称，比较时忽略大小写。没有重名时保持原计划文件名。支持识别末尾 `(数字)` 和 `（数字）`，保留名称中间的括号和非数字括号。自定义命名若使多个输出归属于同一名称，后续输出继续递增，保证本批文件名唯一。

分片前和保存前都会检查冲突。逐个编号会继续避开保存期间新出现的重名；整批编号在保存前重新计算基准。若整批保存中又出现冲突，则回滚本次输出并提示重试，下一次重新计算编号。命名预览展示基础名称，完成信息展示实际文件名。

编号的扩展名前 ` (数字)` 形式参考 [Chromium 下载路径处理](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/components/download/internal/common/download_path_reservation_tracker.cc)；末尾编号识别和整批最大编号是本工具的明确规则。

## 覆盖同名文件

“设置 → 同名文件”提供**自动覆盖同名文件**开关，默认关闭，适用于两种分片模式。开关打开时，设置窗口在开关下方显示一行红色警告，关闭时不显示；启用时还要求确认，确认框默认选“否”；开始分片时还会显示本次输出目录，再次要求确认。

开启后优先执行覆盖，暂停使用终止或编号策略。**被覆盖的原内容不会进入回收站**。源 PDF（包括指向源文件的硬链接）、只读文件、符号链接和同名文件夹受到保护，遇到时终止本次分片并说明原因；无关文件和子目录保持原样。

所有分片先在私有临时目录中生成，完整生成后再发布。覆盖通过文件替换和临时备份完成；发布失败或取消时回滚本次修改。若文件被其他程序占用而导致恢复失败，会保留临时目录及原文件备份，在错误信息中列出路径供恢复。成功后清理临时备份。此流程不提供进程强制结束或断电后的自动恢复。

## 命名与 qpdf

“设置 → 命名与 qpdf”可以调整：

| 项目 | 默认值 | 说明 |
| --- | --- | --- |
| qpdf 程序 | 自动查找 | 可浏览选择本机 qpdf.exe |
| 文件夹名称 | `{原文件名}_clips` | 用于每页分片的默认输出位置 |
| 文件名 | `{原文件名}_page{页码}` | 用于每页分片，自动补上 `.pdf` |
| 页码补零位数 | `1` | 页码不足该位数时前面补 0；设为 3 输出 001、002…，超长页码完整保留 |

设置窗口用中文记号显示命名规则：`{原文件名}` 是原 PDF 文件名（不含扩展名），`{页码}` 是页码，块状后缀另有 `{起始页}` 和 `{结束页}`。每个输入框右侧的“＋记号”按钮在光标处插入记号，下方一行实时显示该规则生成的示例文件名。设置窗口底部的“输出示例”同样是示例：文件名一律用占位的“原文件名”，保存位置按默认位置用文字描述（选择“指定文件夹”时显示所选路径）。当前 PDF 的实际输出位置和文件名在主页预览。`settings.xml` 和命令行始终使用 `{name}`、`{page}`、`{start}`、`{end}`，在输入框里直接键入这些写法同样有效。

页码补零位数大于 1 时，页码前面补 0（如 `001`），按文件名排序时不会出现 1、10、2 的顺序。命名规则和块状后缀均须为单个合法文件名，避免路径分隔符、末尾空格或句点。源文件名中的模板字符按原样保留。

程序自动查找 `QPDF_EXECUTABLE`、程序旁的 `qpdf.exe`、Chocolatey 安装目录和 PATH。两种模式均使用 `--pages . 起始页-结束页 --` 选择范围，每页分片再结合 `--split-pages=1` 生成单页文件。页面文字、图形、旋转和版式保留，文档级书签、表单等按 qpdf 的页面选择规则处理，相关文件建议核对结果。参见 [qpdf 页面选择说明](https://qpdf.readthedocs.io/en/stable/cli.html#page-selection)。

密码仅通过独立匿名管道作为标准输入传给 qpdf，使用无 BOM 的 UTF-8；支持 65001、936 控制台和无控制台启动，并保持宿主的控制台编码与输入流不变。使用后清空密码框，输出 PDF 无需密码打开。

## 构建与命令行

源码按职责分成若干模块，`build.ps1` 顶部的 `$sources` 明确列出全部源文件，新增模块时在那里登记即可。

| 文件 | 职责 |
| --- | --- |
| `PdfSplitter.cs` | 程序入口：选择命令行模式或图形界面 |
| `CommandLine.cs` | `--split` 的命令行解析，产出一份 `AppSettings` |
| `Splitter.cs` | 分片流程编排：校验、暂存、调用 qpdf、发布结果 |
| `QpdfRunner.cs` | qpdf 的查找与进程调用，密码经标准输入传递，支持取消 |
| `QpdfProcess.cs` | 本机进程启动、独立匿名管道与句柄回收 |
| `OutputPublisher.cs` | 同名规划、自动编号，以及发布失败时的回滚 |
| `AppSettings.cs` | 设置模型、XML 持久化、命名模板与校验 |
| `PageRange.cs` | 分片范围文本的解析与纠正（不依赖控件） |
| `Theme.cs` | 界面配色、字体与控件工厂 |
| `MainForm.cs` | 主窗口 |
| `SettingsForm.cs` | 设置对话框 |

分层约定：界面只调用 `Splitter` 的公开方法，不直接启动 qpdf 进程；`Splitter` 负责编排而不关心进程细节；`QpdfRunner` 与 `OutputPublisher` 各自封装一件事，便于单独替换或测试。

在仓库根目录运行：

```powershell
.\build.ps1
.\PdfSplitter.exe
```

若本机 PowerShell 的执行策略阻止构建脚本，可对这一次构建使用：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

命令行读取保存的设置，参数可覆盖本次运行：

```powershell
$job = Start-Process .\PdfSplitter.exe -ArgumentList '--split "D:\课程\讲义.pdf" --mode range --start-page 2 --end-page 5' -WindowStyle Hidden -Wait -PassThru
$job.ExitCode
```

支持以下选项：

- `--mode pages|range`：每页分片或块状分片。
- `--start-page 2 --end-page 5`：分片范围的两个端点，对两种模式都生效；省略时使用已保存的范围，无配置时为 1–1。命令行继续严格检查范围，自动初始化和修正适用于图形界面。
- `--range-suffix "pages{start}-{end}"`：块状文件后缀。
- `--output-directory "D:\已有文件夹"`：直接输出到指定目录。
- `--collision stop|rename|overwrite`：本次两种模式的同名处理策略。
- `--numbering next|max`：逐个递增或整批最大编号。
- `--confirm-overwrite`：明确确认覆盖；即使配置文件已启用覆盖，命令行也必须提供此参数。
- `--qpdf "C:\路径\qpdf.exe"`、`--settings "配置.xml"`、`--password-stdin`。
- `--folder-pattern "{name}_切片"`、`--file-pattern "{name}_p{page}"`、`--page-digits 3`。

成功退出码为 0，失败为 1。GUI 子系统程序在 PowerShell 直接调用时可能立即返回，脚本应使用 `Start-Process -Wait -PassThru` 等方式等待结束。

## 测试

测试分为两类，均需先运行 `.\build.ps1` 生成最新的 `PdfSplitter.exe`，并确保 qpdf 可用。测试 PDF 全部在运行时生成，仓库中不包含任何 PDF。

### Python 集成测试

通过命令行调用 `PdfSplitter.exe`，检查实际分片结果：

```powershell
python -m pip install reportlab pypdf
python .\tests\verify.py
python .\tests\verify_features.py
```

覆盖页面内容完整性、Unicode 路径、命名规则、分片范围、两种编号方式、覆盖确认、源文件保护、密码和临时目录清理。

### C# 测试程序

每个 `tests/*Test.cs` 是一个独立的控制台程序，引用 `PdfSplitter.exe` 测试内部逻辑与界面，成功时输出 `PASS: …` 并以 0 退出。

**1. 生成测试 PDF**（输出到 `tests/qa-output/fixtures/`）：

```powershell
python .\tests\make_fixtures.py
```

生成 12 页的 `sample.pdf`、3000 页的 `large.pdf`（用于取消测试），以及 7 页、以 AES-256 加密的 `encrypted.pdf`（密码 `中文 test & password`）。

**2. 编译**。使用统一脚本，`$out` 为输出目录：

```powershell
function Build-Test($name, $out) {
    & .\tests\build-tests.ps1 -Name $name -OutputDirectory $out
}
```

脚本为每个测试嵌入 `app.manifest`，生成以测试程序命名的 `.exe.config`，启用与主程序相同的长路径支持。发布和取消测试的暂存目录固定放在仓库的 `tests/qa-output/` 下，运行完自动清理，测试程序放在深目录也不会继续加深 qpdf 的工作目录。共享诊断入口先输出异常类型和消息，即使堆栈格式化失败也能看到失败原因。

**3. 运行**。非界面测试可共用一个输出目录：

```powershell
$fx = ".\tests\qa-output\fixtures"
$out = ".\tests\qa-output\core-tests"
foreach ($t in 'OutputDirectoryTest', 'OutputPolicyTest', 'CancellationTest', 'QpdfProcessTest') { Build-Test $t $out }
& "$out\OutputDirectoryTest.exe" "$fx\sample.pdf"
& "$out\OutputPolicyTest.exe" "$fx\sample.pdf"
& "$out\CancellationTest.exe" "$fx\large.pdf"
& "$out\QpdfProcessTest.exe"
```

界面测试会读写程序旁的 `settings.xml`，必须各自编译到 `tests/qa-output/` 下的一个**全新空目录**，以免影响本地设置：

```powershell
$fx = (Resolve-Path .\tests\qa-output\fixtures).Path
foreach ($t in 'OverwriteConfirmationTest', 'SettingsAndLayoutTest', 'HomePageTest') {
    $dir = ".\tests\qa-output\run-$t-$(Get-Date -Format yyyyMMddHHmmss)"
    New-Item -ItemType Directory $dir | Out-Null
    Build-Test $t $dir
}
# 再进入对应目录运行：
.\OverwriteConfirmationTest.exe "$fx\sample.pdf"
.\SettingsAndLayoutTest.exe "$fx\sample.pdf" shots      # 截图保存到当前目录的 shots\
.\HomePageTest.exe "$fx\sample.pdf" "$fx\encrypted.pdf"
```

| 测试 | 覆盖内容 | 参数 |
| --- | --- | --- |
| `OutputDirectoryTest` | 已有目录保护、运行期间出现的冲突、失败回滚、取消 | 任意 PDF（≥ 2 页） |
| `OutputPolicyTest` | 覆盖与新增文件混合发布的回滚、编号重新计算、范围分片取消 | 任意 PDF（≥ 2 页） |
| `QpdfProcessTest` | 65001 / 936 / 无控制台环境的密码原始字节、并发管道、标准流和编码保持不变 | 无 |
| `CancellationTest` | qpdf 运行中取消后清理临时目录、保留源文件 | 足够大的 PDF，确保取消时 qpdf 仍在运行 |
| `OverwriteConfirmationTest` | 启用覆盖时的“是/否”确认、默认选“否”、开始前拒绝覆盖 | PDF 路径 |
| `SettingsAndLayoutTest` | 设置持久化、模式展示、路径保留、命名记号显示与回写、各选项卡截图 | PDF 路径、截图输出目录 |
| `HomePageTest` | 主页范围校验、预览、密码修改后重读页数、实际分片、旧查询取消和关闭清理 | 12 页普通 PDF、7 页加密 PDF |

`HomePageTest` 会输出正在检查的阶段，遇到界面回调异常直接报告失败，并用独立的 90 秒看门狗终止卡住的测试；关闭测试窗口时会等待异步清理和窗口释放完成。

密码或进程调用改动后，在 `chcp 65001` 和 `chcp 936` 两种控制台下分别运行 `verify.py` 与 `HomePageTest`，每次主页测试都使用全新的隔离目录。

界面改动后请查看 `SettingsAndLayoutTest` 生成的截图。所有测试产物都位于 `tests/qa-output/` 等被忽略的目录中，不会进入版本库。

## 许可证

本项目以 [MIT 许可证](LICENSE) 发布。qpdf 是第三方程序，遵循其自身的许可证（Apache 2.0）；含 qpdf 的压缩包原样附带官方 Windows 版 qpdf 及其依赖库，各组件的许可证见包内 `THIRD-PARTY-NOTICES.txt`。
