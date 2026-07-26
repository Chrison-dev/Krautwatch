#!/usr/bin/env pwsh
# Fallout build bootstrapper.
#   ./build.ps1 Compile   # build the solution
#   ./build.ps1 Test      # build + run unit tests (default)
[CmdletBinding()]
Param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $BuildArguments)
$ErrorActionPreference = 'Stop'
dotnet run --project "$PSScriptRoot/build/_build.csproj" -- @BuildArguments
exit $LASTEXITCODE
