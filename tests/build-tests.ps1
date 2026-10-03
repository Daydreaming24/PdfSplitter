param(
    [Parameter(Mandatory = $true)]
    [string[]] $Name,
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
if ($output.TrimEnd('\') -ne $root.TrimEnd('\')) {
    Copy-Item -LiteralPath "$root\PdfSplitter.exe", "$root\PdfSplitter.exe.config" -Destination $output
}
foreach ($test in $Name) {
    if ($test -notmatch '^[A-Za-z0-9]+Test$') { throw "Invalid test name: $test" }
    & $compiler /nologo /utf8output /target:exe "/out:$output\$test.exe" `
        "/win32manifest:$root\app.manifest" "/reference:$output\PdfSplitter.exe" `
        /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Configuration.dll `
        "$PSScriptRoot\TestDiagnostics.cs" "$PSScriptRoot\$test.cs"
    if ($LASTEXITCODE -ne 0) { throw "Test build failed: $test ($LASTEXITCODE)" }
    # Runtime switches belong to the entry executable, not its referenced assembly.
    [xml] $config = Get-Content -LiteralPath "$root\PdfSplitter.exe.config" -Raw
    $settings = $config.CreateElement('appSettings')
    $scratch = $config.CreateElement('add')
    $scratch.SetAttribute('key', 'TestWorkDirectory')
    $scratch.SetAttribute('value', (Join-Path $PSScriptRoot 'qa-output'))
    $settings.AppendChild($scratch) | Out-Null
    $config.configuration.AppendChild($settings) | Out-Null
    $config.Save((Join-Path $output "$test.exe.config"))
    Write-Output "Built: $output\$test.exe"
}
