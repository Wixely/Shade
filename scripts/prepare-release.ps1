param([string]$OutputDirectory, [switch]$Aot)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content (Join-Path $repo 'src/Shade/Shade.csproj')
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
$suffix = if ($Aot) { '-win-x64-aot' } else { '-win-x64' }
if (-not $OutputDirectory) { $OutputDirectory = 'artifacts/releases/' + $version + $suffix }
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output directory to preserve existing candidates.' }
& (Join-Path $PSScriptRoot 'verify-cupri-host.ps1')
& (Join-Path $PSScriptRoot 'verify-cupri-engine.ps1')
Push-Location $repo
try {
    if ($Aot) {
        # NativeAOT compiles and trims in one step, so PublishSingleFile and PublishTrimmed play no
        # part; the project's own targets embed the unmanaged dependencies instead, which is what
        # makes this one file. TreatWarningsAsErrors is relaxed for this publish alone because
        # Silk.NET's native-asset resolver raises IL3000/IL3002 against APIs that return nothing in a
        # single-file app. The warnings stay in the log deliberately rather than being suppressed:
        # they are the standing reason to keep verifying that this build finds its libraries.
        dotnet publish src/Shade/Shade.csproj -c Release -r win-x64 --self-contained true `
            -p:Aot=true -p:TreatWarningsAsErrors=false `
            -p:DebugType=None -p:DebugSymbols=false -o $output
    }
    else {
        dotnet publish src/Shade/Shade.csproj -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:Trim=false -p:Aot=false -p:DebugType=None -p:DebugSymbols=false -o $output
    }
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
