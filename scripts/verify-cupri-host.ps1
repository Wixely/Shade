$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $repo 'third-party/CupriFace.Shell'
$manifest = Get-Content -LiteralPath (Join-Path $vendor 'provenance.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$actual = @(Get-ChildItem -LiteralPath $vendor -Filter '*.cs' -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
if ($actual.Count -ne $manifest.files.Count) { throw 'Vendored host source list changed; review and update provenance.' }
foreach ($file in $actual) {
    $relative = $file.FullName.Substring($vendor.Length + 1).Replace('\', '/')
    $record = @($manifest.files | Where-Object { $_.file -ceq $relative })
    if ($record.Count -ne 1 -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -cne $record[0].sha256) {
        throw ('Vendored host source changed without a provenance update: ' + $relative)
    }
}
Write-Output ('Verified vendored host provenance: ' + $actual.Count + ' source files.')
