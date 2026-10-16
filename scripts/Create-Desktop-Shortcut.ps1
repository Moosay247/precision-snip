$ErrorActionPreference = 'Stop'

$programPath = Join-Path $PSScriptRoot 'PrecisionSnip.exe'
if (-not (Test-Path -LiteralPath $programPath)) {
    throw "PrecisionSnip.exe was not found next to this script."
}

$desktopPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$shortcutPath = Join-Path $desktopPath 'Precision Snip.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $programPath
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.Description = 'Copy editable text from a screen region'
$shortcut.Hotkey = ''
$shortcut.WindowStyle = 1
$shortcut.Save()

$startupPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$startupShortcutPath = Join-Path $startupPath 'Precision Text.lnk'
if (Test-Path -LiteralPath $startupShortcutPath) {
    Remove-Item -LiteralPath $startupShortcutPath -Force
}

Write-Host "Created: $shortcutPath"
Write-Host 'The utility does not launch with Windows.'
Write-Host 'After opening it once, Ctrl+Alt+S stays ready for one hour.'


