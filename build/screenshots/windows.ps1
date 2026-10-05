# Takes the Windows screenshots of the README with the app in <AppDir>: start screen, movies preview and Rename files.
# Needs mock.py running as HTTPS proxy on 127.0.0.1:8899 with its ca.pem trusted (see .github/workflows/screenshots.yml).
param(
    [Parameter(Mandatory)] [string] $AppDir,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
New-Item -ItemType Directory -Force $OutDir | Out-Null

Add-Type -AssemblyName System.Drawing, System.Security
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);

    // The runner's screen is 1024x768, smaller than the app window: ask for 1920x1080.
    public static int SetResolution(int width, int height) {
        var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        EnumDisplaySettings(null, -1, ref dm);
        dm.dmPelsWidth = width; dm.dmPelsHeight = height; dm.dmFields = 0x80000 | 0x100000;
        return ChangeDisplaySettings(ref dm, 0);
    }
}
'@
Write-Host "Screen resolution change: $([Win]::SetResolution(1920, 1080)) (0 = done)"
Start-Sleep -Seconds 2

# Demo library: plain titles and years only, nothing else in the names.
$film = 'D:\Film'
$photos = 'D:\Foto\2023\Giugno\Milano'
New-Item -ItemType Directory -Force $film, $photos | Out-Null
foreach ($name in 'Backrooms.2026.mkv', 'black.bag.2025.mkv', 'Bugonia_2025.mkv', 'Death of a Unicorn 2025.mkv',
                  'Dracula.di.Bram.Stoker.1992.mkv', 'dragon trainer (2025).mkv') {
    Copy-Item "$here/sample.mkv" (Join-Path $film $name)
}
foreach ($stamp in '20230629_102842', '20230629_102904', '20230629_102916', '20230629_123859', '20230629_123910', '20230629_154722',
                   '20230630_104424', '20230630_104600', '20230630_111835', '20230630_112054', '20230630_131502', '20230630_131544',
                   '20230630_190210', '20230701_093015', '20230701_093047', '20230701_112930', '20230701_181205', '20230701_212240') {
    $ext = if ($stamp -eq '20230629_102904') { 'mp4' } else { 'jpg' }
    $file = Join-Path $photos "$stamp.$ext"
    Copy-Item "$here/sample.$ext" $file
    $date = [datetime]::ParseExact($stamp, 'yyyyMMdd_HHmmss', $null)
    (Get-Item $file).LastWriteTime = $date
    (Get-Item $file).CreationTime = $date
}

# Settings: English interface, a fake TMDb key (the mock proxy answers anyway) protected with DPAPI like the app does.
$data = Join-Path $env:LOCALAPPDATA 'Renamr'
New-Item -ItemType Directory -Force $data | Out-Null
'{ "Language": "en" }' | Set-Content (Join-Path $data 'interface.json')
$keys = [Text.Encoding]::UTF8.GetBytes('{"TmdbApiKey":"0123456789abcdef0123456789abcdef"}')
$entropy = [Text.Encoding]::UTF8.GetBytes('Renamr.ProviderKeys.v1')
$protected = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect($keys, $entropy, 'CurrentUser'))
$settings = Get-Content "$here/settings.json" -Raw | ConvertFrom-Json
$settings | Add-Member ProtectedKeys $protected
$settings | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $data 'settings.json')

# Dark theme, as on Daniele's PC.
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' AppsUseLightTheme 0 -Type DWord -Force

function Save-Window([string] $Name, [string[]] $Arguments, [int] $WaitSeconds) {
    $exe = Join-Path $AppDir 'Renamr.exe'
    $process = if ($Arguments) { Start-Process $exe -ArgumentList $Arguments -PassThru } else { Start-Process $exe -PassThru }
    $deadline = (Get-Date).AddSeconds(60)
    while ($process.MainWindowHandle -eq 0 -and -not $process.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
    }
    if ($process.MainWindowHandle -eq 0) { throw "No window for $Name" }
    $h = $process.MainWindowHandle
    # 1360x900 visible, like the Linux screenshots (plus the invisible 7-pixel resize borders of Windows).
    [Win]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 1374, 907, 0x0004) | Out-Null
    [Win]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Seconds $WaitSeconds

    # Whole window with PrintWindow (works even if it is bigger than the runner's screen), then cut the invisible borders.
    $r = New-Object Win+RECT; [Win]::GetWindowRect($h, [ref] $r) | Out-Null
    $f = New-Object Win+RECT; [Win]::DwmGetWindowAttribute($h, 9, [ref] $f, 16) | Out-Null
    $full = New-Object Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
    $g = [Drawing.Graphics]::FromImage($full)
    $hdc = $g.GetHdc(); [Win]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
    $crop = New-Object Drawing.Rectangle ($f.Left - $r.Left), ($f.Top - $r.Top), ($f.Right - $f.Left), ($f.Bottom - $f.Top)
    $shot = $full.Clone($crop, $full.PixelFormat)
    $shot.Save((Join-Path $OutDir "$Name.png"), [Drawing.Imaging.ImageFormat]::Png)
    $shot.Dispose(); $full.Dispose()
    Write-Host "$Name.png: $($crop.Width)x$($crop.Height), title '$($process.MainWindowTitle)'"
    Stop-Process -Id $process.Id -Force
    Start-Sleep -Seconds 2
}

Remove-Item (Join-Path $data 'batch-rename.json') -ErrorAction SilentlyContinue
Save-Window 'windows-start' @() 8
Save-Window 'windows-media-preview' @("`"$film`"") 20
Copy-Item "$here/batch-rename.json" (Join-Path $data 'batch-rename.json')
Save-Window 'windows-rename-files' @("`"$photos`"") 12
Write-Host "Screenshots saved in $OutDir"
