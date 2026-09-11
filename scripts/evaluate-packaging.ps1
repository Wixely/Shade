param(
    [ValidateSet('win-x64', 'linux-x64')][string]$RuntimeIdentifier = 'win-x64',
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'Use a new evidence directory; previous evidence is never overwritten.' }
& (Join-Path $PSScriptRoot 'verify-cupri-host.ps1')
& (Join-Path $PSScriptRoot 'verify-cupri-engine.ps1')
New-Item -ItemType Directory -Path $evidence | Out-Null
$variants = @(
    @{ Name = 'baseline'; Single = 'true'; Trim = 'false'; Compression = 'false' },
    @{ Name = 'loose'; Single = 'false'; Trim = 'false'; Compression = 'false' },
    @{ Name = 'trimmed'; Single = 'true'; Trim = 'true'; Compression = 'false' },
    @{ Name = 'compressed'; Single = 'true'; Trim = 'false'; Compression = 'true' },
    @{ Name = 'trimmed-compressed'; Single = 'true'; Trim = 'true'; Compression = 'true' }
)
$results = @()
$utf8 = New-Object Text.UTF8Encoding($false)
foreach ($variant in $variants) {
    $directory = Join-Path $evidence $variant.Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    $output = Join-Path $directory 'publish'
    $arguments = @('publish', 'src/Shade/Shade.csproj', '-c', 'Release', '-r', $RuntimeIdentifier,
        '--self-contained', 'true', ('-p:PublishSingleFile=' + $variant.Single),
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false',
        ('-p:Trim=' + $variant.Trim), ('-p:EnableCompressionInSingleFile=' + $variant.Compression),
        '--nologo', '-o', $output)
    # Arguments contain fixed switches and filesystem paths (Windows paths cannot
    # contain quotes). Pass directly to dotnet, without a shell or command substitution.
    $quoted = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    Write-Output ('Publishing ' + $variant.Name)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath 'dotnet' -WorkingDirectory $repo -ArgumentList $quoted -WindowStyle Hidden -PassThru -Wait `
        -RedirectStandardOutput (Join-Path $directory 'build.stdout.log') -RedirectStandardError (Join-Path $directory 'build.stderr.log')
    if ($process.ExitCode -ne 0) { throw ('Publish failed for ' + $variant.Name + '; inspect its build logs.') }
    $files = @(Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object Length -Descending | ForEach-Object {
        [pscustomobject][ordered]@{ File = $_.FullName.Substring($output.Length + 1).Replace('\', '/'); Bytes = $_.Length;
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    if ($files.File -match '\.pdb$') { throw 'Unexpected debug symbols in evaluated package.' }
    $results += [ordered]@{ Variant = $variant.Name; RuntimeIdentifier = $RuntimeIdentifier; Arguments = $arguments;
        PublishSeconds = $timer.Elapsed.TotalSeconds; TotalBytes = ($files | Measure-Object Bytes -Sum).Sum;
        Files = $files; FunctionalAcceptance = 'Not run by this script; use the published application oracle.' }
    [IO.File]::WriteAllText((Join-Path $evidence 'manifest.json'), ($results | ConvertTo-Json -Depth 6), $utf8)
    Write-Output ('Published ' + $variant.Name + ': ' + $results[-1].TotalBytes + ' bytes')
}
Write-Output 'Build/size evidence only. No release defaults were changed; functional comparison remains required.'
