# Starts Renamr.exe, waits for its main window and fails if the app crashes or never shows a window.
# Saves a screenshot of the screen to <ScreenshotPath> so the result can be checked by eye.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $ScreenshotPath,
    [int] $TimeoutSeconds = 60
)
$ErrorActionPreference = 'Stop'

$process = Start-Process -FilePath $Exe -PassThru
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $process.Refresh()
    if ($process.HasExited) { break }
    if ($process.MainWindowHandle -ne 0) { break }
}

if ($process.HasExited) {
    Write-Host "Renamr exited with code $($process.ExitCode)"
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-5) } -ErrorAction SilentlyContinue |
        Where-Object { $_.LevelDisplayName -eq 'Error' } | Select-Object -First 5 | Format-List TimeCreated, ProviderName, Message
    throw "Renamr crashed at startup: $Exe"
}
if ($process.MainWindowHandle -eq 0) {
    Stop-Process -Id $process.Id -Force
    throw "Renamr showed no window within $TimeoutSeconds seconds: $Exe"
}

Start-Sleep -Seconds 5  # let the first frame render
Write-Host "Window: '$($process.MainWindowTitle)', memory $([math]::Round($process.WorkingSet64 / 1MB)) MB"

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$bitmap.Save($ScreenshotPath, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose(); $bitmap.Dispose()

Stop-Process -Id $process.Id -Force
Write-Host "Renamr started correctly: $Exe"
