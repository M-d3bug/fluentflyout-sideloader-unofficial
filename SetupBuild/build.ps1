# Builds FluentFlyout-Sideloader_<version>_x64_Setup.exe with the C# compiler built into Windows.
# No external tooling required. Run:  powershell -ExecutionPolicy Bypass -File .\build.ps1

$ErrorActionPreference = 'Stop'
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$root       = Split-Path -Parent $MyInvocation.MyCommand.Path          # SetupBuild\
$parent     = Split-Path -Parent $root                                # installer folder
$systemDir  = Join-Path $parent 'SystemFiles'
$payloadDir = Join-Path $root 'payload'

$bundles = @(Get-ChildItem -Path $systemDir -Filter '*.msixbundle' | Sort-Object LastWriteTime -Descending)
$certs   = @(Get-ChildItem -Path $systemDir -Filter '*.cer'       | Sort-Object LastWriteTime -Descending)
if ($bundles.Count -eq 0) { throw "No .msixbundle found in $systemDir. Run fetch-official-kit.ps1 first." }
if ($certs.Count -eq 0)   { throw "No .cer found in $systemDir" }
$bundle = $bundles[0]
$cer    = $certs[0]

# Derive the version and publisher from the actual bundle manifest. This keeps the
# exe name, UI version and package filters correct after fetch-official-kit.ps1.
$bundleVersion   = '2.16.0'
$bundlePublisher = $null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($bundle.FullName)
try {
    $entry = $archive.Entries | Where-Object { $_.FullName -eq 'AppxMetadata/AppxBundleManifest.xml' } | Select-Object -First 1
    if ($entry) {
        $reader = New-Object IO.StreamReader($entry.Open())
        try { $manifest = $reader.ReadToEnd() } finally { $reader.Close() }
        $identity = [regex]::Match($manifest, '<Identity\b[^>]*>')
        if ($identity.Success) {
            $vm = [regex]::Match($identity.Value, '\bVersion="([^"]+)"')
            if ($vm.Success) {
                $parsed = $null
                if ([version]::TryParse($vm.Groups[1].Value, [ref]$parsed)) {
                    $bundleVersion = '{0}.{1}.{2}' -f $parsed.Major, $parsed.Minor, $parsed.Build
                } else {
                    $bundleVersion = $vm.Groups[1].Value
                }
            }
            $pm = [regex]::Match($identity.Value, '\bPublisher="([^"]+)"')
            if ($pm.Success) { $bundlePublisher = $pm.Groups[1].Value }
        }
    }
}
finally {
    $archive.Dispose()
}

# Pick the certificate that matches the bundle, not just the alphabetically first one.
if ($bundlePublisher) {
    $match = $null
    foreach ($candidate in $certs) {
        $candidateObject = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($candidate.FullName)
        try {
            if ($candidateObject.Subject -eq $bundlePublisher) { $match = $candidate; break }
        }
        finally {
            $candidateObject.Dispose()
        }
    }
    if (-not $match) {
        throw "No certificate in $systemDir matches the bundle publisher ($bundlePublisher). Run fetch-official-kit.ps1."
    }
    $cer = $match
}

$outExe = Join-Path $parent ("FluentFlyout-Sideloader_{0}_x64_Setup.exe" -f $bundleVersion)

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe (.NET Framework 4.x) not found' }

# --- Payload: clean names without spaces so csc args stay simple ---
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
$certPath   = Join-Path $payloadDir 'app.cer'
$bundlePath = Join-Path $payloadDir 'bundle.msixbundle'
$logoPath   = Join-Path $payloadDir 'logo.png'
$iconPath   = Join-Path $payloadDir 'app.ico'
Copy-Item $cer.FullName $certPath -Force
Copy-Item $bundle.FullName $bundlePath -Force

# --- App logo for the setup window badge and the exe icon ---
# Preferred source: payload\logo.png (the app's "unplated" icon artwork).
# If missing, take the best guess from the msix assets (needs a prior manual extraction
# into payload\assets, or re-run the one-off extraction).
if (-not (Test-Path $logoPath)) {
    $assetsDir = Join-Path $payloadDir 'assets'
    if (Test-Path $assetsDir) {
        $candidates = Get-ChildItem $assetsDir -Filter '*.png'
        $pick = $candidates | Where-Object { $_.Name -match 'altform-unplated_targetsize-256' } | Select-Object -First 1
        if (-not $pick) { $pick = $candidates | Where-Object { $_.Name -match 'targetsize-256' -and $_.Name -notmatch 'lightunplated' } | Select-Object -First 1 }
        if (-not $pick) { $pick = $candidates | Where-Object { $_.Name -match 'altform-unplated' } | Sort-Object Length -Descending | Select-Object -First 1 }
        if (-not $pick) { $pick = $candidates | Where-Object { $_.Name -eq 'Square150x150Logo.scale-400.png' } | Select-Object -First 1 }
        if ($pick) {
            Copy-Item $pick.FullName $logoPath -Force
            Write-Host "Logo heuristic picked: $($pick.Name)"
        }
    }
}
if (Test-Path $logoPath) {
    # Build a 256x256 PNG-in-ICO wrapper so the exe (and the Apps-list entry that
    # points at it) carries the real app icon.
    Add-Type -AssemblyName System.Drawing
    $src = [System.Drawing.Image]::FromFile($logoPath)
    $bmp = New-Object System.Drawing.Bitmap(256, 256)
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gain = [Math]::Min(256.0 / $src.Width, 256.0 / $src.Height)
    $srcW = $src.Width * $gain
    $srcH = $src.Height * $gain
    $gfx.DrawImage($src, [int]((256 - $srcW) / 2), [int]((256 - $srcH) / 2), [int]$srcW, [int]$srcH)
    $gfx.Dispose(); $src.Dispose()
    $pngMs = New-Object System.IO.MemoryStream
    $bmp.Save($pngMs, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngBytes = $pngMs.ToArray(); $pngMs.Dispose()
    $icoMs = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($icoMs)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]1)   # reserved, type=icon, count=1
    $bw.Write([byte]0);   $bw.Write([byte]0);  $bw.Write([byte]0);  $bw.Write([byte]0)  # 256px, palette, reserved
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)  # planes, bpp
    $bw.Write([UInt32]$pngBytes.Length); $bw.Write([UInt32]22)
    $bw.Write($pngBytes); $bw.Flush()
    [IO.File]::WriteAllBytes($iconPath, $icoMs.ToArray())
    $icoMs.Dispose(); $bw.Dispose()
    Write-Host "Built exe icon: $(Split-Path -Leaf $iconPath)"
}

# --- Compile ---
Write-Host "Compiling..."
$cscArgs = @(
    '-nologo',
    '-target:winexe',
    '-platform:anycpu',
    '-optimize+',
    "-out:$outExe",
    '-r:System.dll',
    '-r:System.Windows.Forms.dll',
    '-r:System.Drawing.dll',
    '-r:System.IO.Compression.dll',
    '-r:System.IO.Compression.FileSystem.dll',
    ('-resource:' + $certPath + ',FluentFlyoutSetup.cer'),
    ('-resource:' + $bundlePath + ',FluentFlyoutSetup.msixbundle'),
    (Join-Path $root 'Installer.cs')
)
if (Test-Path $logoPath) {
    # Insert the logo resource argument before the source file argument.
    $srcIdx = $cscArgs.Count - 1
    $cscArgs = @($cscArgs[0..($srcIdx - 1)] + ('-resource:' + $logoPath + ',FluentFlyoutSetup.logo.png') + $cscArgs[$srcIdx])
}
if (Test-Path (Join-Path $payloadDir 'app.ico')) {
    # Insert the icon argument just before the source file argument.
    $cscArgs = @($cscArgs[0..($cscArgs.Count - 2)] + ('-win32icon:' + (Join-Path $payloadDir 'app.ico')) + $cscArgs[$cscArgs.Count - 1])
}
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc.exe failed with exit code $LASTEXITCODE" }

$sw.Stop()
$exe = Get-Item $outExe
Write-Host ""
Write-Host ("DONE: {0}" -f $exe.FullName)
Write-Host ("Size: {0} MB   Time: {1:n0} s" -f [math]::Round($exe.Length/1MB,1), $sw.Elapsed.TotalSeconds)
