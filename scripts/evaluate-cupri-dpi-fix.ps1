param(
    [string]$RuntimeIdentifier = 'linux-x64',
    [string]$EvidenceDirectory,
    [string]$SourceArchive
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $EvidenceDirectory) { $EvidenceDirectory = Join-Path $repo ('artifacts/cupri-dpi-' + [Guid]::NewGuid().ToString('N')) }
$stage = [System.IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $stage) { throw 'Use a new evidence directory.' }
New-Item -ItemType Directory -Path $stage | Out-Null
$revision = '56a9850decbb3e07b1cbd5f409f3d988e406b94b'
$archive = Join-Path $stage 'source.zip'
if ($SourceArchive) { Copy-Item -LiteralPath $SourceArchive -Destination $archive }
else { Invoke-WebRequest -UseBasicParsing ('https://codeload.github.com/Wixely/CupriFace/zip/' + $revision) -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '20EF2397697E2FCAF61071B00D33895DF113D97DBCADCDD60F473EEE96BBED88') {
    throw 'Source archive changed; review it before changing the pin.'
}
Expand-Archive -LiteralPath $archive -DestinationPath $stage
$sourceRoot = Join-Path $stage ('CupriFace-' + $revision)
$patch = Join-Path $repo 'third-party/CupriFace-Shell-dpi.patch'
# Apply within a standalone temporary repository, so paths are relative to the extracted source.
git -C $sourceRoot init --quiet
if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the isolated patch workspace.' }
git -C $sourceRoot apply --check $patch
if ($LASTEXITCODE -ne 0) { throw 'Source does not match the reviewed patch.' }
git -C $sourceRoot apply $patch
if ($LASTEXITCODE -ne 0) { throw 'Patch failed.' }
git -C $sourceRoot apply (Join-Path $repo 'third-party/CupriFace-Shell-probe.patch')
if ($LASTEXITCODE -ne 0) { throw 'Probe compatibility patch failed.' }
git -C $sourceRoot apply (Join-Path $repo 'third-party/CupriFace-Shell-accessibility.patch')
if ($LASTEXITCODE -ne 0) { throw 'Accessibility activation patch failed.' }
git -C $sourceRoot apply (Join-Path $repo 'third-party/CupriFace-Shell-windows-password.patch')
if ($LASTEXITCODE -ne 0) { throw 'Windows password semantics patch failed.' }

# Build only the modified host against the unchanged, pinned engine package.
$project = Join-Path $sourceRoot 'src/CupriFace.Shell/CupriFace.Shell.csproj'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$projectText = [System.IO.File]::ReadAllText($project)
$original = '<ProjectReference Include="..\CupriFace\CupriFace.csproj" />'
if (-not $projectText.Contains($original)) { throw 'Unexpected host project reference.' }
[System.IO.File]::WriteAllText($project, $projectText.Replace($original, '<PackageReference Include="CupriFace" Version="[0.20.0]" />'), $utf8)
dotnet build $project -c Release -p:Version=0.20.0 --configfile (Join-Path $repo 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Patched host build failed.' }
$control = Join-Path $stage 'control'
$candidate = Join-Path $stage 'candidate'
dotnet publish (Join-Path $repo 'src/Shade/Shade.csproj') -c Release -r $RuntimeIdentifier --self-contained true -p:PublishSingleFile=false -o $control
if ($LASTEXITCODE -ne 0) { throw 'Control publish failed.' }
# Normal Shade builds now use the vendored host. Restore the original release host
# in this isolated control before making the patched comparison copy.
$originalPackage = Join-Path $repo 'packages-local/CupriFace.Shell.0.20.0.nupkg'
if ((Get-FileHash -LiteralPath $originalPackage -Algorithm SHA256).Hash -ne '7A3A1C47277658F6C5997869A537B22E2041C1E1785EC23DF77C7B8C193439EF') {
    throw 'Original host package does not match its reviewed pin. Run scripts/restore.ps1.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = [System.IO.Compression.ZipFile]::OpenRead($originalPackage)
try {
    $entry = $package.GetEntry('lib/net10.0/CupriFace.Shell.dll')
    if (-not $entry) { throw 'Original host assembly missing from package.' }
    $inputStream = $entry.Open()
    try {
        $outputStream = [System.IO.File]::Create((Join-Path $control 'CupriFace.Shell.dll'))
        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
    } finally { $inputStream.Dispose() }
} finally { $package.Dispose() }
Copy-Item -LiteralPath $control -Destination $candidate -Recurse
$hostAssembly = Join-Path $sourceRoot 'src/CupriFace.Shell/bin/Release/net10.0/CupriFace.Shell.dll'
Copy-Item -LiteralPath $hostAssembly -Destination (Join-Path $candidate 'CupriFace.Shell.dll')
Write-Output ('Control: ' + $control)
Write-Output ('Candidate: ' + $candidate)
Write-Output 'Experimental loose-output comparison only. Normal builds use the vendored patched host.'
