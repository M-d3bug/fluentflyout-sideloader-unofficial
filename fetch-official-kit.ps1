# Downloads the official FluentFlyout GitHub-build installer kit (zip) from
# fluentflyout.com and extracts the .msixbundle + .cer into SystemFiles\.
# This is exactly what the manual download page hands you, automated.
#
# Run:  powershell -ExecutionPolicy Bypass -File .\fetch-official-kit.ps1

$ErrorActionPreference = 'Stop'

$root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$systemDir = Join-Path $root 'SystemFiles'
New-Item -ItemType Directory -Force -Path $systemDir | Out-Null

$url = 'https://fluentflyout.com/files/latest/x64'
$zip = Join-Path ([IO.Path]::GetTempPath()) 'FluentFlyout_official_kit.zip'

Write-Host "Downloading the official installer kit from $url ..."
try
{
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -UserAgent 'FluentFlyout-Sideloader/1.0' -UseBasicParsing -OutFile $zip -TimeoutSec 600
}
catch
{
    throw "Download failed: $($_.Exception.Message). You can also fetch it manually: open fluentflyout.com/donate and click 'No thanks, continue to download'."
}

Write-Host "Extracting SystemFiles bits ..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try
{
    $bundle = $archive.Entries | Where-Object { $_.FullName -like '*.msixbundle' } | Select-Object -First 1
    $cert   = $archive.Entries | Where-Object { $_.FullName -like '*.cer' }       | Select-Object -First 1
    if (-not $bundle) { throw 'The downloaded kit contains no .msixbundle' }
    if (-not $cert)   { throw 'The downloaded kit contains no .cer certificate' }

    # Replace old payloads so build.ps1 cannot accidentally bundle a stale version.
    Get-ChildItem -Path $systemDir -Filter '*.msixbundle' | Remove-Item -Force
    Get-ChildItem -Path $systemDir -Filter '*.cer'       | Remove-Item -Force

    foreach ($entry in @($bundle, $cert))
    {
        $dest = Join-Path $systemDir ([IO.Path]::GetFileName($entry.FullName))
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
        Write-Host ("  extracted: {0}  ({1:N0} bytes)" -f $dest, $entry.Length)
    }
}
finally
{
    $archive.Dispose()
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
}

Write-Host 'Done. Now run:  powershell -ExecutionPolicy Bypass -File .\SetupBuild\build.ps1'
