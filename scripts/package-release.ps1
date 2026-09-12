$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content (Join-Path $repo 'src/Shade/Shade.csproj')
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if ($version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$') { throw 'Invalid release version' }
$relative = 'artifacts/releases/' + $version + '-win-x64'
& (Join-Path $PSScriptRoot 'prepare-release.ps1') -OutputDirectory $relative
$candidate = Join-Path $repo $relative
$assets = Join-Path $repo 'artifacts/release-assets'
if (Test-Path -LiteralPath $assets) { throw 'Release assets already exist; preserve them and use a clean workspace.' }
New-Item -ItemType Directory $assets | Out-Null
$files = @(Get-ChildItem -LiteralPath $candidate -Recurse -File)
foreach ($file in $files) {
    if ($file.Extension -notin @('.exe', '.txt', '.md', '') -or
        ($file.Extension -eq '.exe' -and $file.Name -ne 'Shade.exe')) { throw ('Unexpected release file: ' + $file.Name) }
}
$exe = Get-Item (Join-Path $candidate 'Shade.exe')
if ($exe.VersionInfo.ProductName -ne 'Shade' -or $exe.VersionInfo.ProductVersion.Split('+')[0] -ne $version) {
    throw 'Executable version metadata mismatch'
}
$archive = Join-Path $assets ('Shade-' + $version + '-win-x64.zip')
Compress-Archive -Path (Join-Path $candidate '*') -DestinationPath $archive -CompressionLevel Optimal
$hash = Get-FileHash -LiteralPath $archive -Algorithm SHA256
[IO.File]::WriteAllText((Join-Path $assets 'SHA256SUMS.txt'), ($hash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($archive) + "`n"))
Write-Output ('Packaged ' + [IO.Path]::GetFileName($archive))
