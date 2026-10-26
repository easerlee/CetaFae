param(
    [Parameter(Mandatory = $true)][string[]]$Dumps
)

# 统计多次 UIA 快照的稳定性与可定位率。
# 键 = 控件类型|AutomationId|Name|ClassName#同类序号：无名字的 Image 靠遍历顺序区分，
# 否则十几个匿名图片会挤成一个键，误报成漂移。
$runs = @()
foreach ($file in $Dumps) {
    if (-not (Test-Path $file)) { continue }
    $rows = Get-Content $file | ForEach-Object { $_ | ConvertFrom-Json }
    $runs += , @{ File = (Split-Path $file -Leaf); Rows = $rows }
}
if ($runs.Count -eq 0) { Write-Host "no dumps"; return }

$keys = @{}
foreach ($run in $runs) {
    $ord = @{}
    foreach ($r in $run.Rows) {
        $base = "$($r.ControlType)|$($r.AutoId)|$($r.Name)|$($r.ClassName)"
        $ord[$base] = 1 + ($(if ($ord.ContainsKey($base)) { $ord[$base] } else { 0 }))
        $key = "$base#$($ord[$base])"
        if (-not $keys.ContainsKey($key)) {
            $keys[$key] = [pscustomobject]@{
                Key = $key; Rects = @(); Clickable = $r.Clickable
                Name = $r.Name; Type = $r.ControlType; AutoId = $r.AutoId
            }
        }
        $keys[$key].Rects += $r.Rect
    }
}

function Get-RectParts([string]$rect) {
    if ([string]::IsNullOrWhiteSpace($rect)) { return $null }
    $p = $rect.Split(',')
    if ($p.Count -ne 4) { return $null }
    return [pscustomobject]@{ L = [int]$p[0]; T = [int]$p[1]; W = [int]$p[2]; H = [int]$p[3] }
}

$stable = 0; $drifted = 0; $emptyRect = 0; $maxJitter = 0
$driftList = @()
foreach ($k in $keys.Values) {
    $parts = @($k.Rects | ForEach-Object { Get-RectParts $_ } | Where-Object { $_ })
    if ($parts.Count -eq 0) { $emptyRect++; continue }
    $jitter = 0
    foreach ($field in 'L', 'T', 'W', 'H') {
        $vals = $parts | ForEach-Object { $_.$field }
        $d = ($vals | Measure-Object -Maximum).Maximum - ($vals | Measure-Object -Minimum).Minimum
        if ($d -gt $jitter) { $jitter = $d }
    }
    if ($jitter -eq 0) { $stable++ } else { $drifted++; $driftList += $k }
    if ($jitter -gt $maxJitter) { $maxJitter = $jitter }
}

$clickableKeys = @($keys.Values | Where-Object { $_.Clickable })
$dupNames = $clickableKeys | Group-Object Name | Where-Object { $_.Count -gt 1 -and $_.Name -ne '' }

"runs=$($runs.Count) nodes=$((($runs | ForEach-Object { $_.Rows.Count }) -join '/'))"
"keys=$($keys.Count) stable=$stable drifted=$drifted emptyRect=$emptyRect maxJitterPx=$maxJitter"
"clickableKeys=$($clickableKeys.Count) duplicateNameAmongClickable=$(@($dupNames).Count)"
"unnamedClickable=$(@($clickableKeys | Where-Object { $_.Name -eq '' }).Count)"

if (@($dupNames).Count -gt 0) {
    "--- 同名可点控件 ---"
    $dupNames | Sort-Object Count -Descending | Select-Object -First 8 | ForEach-Object {
        $types = ($_.Group | ForEach-Object { ($_.Type -replace 'ControlType\.', '') }) -join ' + '
        "  '$($_.Name)' x$($_.Count): $types"
    }
}

if ($driftList.Count -gt 0) {
    "--- 位置漂移（前 5）---"
    $driftList | Select-Object -First 5 | ForEach-Object {
        $t = $_.Type -replace 'ControlType\.', ''
        $uniq = ($_.Rects | Select-Object -Unique) -join ' | '
        "  $t '$($_.Name)' : $uniq"
    }
}
