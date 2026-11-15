param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\MidiBroad.App\MidiBroad.App.csproj"
$out = Join-Path $root "dist\MidiBroad"
$medium = Join-Path (Split-Path -Parent $root) "MidiBroad"
if ([IO.Path]::GetFullPath($medium) -eq [IO.Path]::GetFullPath($root)) {
    $medium = Join-Path $env:LOCALAPPDATA "MidiBroad\app"
}
$backup = Join-Path $root "dist\.midibroad-config-backup"

# Keep the user's portable configuration across a rebuild. The published
# folder is refreshed wholesale, but config.json/settings.json are user data.
if (Test-Path $backup) {
    Remove-Item $backup -Recurse -Force
}
New-Item -ItemType Directory -Force $backup | Out-Null
foreach ($name in @("config.json", "settings.json", "chords.json")) {
    $candidates = @(
        (Join-Path $out $name)
        (Join-Path $medium $name)
    ) | Where-Object { Test-Path $_ } | Sort-Object { (Get-Item $_).LastWriteTimeUtc } -Descending
    if ($candidates.Count -gt 0) {
        Copy-Item $candidates[0] (Join-Path $backup $name) -Force
    }
}

try {
    if (Test-Path $out) {
        Remove-Item $out -Recurse -Force
    }
    New-Item -ItemType Directory -Force $out | Out-Null

    dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $out

    Get-ChildItem $out -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force
}
finally {
    foreach ($name in @("config.json", "settings.json", "chords.json")) {
        $saved = Join-Path $backup $name
        if (Test-Path $saved) {
            New-Item -ItemType Directory -Force $out | Out-Null
            Copy-Item $saved (Join-Path $out $name) -Force
        }
    }
    Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue
}

# The workspace can carry an NTFS Low Mandatory Level label. A copy created in a
# normal-integrity sibling folder does not, so background keyboard capture works.
if (Test-Path $medium) {
    Remove-Item $medium -Recurse -Force
}
New-Item -ItemType Directory -Force $medium | Out-Null
Copy-Item (Join-Path $out "*") $medium -Recurse -Force
foreach ($name in @("config.json", "settings.json", "chords.json")) {
    $source = Join-Path $out $name
    if (Test-Path $source) {
        Copy-Item $source (Join-Path $medium $name) -Force
    }
}

$zip = Join-Path $root "dist\MidiBroad.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $medium "*") -DestinationPath $zip -CompressionLevel Optimal

Write-Host "Portable folder published to $out"
Write-Host "Normal-integrity runnable copy: $medium"
Write-Host "Shareable ZIP: $zip"
Get-ChildItem $out | Select-Object Name, Length
