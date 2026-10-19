$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) {
    throw '找不到 .NET Framework 的 csc.exe，无法编译 helper。'
}

$source = Join-Path $root 'helper\AutoHideCursorHelper.cs'
$output = Join-Path $root 'helper\AutoHideCursorHelper.exe'

& $csc /nologo /target:exe /optimize+ /platform:anycpu /out:"$output" "$source"
if ($LASTEXITCODE -ne 0) {
    throw "csc 编译失败，退出码 $LASTEXITCODE"
}
Write-Host "已生成: $output"
