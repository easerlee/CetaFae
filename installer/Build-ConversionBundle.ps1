param(
    [Parameter(Mandatory = $true)][string]$OfficialArchive
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$notices = Join-Path $root 'third-party/filee'
$release = Get-Content -LiteralPath (Join-Path $notices 'bundle.json') -Raw | ConvertFrom-Json
if ($release.id -notmatch '^image\.filee\.win\.x64\.(\d+\.\d+\.\d+)$') {
    throw 'Unsupported conversion bundle version.'
}
$version = $Matches[1]
$official = (Get-Item -LiteralPath $OfficialArchive).FullName
$utf8 = [System.Text.UTF8Encoding]::new($false, $true)
function Read-PinnedNotice([string]$Name, [string]$Hash) {
    $path = Join-Path $notices $Name
    # Git on Windows can check out CRLF. Emit the verified upstream LF bytes.
    $bytes = $utf8.GetBytes([IO.File]::ReadAllText($path, $utf8).Replace("`r`n", "`n"))
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -cne $Hash) {
        throw "Upstream notice does not match the pinned release: $Name"
    }
    return ,$bytes
}
$license = Read-PinnedNotice 'LICENSE' $release.licenseHash
$dependencyNotices = Read-PinnedNotice 'THIRD-PARTY-NOTICES.md' $release.noticesHash
$source = [IO.File]::Open($official, 'Open', 'Read', 'Read')
try {
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($source)) -cne $release.archiveHash) {
        throw 'Use the exact upstream Windows x64 portable archive for this release.'
    }
    $source.Position = 0
    $output = Join-Path $root '.artifacts/conversion-bundle'
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $destination = Join-Path $output "Filee-$version-win-x64.cetaplugin"
    $temporary = Join-Path $output ([Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $zip = [IO.Compression.ZipFile]::Open($temporary, 'Create')
        try {
            function Add-BundleEntry([string]$Name, [byte[]]$Bytes) {
                $entry = $zip.CreateEntry($Name).Open()
                try { $entry.Write($Bytes, 0, $Bytes.Length) } finally { $entry.Dispose() }
            }
            Add-BundleEntry 'bundle-id.txt' ($utf8.GetBytes($release.id))
            $entry = $zip.CreateEntry('filee-portable.zip', 'NoCompression').Open()
            try { $source.CopyTo($entry) } finally { $entry.Dispose() }
            Add-BundleEntry 'LICENSE' $license
            Add-BundleEntry 'THIRD-PARTY-NOTICES.md' $dependencyNotices
            Add-BundleEntry 'SOURCE.txt' ($utf8.GetBytes($release.source))
        } finally { $zip.Dispose() }
        [IO.File]::Move($temporary, $destination, $true)
        Get-Item -LiteralPath $destination
    } finally { [IO.File]::Delete($temporary) }
} finally { $source.Dispose() }
