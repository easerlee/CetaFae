param(
    [Parameter(Mandatory = $true)][string]$Compiler,
    [Parameter(Mandatory = $true)][string]$ChineseMessagesFile,
    [string]$Output = '.artifacts/installer'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outputPath = [IO.Path]::GetFullPath((Join-Path $root $Output))
if (-not $outputPath.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installer output must stay inside the workspace.'
}
$payload = Join-Path $outputPath 'payload'
$project = Join-Path $root 'src/CetaFae.Probe/CetaFae.Probe.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
New-Item -ItemType Directory -Force -Path $payload | Out-Null
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:RuntimeFrameworkVersion=8.0.32 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true --ignore-failed-sources -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
& $Compiler "/DAppVersion=$version" "/DPayloadDir=$payload" "/DOutputDir=$outputPath" "/DChineseMessagesFile=$ChineseMessagesFile" (Join-Path $PSScriptRoot 'CetaFae.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
