param([string]$OutputDirectory = 'artifacts/releases/1.0.0-rc.1-win-x64')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output directory to preserve existing candidates.' }
& (Join-Path $PSScriptRoot 'verify-cupri-host.ps1')
& (Join-Path $PSScriptRoot 'verify-cupri-engine.ps1')
Push-Location $repo
try {
    dotnet publish src/Shade/Shade.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:Trim=false -p:Aot=false -p:DebugType=None -p:DebugSymbols=false -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Release candidate publish failed.' }
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $output
    Copy-Item -LiteralPath (Join-Path $repo 'docs/release-1.0.0-rc.1.md') -Destination (Join-Path $output 'RELEASE-NOTES.md')
    $hash = Get-FileHash -LiteralPath (Join-Path $output 'Shade.exe') -Algorithm SHA256
    [IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), ($hash.Hash.ToLowerInvariant() + "  Shade.exe`n"))
    Write-Output ('Local candidate prepared: ' + $output)
    Write-Output 'See RELEASE-NOTES.md for required acceptance and notice review before distribution.'
}
finally { Pop-Location }
