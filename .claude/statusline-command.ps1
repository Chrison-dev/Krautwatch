# Claude Code status line — MyFoodBag workspace
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$esc = [char]27
$bel = [char]7

$separator = "  │  "

$consoleColours = @{
    "red"     = "31"
    "green"   = "32"
    "yellow"  = "33"
    "blue"    = "34"
    "magenta" = "35"
    "cyan"    = "36"
    "white"   = "37"
}

$progressBarColours = @{
    80 = $consoleColours["red"]    # red
    60 = $consoleColours["yellow"] # amber
    0  = $consoleColours["green"]  # green
}

$progressBarFillings = @{
    "empty" = "░"
    "full"  = "█"
}

$emojis = @{
    "folder"    = "📁"
    "git"       = "🌿"
    "branch"    = "🌿"
    "staged"    = "🟢"
    "modified"  = "🟠"
    "untracked" = "⚪"
    "5h"        = "🕐"
    "7d"        = "📅"
}

$modelEmojis = @{
    "opus"    = "👑"
    "sonnet"  = "⚡"
    "haiku"   = "🌱"
    "default" = "🤖"
}

# ── data classes ───────────────────────────────────────────────────────────

class ClaudeModel {
    [string]$Id
    [string]$DisplayName
}

class ClaudeWorkspace {
    [string]$CurrentDir
    [string]$GitWorktree
}

class ClaudeRateLimit {
    [nullable[double]]$UsedPercentage
    [long]$ResetsAt
}

class ClaudeRateLimits {
    [ClaudeRateLimit]$FiveHour
    [ClaudeRateLimit]$SevenDay
}

class ClaudeContextWindow {
    [nullable[double]]$UsedPercentage
    [nullable[double]]$RemainingPercentage
}

class ClaudeData {
    [ClaudeModel]$Model
    [ClaudeWorkspace]$Workspace
    [ClaudeRateLimits]$RateLimits
    [ClaudeContextWindow]$ContextWindow
}

# ── helpers ────────────────────────────────────────────────────────────────

function Get-ClaudeDataFromJson {
    param([PSCustomObject]$json)

    $fiveHour = $null
    if ($null -ne $json.rate_limits -and $null -ne $json.rate_limits.five_hour) {
        $fiveHour = [ClaudeRateLimit]@{
            UsedPercentage = $json.rate_limits.five_hour.used_percentage
            ResetsAt       = $json.rate_limits.five_hour.resets_at
        }
    }
    $sevenDay = $null
    if ($null -ne $json.rate_limits -and $null -ne $json.rate_limits.seven_day) {
        $sevenDay = [ClaudeRateLimit]@{
            UsedPercentage = $json.rate_limits.seven_day.used_percentage
            ResetsAt       = $json.rate_limits.seven_day.resets_at
        }
    }

    return [ClaudeData]@{
        Model = [ClaudeModel]@{
            Id          = $json.model.id
            DisplayName = $json.model.display_name
        }
        Workspace = [ClaudeWorkspace]@{
            CurrentDir  = $json.workspace.current_dir
            GitWorktree = $json.workspace.git_worktree
        }
        RateLimits = [ClaudeRateLimits]@{
            FiveHour = $fiveHour
            SevenDay = $sevenDay
        }
        ContextWindow = [ClaudeContextWindow]@{
            UsedPercentage      = $json.context_window.used_percentage
            RemainingPercentage = $json.context_window.remaining_percentage
        }
    }
}

function Get-Bar {
    param([double]$pct, [int]$width = 10)
    $filled = [int][Math]::Round([Math]::Min([Math]::Max($pct, 0), 100) / 100 * $width)
    $empty  = $width - $filled
    return ($progressBarFillings["full"] * $filled) + ($progressBarFillings["empty"] * $empty)
}

function Get-PercentageColour {
    param([double]$pct)
    if ($pct -ge 80) { return $progressBarColours.80 }
    if ($pct -ge 60) { return $progressBarColours.60 }
    if ($pct -ge 0)  { return $progressBarColours.0 }
    return $consoleColours.white
}

function Get-Hyperlink {
    param([string]$url, [string]$text)
    return "${script:esc}]8;;${url}${script:bel}${text}${script:esc}]8;;${script:bel}"
}

function Get-ModelEmoji {
    param([string]$modelId)
    $id    = $modelId.ToLower()
    $match = $modelEmojis.Keys | Where-Object { $id -match $_ } | Select-Object -First 1
    return $match ? $modelEmojis[$match] : $modelEmojis.default
}

function Get-RateLimitSegment {
    param([string]$emoji, [string]$label, [double]$pct)
    $pct_int = [int][Math]::Round($pct)
    $bar     = Get-Bar $pct
    $col     = Get-PercentageColour $pct
    return "${emoji} ${script:esc}[${col}m${label} [${bar}] ${pct_int}%${script:esc}[0m"
}

function Get-ResetString {
    param([long]$unixSeconds)
    if (-not $unixSeconds) { return "" }
    $remaining = [DateTimeOffset]::FromUnixTimeSeconds($unixSeconds) - [DateTimeOffset]::UtcNow
    if ($remaining.TotalMinutes -le 1) { return "" }
    $h = [int][Math]::Floor($remaining.TotalHours)
    $m = $remaining.Minutes
    if ($h -gt 0) { return "Resets in ${h} hr ${m} min" }
    return "Resets in ${m} min"
}

# ── git wrapper (mock seam for Pester) ────────────────────────────────────

function Get-GitBranchInfo {
    param([string]$Dir)

    $branch = git -C $Dir branch --show-current 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $branch) { return $null }

    $remoteUrl         = git -C $Dir remote get-url origin 2>$null
    $resolvedRemoteUrl = if ($LASTEXITCODE -eq 0 -and $remoteUrl) { $remoteUrl } else { $null }

    $statusLines = git -C $Dir status --porcelain 2>$null
    if ($LASTEXITCODE -ne 0) { $statusLines = @() }

    return [PSCustomObject]@{
        Branch      = $branch
        RemoteUrl   = $resolvedRemoteUrl
        StatusLines = @($statusLines)
    }
}

# ── segments ───────────────────────────────────────────────────────────────
# Each function returns its content as a plain string, or "" to be omitted.
# Segments must NOT include separator characters — the assembler adds those.

function Get-ModelSegment {
    param([ClaudeData]$Data)
    return "$(Get-ModelEmoji $Data.Model.Id) ${script:esc}[1;36m$($Data.Model.DisplayName)${script:esc}[0m"
}

function Get-FiveHourSegment {
    param([ClaudeData]$Data)
    if ($null -eq $Data.RateLimits.FiveHour -or $null -eq $Data.RateLimits.FiveHour.UsedPercentage) { return "" }
    $parts = @(Get-RateLimitSegment $emojis."5h" "5h" $Data.RateLimits.FiveHour.UsedPercentage)
    $reset = Get-FiveHourResetSegment $Data
    if ($reset -ne "") { $parts += $reset }
    return $parts -join $separator
}

function Get-FiveHourResetSegment {
    param([ClaudeData]$Data)
    if ($null -eq $Data.RateLimits.FiveHour) { return "" }
    $reset = Get-ResetString $Data.RateLimits.FiveHour.ResetsAt
    if ($reset -eq "") { return "" }
    return "${script:esc}[90m${reset}${script:esc}[0m"
}

function Get-SevenDaySegment {
    param([ClaudeData]$Data)
    if ($null -eq $Data.RateLimits.SevenDay -or $null -eq $Data.RateLimits.SevenDay.UsedPercentage) { return "" }
    $parts = @(Get-RateLimitSegment $emojis."7d" "7d" $Data.RateLimits.SevenDay.UsedPercentage)
    $reset = Get-SevenDayResetSegment $Data
    if ($reset -ne "") { $parts += $reset }
    return $parts -join $separator
}

function Get-SevenDayResetSegment {
    param([ClaudeData]$Data)
    if ($null -eq $Data.RateLimits.SevenDay) { return "" }
    $reset = Get-ResetString $Data.RateLimits.SevenDay.ResetsAt
    if ($reset -eq "") { return "" }
    return "${script:esc}[90m${reset}${script:esc}[0m"
}

function Get-ContextSegment {
    param([ClaudeData]$Data)
    if ($null -eq $Data.ContextWindow.UsedPercentage) { return "" }
    $col     = Get-PercentageColour $Data.ContextWindow.UsedPercentage
    $ctx_int = [int][Math]::Round($Data.ContextWindow.UsedPercentage)
    return "${script:esc}[${col}mctx ${ctx_int}%${script:esc}[0m"
}

function Get-WorkspaceSegment {
    param([ClaudeData]$Data)
    if ($Data.Workspace.CurrentDir -eq "") { return "" }
    $fileUrl    = "file:///" + $Data.Workspace.CurrentDir.Replace("\", "/")
    $folderLink = "${script:esc}[0;32m$(Get-Hyperlink $fileUrl $Data.Workspace.CurrentDir)${script:esc}[0m"
    return "$($emojis['folder']) ${folderLink}"
}

function Get-GitSegment {
    param([ClaudeData]$Data)
    if ($Data.Workspace.CurrentDir -eq "") { return "" }

    $gitInfo = Get-GitBranchInfo $Data.Workspace.CurrentDir
    if ($null -eq $gitInfo) { return "" }

    $branchDisplay = $gitInfo.Branch
    if ($null -ne $gitInfo.RemoteUrl) {
        $remoteUrl = $gitInfo.RemoteUrl -replace '\.git$', ''
        $remoteUrl = $remoteUrl -replace '^git@github\.com:', 'https://github.com/'
        if ($remoteUrl -match 'github\.com') {
            $branchDisplay = Get-Hyperlink "${remoteUrl}/tree/$($gitInfo.Branch)" $gitInfo.Branch
        }
    }

    $staged = 0; $modified = 0; $untracked = 0
    foreach ($sl in $gitInfo.StatusLines) {
        if ($sl.Length -lt 2) { continue }
        $x = $sl[0]; $y = $sl[1]
        if ($x -ne ' ' -and $x -ne '?') { $staged++ }
        if ($y -eq 'M' -or $y -eq 'D')  { $modified++ }
        if ($x -eq '?' -and $y -eq '?') { $untracked++ }
    }

    $changes = ""
    if ($staged -gt 0)    { $changes += " ${script:esc}[32m+${staged}${script:esc}[0m" }
    if ($modified -gt 0)  { $changes += " ${script:esc}[33m~${modified}${script:esc}[0m" }
    if ($untracked -gt 0) { $changes += " ${script:esc}[90m?${untracked}${script:esc}[0m" }

    return "$($emojis['branch']) ${script:esc}[0;35m${branchDisplay}${script:esc}[0m${changes}"
}

# ── assembler ──────────────────────────────────────────────────────────────
# $layout is an array of lines; each line is an array of segment strings.
# Empty segments are dropped; empty lines are dropped.
# Segments within a line are joined with $separator.

function Get-StatusLine {
    param([ClaudeData]$Data)

    $layout = @(
        @(
            Get-ContextSegment $Data
            Get-FiveHourSegment $Data
            Get-SevenDaySegment $Data
        ),
        @(
            Get-ModelSegment $Data
            Get-WorkspaceSegment $Data
            Get-GitSegment $Data
        )
    )

    $renderedLines = foreach ($line in $layout) {
        $segments = $line | Where-Object { $_ -ne '' }
        if ($segments) { $segments -join $separator }
    }

    return ($renderedLines | Where-Object { $_ }) -join "`n"
}

# ── orchestrator ───────────────────────────────────────────────────────────

function Invoke-StatusLine {
    param([PSCustomObject]$InputJson)
    $claudeData = Get-ClaudeDataFromJson $InputJson
    [Console]::Write($(Get-StatusLine $claudeData))
}

# ── entry point (skipped when dot-sourced by Pester) ──────────────────────
# $input must be read here at script level — it is not available inside functions.

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StatusLine ($input | ConvertFrom-Json)
}
