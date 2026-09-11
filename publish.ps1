$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'SyncthingNotifier.csproj'
$output = Join-Path $PSScriptRoot 'publish'

if (Test-Path $output)
{
    Remove-Item $output -Recurse -Force
}

dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --no-self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output $output

Write-Host "Published single-file executable: $output\SyncthingNotifier.exe"
