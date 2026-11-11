# -----------------------------------------------------------------------------
# release.ps1 - per-app GitHub releases for nvidia_app, keyed by compile date
#
#   Both apps (NvidiaMonitor GUI + NvmlTest CLI) print their own compile date
#   (LastWriteTime of the executing assembly). This script mirrors that into
#   GitHub Releases: one release per app per compilation, named
#       gui/<yyyy-MM-dd-HHmm>   and   cli/<yyyy-MM-dd-HHmm>
#   with a zip of that app's published bin\<App>\ output (exe + dlls + json,
#   NO .pdb) attached.
#
#   RULE: run this after every merge into main. Only apps whose code actually
#   changed in the merge get a new release; unchanged apps are skipped.
#
#   Usage:
#     release.ps1 --merge <base> <head>   post-merge mode: diff each app's
#                                         dependency closure between <base> and
#                                         <head>; release only changed apps.
#     release.ps1 --force [gui|cli]       force a release for both / one app,
#                                         regardless of change detection.
#
#   Idempotent: if tag gui/<date> or cli/<date> already exists, that app is
#   skipped (a re-compile changes the date, so it would be a new release).
#
#   Changelog: every release body carries a "## What's changed" section built
#   from the conventional commit messages that touched each app's dependency
#   closure over the given range. It is generated automatically in --merge
#   mode (range = mergeBase..mergeHead); with --force use:
#     release.ps1 --changelog <base> [<head>] [gui|cli]   force-release +
#                                                       changelog from range
#     release.ps1 --changelog gui                        changelog only, no
#                                                        new release (use to
#                                                        patch notes wording)
#   Docs commits (docs(ai): / ai/ files) are excluded from the changelog.
#
#   NOTE: aborts while NvidiaMonitor.exe is running - it locks its own DLLs.
#   Framework-dependent output: runners need the .NET 8 runtime (stated in
#   release notes).
# -----------------------------------------------------------------------------

$ErrorActionPreference = 'Stop'

# ── argument parsing ($ARGS is PowerShell's automatic script-arg array) ─
$mode = $null          # 'merge' | 'force' | 'changelog'
$mergeBase = $null
$mergeHead = $null
$forceApps = @('gui', 'cli')   # --force default = both
$clBase = $null
$clHead = $null                 # defaults to HEAD when omitted
$clApps = @('gui', 'cli')      # --changelog default = both

$a = $ARGS
for ($i = 0; $i -lt $a.Count; $i += 1) {
    $t = $a[$i]
    if ($t -eq '--merge') {
        if ($i + 2 -ge $a.Count) { throw 'usage: release.ps1 --merge <base> <head>' }
        $mode = 'merge'
        $mergeBase = $a[$i + 1]; $i += 1
        $mergeHead = $a[$i + 1]; $i += 1
    }
    elseif ($t -eq '--force') {
        $mode = 'force'
        # optional app selector: --force gui | --force cli
        if ($i + 1 -lt $a.Count -and ($a[$i+1] -in @('gui', 'cli'))) {
            $forceApps = @($a[$i + 1]); $i += 1
        }
    }
    elseif ($t -eq '--changelog') {
        $mode = 'changelog'
        # optional args: --changelog <base> [<head>] [gui|cli]
        $pos = @()
        while ($i + 1 -lt $a.Count) {
            $pos += $a[$i + 1]; $i += 1
        }
        foreach ($p in $pos) {
            if ($p -in @('gui', 'cli')) { $clApps = @($p) }
            elseif (-not $clBase) { $clBase = $p }
            elseif (-not $clHead) { $clHead = $p }
            else { throw "unknown --changelog argument: $p" }
        }
        if (-not $clBase) { throw 'usage: release.ps1 --changelog <base> [<head>] [gui|cli]' }
        if (-not $clHead) { $clHead = 'HEAD' }
    }
    else {
        throw "unknown argument: $t"
    }
}

if (-not $mode) {
    Write-Host 'usage: release.ps1 --merge <base> <head> | --force [gui|cli] | --changelog <base> [<head>] [gui|cli]' -ForegroundColor Yellow
    exit 2
}


# ── repo constants ────────────────────────────────────────────────
if ($PS0) { Set-Location (Split-Path $PS0) }

$apps = [ordered]@{
    gui = [ordered]@{
        name       = 'NvidiaMonitor'
        project    = 'src/NvidiaMonitor'
        closure    = @('src/NvidiaMonitor', 'src/NvmlCore', 'src/HectorBravo.Nvidia.Contracts')
        notes      = 'WPF dashboard. Framework-dependent: needs the .NET 8 Desktop runtime to run.'
    }
    cli = [ordered]@{
        name       = 'NvmlTest'
        project    = 'src/NvmlTest'
        closure    = @('src/NvmlTest', 'src/NvmlCore', 'src/HectorBravo.Nvidia.Contracts')
        notes      = 'Headless console CLI. Framework-dependent: needs the .NET 8 base runtime to run.'
    }
}

# ── changelog builder ─────────────────────────────────────────────
# Collects conventional commit messages whose touched files intersect an
# app's dependency closure over <base>..<head>; docs commits are skipped.
# Returns lines like "- feat(monitor): single-instance GUI guard (2c8dc2f)".
function Get-AppChangelog($clBaseRef, $clHeadRef, $closurePaths) {
    # %h marker keeps the short hash with each subject; file paths follow it
    $lines = git log --name-only --pretty=format:"%h@#@%s" "$clBaseRef..$clHeadRef" -- $closurePaths
    $out = @()
    $keep = $false
    foreach ($ln in @($lines)) {
        if ($ln -match '^([0-9a-f]+)@#@(.*)$') {
            $hash = $matches[1]; $subj = $matches[2]
            # skip pure-docs commits (task log / project index churn)
            $keep = !($subj -match '^docs\(ai\):')
            if ($keep) {
                # strip the [ai] AI-authorship tag from the displayed message
                $clean = $subj -replace ':\s*\[ai\]\s*', ': '
                $out += "- $clean ($hash)"
            }
        }
    }
    return $out
}


$releaseApps = @()
if ($mode -eq 'merge') {
    foreach ($stream in $apps.Keys) {
        $paths = @($apps[$stream].closure)
        $changed = git log --name-only --pretty=format: "$mergeBase..$mergeHead" -- $paths | Where-Object { $_ -match '\S' }
        if (@($changed).Count -gt 0) {
            $releaseApps += $stream
            Write-Host "[merge] $($apps[$stream].name) changed between $mergeBase and $mergeHead -> new release" -ForegroundColor Cyan
        } else {
            Write-Host "[merge] $($apps[$stream].name) unchanged -> skipped" -ForegroundColor Gray
        }
    }
} elseif ($mode -eq 'force') {
    $releaseApps = $forceApps
}

if ($mode -ne 'changelog' -and $releaseApps.Count -eq 0) {
    Write-Host 'Nothing to release.'
    exit 0
}

# ── changelog-only mode: patch the notes of existing releases ─────
if ($mode -eq 'changelog') {
    foreach ($stream in $clApps) {
        $app = $apps[$stream]
        $chg = @(Get-AppChangelog $clBase $clHead @($app.closure))
        if ($chg.Count -eq 0) {
            Write-Host "[changelog] $($app.name): no code commits in $clBase..$clHead" -ForegroundColor Gray
            continue
        }
        # find the newest release tag for this stream and patch its body
        $prevEa = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $tagJson = gh release list --limit 100 --json tagName 2>$null
        $ErrorActionPreference = $prevEa
        $tags = @($tagJson | ConvertFrom-Json | ForEach-Object { $_.tagName } | Where-Object { $_ -like "$stream/*" })
        if ($tags.Count -eq 0) { Write-Host "[changelog] no $($stream) release found" -ForegroundColor Red; continue }
        # sort by the date part of the tag so we patch the newest one
        $tag = $tags | Sort-Object | Select-Object -Last 1

        # keep any static footer already in the release body below the new changelog
        $prevEa2 = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $oldBody = gh release view $tag --json body 2>$null | ConvertFrom-Json | ForEach-Object { $_.body }
        $ErrorActionPreference = $prevEa2
        $footerText = ''
        if ($oldBody -match "(?s)^(.*?)## What's changed.*$") {
            $footerText = "`n`n" + $matches[1].Trim()
        } elseif ($oldBody) {
            $footerText = "`n`n" + $oldBody.Trim()
        }
        $bodyFile = Join-Path $env:TEMP "changelog-$stream.md"
        Set-Content -LiteralPath $bodyFile -Value ("## What's changed`n" + ($chg -join "`n") + $footerText) -Encoding utf8
        Write-Host "[changelog] patching notes of $tag" -ForegroundColor Cyan
        gh release edit $tag --notes-file $bodyFile
        if ($LASTEXITCODE -ne 0) { Write-Host "EDIT FAILED for $tag" -ForegroundColor Red; exit 1 }
        Remove-Item -LiteralPath $bodyFile -ErrorAction Ignore
        Write-Host "[ok] $tag notes updated" -ForegroundColor Green
    }
    exit 0
}

# ── guard: dashboard must not be running (locks its DLLs) ─────────
$running = tasklist /fi 'imagename eq NvidiaMonitor.exe' 2>$null | Select-String -Pattern 'NvidiaMonitor\.exe' -Quiet
if ($running) {
    Write-Host '[!] NvidiaMonitor.exe is still running - close the dashboard first, then re-run.' -ForegroundColor Red
    exit 1
}

# ── build + publish ───────────────────────────────────────────────
Write-Host '[1/3] Building nvidia_app.sln (Release)…'
dotnet build nvidia_app.sln -c Release --nologo
if ($LASTEXITCODE -ne 0) { Write-Host 'BUILD FAILED' -ForegroundColor Red; exit 1 }

foreach ($stream in $releaseApps) {
    $app = $apps[$stream]
    $appDir = Join-Path 'bin' $app.name
    Write-Host "[2/3] Publishing $($app.name) -> $appDir"
    # clean previous output so LastWriteTime reflects THIS compilation
    if (Test-Path $appDir) { Remove-Item -LiteralPath $appDir -Recurse -Force -ErrorAction Ignore }
    dotnet publish $app.project -c Release -o $appDir --nologo
    if ($LASTEXITCODE -ne 0) { Write-Host "PUBLISH $($app.name) FAILED" -ForegroundColor Red; exit 1 }
}

# ── zip + release per app ─────────────────────────────────────────
foreach ($stream in $releaseApps) {
    $app = $apps[$stream]
    $exe = Join-Path (Join-Path 'bin' $app.name) ($app.name + '.exe')
    if (-not (Test-Path $exe)) { Write-Host "missing $exe - cannot stamp release" -ForegroundColor Red; exit 1 }

    # Release date == the compile date the binary itself prints
    $stamp = (Get-Item $exe).LastWriteTime
    $dateTag = $stamp.ToString('yyyy-MM-dd-HHmm')
    $dateDisp = $stamp.ToString('yyyy-MM-dd HH:mm')
    $tag = "$stream/$dateTag"

    # Idempotency: skip if this exact release already exists
    $prevEa = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    gh release view $tag 2>$null | Out-Null
    $viewRc = $LASTEXITCODE
    $ErrorActionPreference = $prevEa
    if ($viewRc -eq 0) {
        Write-Host "[skip] release $tag already exists" -ForegroundColor Gray
        continue
    }

    # Zip the app's published output - exe + dlls + json only (no .pdb)
    $zipName = "$($app.name)-$dateTag"
    $tmpZip = Join-Path $env:TEMP "$zipName.zip"
    if (Test-Path $tmpZip) { Remove-Item -LiteralPath $tmpZip }
    $files = Get-ChildItem (Join-Path 'bin' $app.name) | Where-Object { $_.Extension.TrimStart('.') -in @('exe', 'dll', 'json') }
    Compress-Archive -Path $files.FullName -DestinationPath $tmpZip

    Write-Host "[3/3] Creating GitHub release $tag (title $($app.name) - built $dateDisp)"

    # Release notes = changelog for this app's closure + static footer.
    # In --merge mode the range is the merge; in --force mode use the most
    # recent commits if any were provided via --changelog-style args, else
    # fall back to a sensible look-back window.
    $clRangeBase = $null; $clRangeHead = 'HEAD'
    if ($mode -eq 'merge') { $clRangeBase = $mergeBase; $clRangeHead = $mergeHead }
    $chgLines = @()
    if ($clRangeBase) { $chgLines = @(Get-AppChangelog $clRangeBase $clRangeHead @($app.closure)) }

    $notesFile = Join-Path $env:TEMP "release-notes-$stream.md"
    $footer = "Compile date: $dateDisp (shown by the app itself). Contains $($app.name).exe + required DLLs/runtime manifests. $($app.notes)"
    if ($chgLines.Count -gt 0) {
        Set-Content -LiteralPath $notesFile -Value ("## What's changed`n" + ($chgLines -join "`n") + "`n`n" + $footer) -Encoding utf8
    } else {
        Write-Host "[warn] no changelog entries found for $($app.name) - releasing without one" -ForegroundColor Yellow
        Set-Content -LiteralPath $notesFile -Value $footer -Encoding utf8
    }

    gh release create $tag $tmpZip `
        --title "$($app.name) - built $dateDisp" `
        --notes-file $notesFile
    if ($LASTEXITCODE -ne 0) { Write-Host "RELEASE FAILED for $tag" -ForegroundColor Red; exit 1 }

    Remove-Item -LiteralPath $notesFile -ErrorAction Ignore

    Remove-Item -LiteralPath $tmpZip
    Write-Host "[ok] $tag published" -ForegroundColor Green
}

exit 0

