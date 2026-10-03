$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
$outputFile = Join-Path $PSScriptRoot 'PdfSplitter.exe'
# Compilation unit list. Add new modules here.
$sources = @(
    'PdfSplitter.cs',      # 入口：选择命令行或图形界面
    'CommandLine.cs',      # --split 的命令行解析
    'Splitter.cs',         # 分片流程编排
    'QpdfRunner.cs',       # qpdf 的查找与进程调用
    'QpdfProcess.cs',      # qpdf 的匿名管道与本机进程句柄
    'OutputPublisher.cs',  # 输出发布与回滚
    'AppSettings.cs',      # 设置模型、持久化与命名规则
    'PageRange.cs',        # 分片范围的解析与纠正
    'Theme.cs',            # 界面配色与控件工厂
    'MainForm.cs',         # 主窗口
    'SettingsForm.cs'      # 设置对话框
) | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output "/out:$outputFile" "/win32manifest:$PSScriptRoot\app.manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $sources
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
Write-Output "Built: $outputFile"
