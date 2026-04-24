param(
    [string[]]$Filter,
    [switch]$Verbose
)

$verbosity = if ($Verbose) { "detailed" } else { "normal" }
$cmd = @("dotnet", "test", "tests/Playground/", "--logger", "console;verbosity=$verbosity")

if ($Filter) {
    $cmd += "--filter"
    $cmd += $Filter -join "|"
}

Write-Host ""
Write-Host "  Running Playground tests" -ForegroundColor Cyan
if ($Filter) {
    Write-Host "  Filter: $($Filter -join ' | ')" -ForegroundColor DarkCyan
}
Write-Host ""

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

& $cmd[0] $cmd[1..($cmd.Length - 1)] 2>&1 | ForEach-Object {
    $line = $_
    if ($line -match "Passed!|passed") {
        Write-Host $line -ForegroundColor Green
    } elseif ($line -match "Failed!|failed|Error") {
        Write-Host $line -ForegroundColor Red
    } elseif ($line -match "Skipped|skipped") {
        Write-Host $line -ForegroundColor Yellow
    } elseif ($line -match "^\s*(at |-->)") {
        Write-Host $line -ForegroundColor DarkRed
    } else {
        Write-Host $line
    }
}

$stopwatch.Stop()
$elapsed = $stopwatch.Elapsed.ToString("mm\:ss\.ff")

Write-Host ""
if ($LASTEXITCODE -eq 0) {
    Write-Host "  Done in $elapsed" -ForegroundColor Green
} else {
    Write-Host "  Failed in $elapsed (exit code $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}
