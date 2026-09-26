$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content (Join-Path $repo 'src/Shade/Shade.csproj')
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if ($version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$') { throw 'Invalid release version' }
$assets = Join-Path $repo 'artifacts/release-assets'
if (Test-Path -LiteralPath $assets) { throw 'Release assets already exist; preserve them and use a clean workspace.' }
New-Item -ItemType Directory $assets | Out-Null

# Both Windows downloads come from the same source and the same gates. The baseline is the long-proven
# untrimmed build; the NativeAOT one is smaller and starts sooner, and is offered beside it rather
# than in place of it so a machine it misbehaves on still has something that runs.
$checksums = New-Object System.Collections.Generic.List[string]
foreach ($variant in @(
        @{ Suffix = '-win-x64'; Aot = $false },
        @{ Suffix = '-win-x64-aot'; Aot = $true })) {
    $relative = 'artifacts/releases/' + $version + $variant.Suffix
    if ($variant.Aot) { & (Join-Path $PSScriptRoot 'prepare-release.ps1') -OutputDirectory $relative -Aot }
    else { & (Join-Path $PSScriptRoot 'prepare-release.ps1') -OutputDirectory $relative }
    $candidate = Join-Path $repo $relative
    $files = @(Get-ChildItem -LiteralPath $candidate -Recurse -File)
    foreach ($file in $files) {
        if ($file.Extension -notin @('.exe', '.txt', '.md', '') -or
            ($file.Extension -eq '.exe' -and $file.Name -ne 'Shade.exe')) { throw ('Unexpected release file: ' + $file.Name) }
    }
    $exe = Get-Item (Join-Path $candidate 'Shade.exe')
    if ($exe.VersionInfo.ProductName -ne 'Shade' -or $exe.VersionInfo.ProductVersion.Split('+')[0] -ne $version) {
        throw ('Executable version metadata mismatch in ' + $variant.Suffix)
    }
    # A NativeAOT publish that failed to embed its unmanaged dependencies would leave them beside the
    # executable, so one file is the check that the embedding actually happened.
    if ($variant.Aot -and $files.Where({ $_.Extension -eq '.exe' }).Count -ne 1) {
        throw 'The NativeAOT candidate has more than one executable; its dependencies were not embedded.'
    }
    $archive = Join-Path $assets ('Shade-' + $version + $variant.Suffix + '.zip')
    Compress-Archive -Path (Join-Path $candidate '*') -DestinationPath $archive -CompressionLevel Optimal
    $hash = Get-FileHash -LiteralPath $archive -Algorithm SHA256
    $checksums.Add($hash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($archive))
    Write-Output ('Packaged ' + [IO.Path]::GetFileName($archive))
}
[IO.File]::WriteAllText((Join-Path $assets 'SHA256SUMS.txt'), (($checksums -join "`n") + "`n"))
