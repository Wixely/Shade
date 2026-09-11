$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$feed = Join-Path $repo 'packages-local'
New-Item -ItemType Directory -Force $feed | Out-Null
$pins = @{
    'CupriFace' = '5BA702B29D9C2A0A4A34DFC1AA63709B4C65C0D08A3F9B09BFFA39A4E4CA5D34'
    'CupriFace.Shell' = '7A3A1C47277658F6C5997869A537B22E2041C1E1785EC23DF77C7B8C193439EF'
}
foreach ($name in $pins.Keys) {
    $file = Join-Path $feed "$name.0.20.0.nupkg"
    if (-not (Test-Path -LiteralPath $file)) {
        $download = $file + '.' + [Guid]::NewGuid().ToString('N') + '.download'
        try {
            Invoke-WebRequest -UseBasicParsing "https://github.com/Wixely/CupriFace/releases/download/v0.20.0/$name.0.20.0.nupkg" -OutFile $download
            if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $pins[$name]) {
                throw "Package checksum mismatch for $name. Review the release before replacing the pin."
            }
            Move-Item -LiteralPath $download -Destination $file
        }
        finally {
            # Remove only this invocation's incomplete download, never an existing package.
            if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download }
        }
    }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $pins[$name]) {
        throw "Package checksum mismatch for $name. Review the release before replacing the pin."
    }
}
& (Join-Path $PSScriptRoot 'verify-cupri-host.ps1')
& (Join-Path $PSScriptRoot 'verify-cupri-engine.ps1')
dotnet restore (Join-Path $repo 'Shade.slnx') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
