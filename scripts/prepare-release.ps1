param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content (Join-Path $repo 'src/Shade/Shade.csproj')
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (-not $OutputDirectory) { $OutputDirectory = 'artifacts/releases/' + $version + '-win-x64' }
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
    Copy-Item -LiteralPath (Join-Path $repo ('docs/release-' + $version + '.md')) -Destination (Join-Path $output 'RELEASE-NOTES.md')
    & (Join-Path $PSScriptRoot 'collect-release-notices.ps1') -OutputDirectory $output
    $hash = Get-FileHash -LiteralPath (Join-Path $output 'Shade.exe') -Algorithm SHA256
    [IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), ($hash.Hash.ToLowerInvariant() + "  Shade.exe`n"))
    Write-Output ('Local candidate prepared: ' + $output)
    Write-Output 'See RELEASE-NOTES.md for known limitations.'
}
finally { Pop-Location }
