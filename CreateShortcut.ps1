param([switch]$Desktop)
$ErrorActionPreference = 'Stop'
$taskExe = Join-Path $PSScriptRoot 'App\RGBroom.exe'
if (-not (Test-Path -LiteralPath $taskExe)) { throw 'Распакуйте весь архив RGBroom перед созданием ярлыка.' }
$taskFolder = if ($Desktop) { [Environment]::GetFolderPath('Desktop') } else { $PSScriptRoot }
$taskShortcutPath = Join-Path $taskFolder 'RGBroom.lnk'
$taskShell = New-Object -ComObject WScript.Shell
if (Test-Path -LiteralPath $taskShortcutPath) {
    $taskExisting = $taskShell.CreateShortcut($taskShortcutPath)
    if ($taskExisting.TargetPath -ne (Resolve-Path -LiteralPath $taskExe).Path) {
        $taskShortcutPath = Join-Path $taskFolder ('RGBroom ' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.lnk')
    }
}
$taskShortcut = $taskShell.CreateShortcut($taskShortcutPath)
$taskShortcut.TargetPath = (Resolve-Path -LiteralPath $taskExe).Path
$taskShortcut.WorkingDirectory = Split-Path $taskExe -Parent
$taskShortcut.IconLocation = $taskExe + ',0'
$taskShortcut.Description = 'RGBroom — RGB lighting, music and display sync'
$taskShortcut.Save()
Write-Output $taskShortcutPath
