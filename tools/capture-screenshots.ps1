# Starts RecompRadar once per page with a sample file and saves a screenshot of its window.
# Usage (from the repository root, after building the app):
#   powershell -File tools/capture-screenshots.ps1 [-Sample samples/synthetic_mips32_le_r5900_flag.elf] [-OutDir docs/screenshots]
# The app runs with its own temporary data folder (RECOMPRADAR_DATA_DIR), so no real settings or projects are touched.
param(
    [string]$Exe = 'src/RecompRadar.App/bin/Debug/net10.0-windows/RecompRadar.exe',
    [string]$Sample = 'samples/synthetic_mips32_le_r5900_flag.elf',
    [string]$OutDir = 'docs/screenshots',
    [string[]]$Pages = @('dashboard', 'analyzer', 'architecture', 'dependencies', 'report', 'projects', 'settings'),
    [int]$WaitSeconds = 4,
    # Samples are copied here and analyzed from here, so screenshots show a neutral path instead of the current user's profile folder.
    [string]$DemoRoot = 'C:\Users\Public\RecompRadar-demo',
    [string[]]$SeedSamples = @('samples/poc_mips32_be_sum.elf', 'samples/synthetic_x86_64_linux_dynamic.elf', 'samples/synthetic_mips32_le_psp_prx_like.elf', 'samples/synthetic_ppc64_be_ps3_like.elf')
)

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;

public static class NativeCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    public static Bitmap Capture(IntPtr hWnd)
    {
        RECT r;
        GetWindowRect(hWnd, out r);
        var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try { PrintWindow(hWnd, hdc, 2); } finally { g.ReleaseHdc(hdc); }
        }
        return bmp;
    }
}
"@

[void][NativeCapture]::SetProcessDPIAware()
$root = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $root $Exe
$demoSamples = Join-Path $DemoRoot 'samples'
New-Item -ItemType Directory -Force $demoSamples | Out-Null
Copy-Item (Join-Path $root 'samples\*') $demoSamples -Force
$samplePath = Join-Path $DemoRoot $Sample
$out = Join-Path $root $OutDir
New-Item -ItemType Directory -Force $out | Out-Null
$dataDir = Join-Path $DemoRoot 'data'
$env:RECOMPRADAR_DATA_DIR = $dataDir

# Analyze a few other samples first so the Projects page and the dashboard have something to show.
foreach ($seed in $SeedSamples) {
    $seedPath = Join-Path $DemoRoot $seed
    $p = Start-Process -FilePath $exePath -ArgumentList @("`"$seedPath`"") -PassThru
    Start-Sleep -Seconds 4
    if (-not $p.HasExited) { [void]$p.CloseMainWindow(); Start-Sleep -Milliseconds 700; if (-not $p.HasExited) { $p.Kill() } }
}

foreach ($page in $Pages) {
    $args = @("--page=$page")
    if ($page -ne 'dashboard' -and $page -ne 'settings') { $args += "`"$samplePath`"" }
    $proc = Start-Process -FilePath $exePath -ArgumentList $args -PassThru
    try {
        $deadline = (Get-Date).AddSeconds(20)
        while ($proc.MainWindowHandle -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline -and -not $proc.HasExited) {
            Start-Sleep -Milliseconds 200
            $proc.Refresh()
        }
        if ($proc.HasExited) { Write-Warning "RecompRadar exited early for page '$page' (exit code $($proc.ExitCode))"; continue }
        Start-Sleep -Seconds $WaitSeconds
        $proc.Refresh()
        $bmp = [NativeCapture]::Capture($proc.MainWindowHandle)
        $file = Join-Path $out "$page.png"
        $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Write-Output "saved $file"
    }
    finally {
        if (-not $proc.HasExited) { [void]$proc.CloseMainWindow(); Start-Sleep -Milliseconds 500; if (-not $proc.HasExited) { $proc.Kill() } }
    }
}
Remove-Item Env:\RECOMPRADAR_DATA_DIR
Remove-Item -Recurse -Force $DemoRoot -ErrorAction SilentlyContinue
Write-Output "temporary demo folder $DemoRoot was removed"
