$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $repo 'third-party/CupriFace.Engine'
$manifest = Get-Content -LiteralPath (Join-Path $vendor 'provenance.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$actual = @(Get-ChildItem -LiteralPath $vendor -Recurse -File | Where-Object {
    $_.FullName -notmatch '\\(bin|obj)\\' -and ($_.Extension -eq '.cs' -or $_.Name -eq 'SkiaNativeAssets.props')
})
if ($actual.Count -ne $manifest.files.Count) { throw 'Vendored engine source list changed; review provenance.' }
foreach ($file in $actual) {
    $relative = $file.FullName.Substring($vendor.Length + 1).Replace('\', '/')
    $record = @($manifest.files | Where-Object { $_.file -ceq $relative })
    if ($record.Count -ne 1 -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -cne $record[0].sha256) {
        throw ('Vendored engine source changed without a provenance update: ' + $relative)
    }
}
Write-Output ('Verified vendored engine provenance: ' + $actual.Count + ' source files.')
