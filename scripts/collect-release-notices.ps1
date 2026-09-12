param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$assets = Get-Content (Join-Path $repo 'src/Shade/obj/project.assets.json') -Raw | ConvertFrom-Json
$packageRoot = @($assets.packageFolders.PSObject.Properties.Name)[0]
$destination = Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Force $destination | Out-Null
Copy-Item -Path (Join-Path $repo 'third-party/release-licenses/*.txt') -Destination $destination
Copy-Item -LiteralPath (Join-Path $repo 'third-party/CupriFace-LICENSE.txt') -Destination $destination
$inventory = @('Bundled dependency licenses and notices', '', 'CupriFace 0.20.0-shade.8: CupriFace-LICENSE.txt')
$fallbacks = @{ 'AngleSharp' = 'AngleSharp.txt'; 'Tmds.DBus.Protocol' = 'Tmds.DBus.txt'; 'Ultz.Native.GLFW' = 'GLFW.txt'; 'Ultz.Native.SDL' = 'SDL.txt' }
foreach ($entry in $assets.libraries.PSObject.Properties) {
    if ($entry.Value.type -ne 'package' -or $entry.Name -match 'NativeAssets\.(Linux|macOS)/|Microsoft.NET.ILLink.Tasks/') { continue }
    $id = $entry.Name.Split('/')[0]
    $path = Join-Path $packageRoot $entry.Value.path
    $notices = @(Get-ChildItem -LiteralPath $path -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES|COPYING)(\..*)?$' })
    $folder = $entry.Name.Replace('/', '-')
    if ($notices.Count) {
        $target = Join-Path $destination $folder
        New-Item -ItemType Directory -Force $target | Out-Null
        foreach ($file in $notices) { Copy-Item -LiteralPath $file.FullName -Destination $target }
    }
    $fallback = $fallbacks[$id]
    if ($id.StartsWith('Silk.NET.')) { $fallback = 'Silk.NET.txt' }
    if ($id.StartsWith('System.') -or $id.StartsWith('Microsoft.')) { $fallback = 'dotnet-runtime/LICENSE.TXT' }
    if (-not $notices.Count -and -not $fallback) { throw ('Missing license for ' + $entry.Name) }
    $inventory += $entry.Name + ': ' + $folder + $(if ($fallback) { '; ' + $fallback })
}
$runtime = @($assets.project.frameworks.'net10.0'.downloadDependencies | Where-Object { $_.name -eq 'Microsoft.NETCore.App.Runtime.win-x64' })
if ($runtime.Count -ne 1) { throw 'Cannot identify the exact bundled .NET runtime.' }
$runtimeVersion = $runtime[0].version.Trim('[', ']').Split(',')[0].Trim()
$runtimePath = Join-Path $packageRoot ('microsoft.netcore.app.runtime.win-x64/' + $runtimeVersion)
$target = Join-Path $destination 'dotnet-runtime'
New-Item -ItemType Directory -Force $target | Out-Null
foreach ($name in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
    Copy-Item -LiteralPath (Join-Path $runtimePath $name) -Destination $target
}
$inventory += '.NET runtime ' + $runtimeVersion + ': dotnet-runtime'
[IO.File]::WriteAllLines((Join-Path $destination 'INDEX.txt'), $inventory)
