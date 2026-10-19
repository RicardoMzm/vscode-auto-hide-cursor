$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'helper\AutoHideCursorHelper.exe'
if (-not (Test-Path -LiteralPath $helper)) {
    throw "找不到 helper: $helper"
}
& $helper --restore
Write-Host '系统光标已恢复。'
