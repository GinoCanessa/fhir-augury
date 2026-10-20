#Requires -Version 5.1
<#
.SYNOPSIS
Lists Preparer runs and their ticket completion counts through the Orchestrator.
.DESCRIPTION
Shows non-terminal runs, including recoverable errors, by default. Ticket
completion is separate from run completion: grouping and snapshot publication
can still be in progress when every ticket is complete. All requests are read-only.
.EXAMPLE
.\tools\preparer-status.ps1
.EXAMPLE
.\tools\preparer-status.ps1 -Watch -IntervalSeconds 5
.EXAMPLE
.\tools\preparer-status.ps1 -IncludeCompleted -Orchestrator http://localhost:5150
#>
[CmdletBinding()]
param(
    [ValidateScript({
        $_.IsAbsoluteUri -and $_.Scheme -in @('http', 'https') -and
        [string]::IsNullOrEmpty($_.Query) -and
        [string]::IsNullOrEmpty($_.Fragment)
    })]
    [uri]$Orchestrator = 'http://localhost:5150',

    [switch]$Watch,

    [ValidateRange(1, 3600)]
    [int]$IntervalSeconds = 10,

    [switch]$IncludeCompleted
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$endpoint = "$($Orchestrator.AbsoluteUri.TrimEnd('/'))/api/v1/processing-services/Preparer/authoring/runs?limit=100"

do {
    $response = Invoke-RestMethod -Uri $endpoint -Method Get -TimeoutSec 30
    if ($null -eq $response -or
        $null -eq $response.PSObject.Properties['runs'] -or
        $response.runs -isnot [array] -or
        $null -eq $response.PSObject.Properties['truncated'] -or
        $response.truncated -isnot [bool]) {
        throw "Invalid Preparer run-list response from ${endpoint}: expected a runs array and a truncated flag."
    }

    $visibleRuns = @()
    $rows = @(
        foreach ($run in $response.runs) {
            if ($null -eq $run -or
                [string]::IsNullOrWhiteSpace($run.runId) -or
                [string]::IsNullOrWhiteSpace($run.status) -or
                $null -eq $run.state -or
                $run.state.isTerminal -isnot [bool]) {
                throw "Invalid Preparer run in response from ${endpoint}: missing run identity or terminal state."
            }

            foreach ($field in @('totalItems', 'completedItems', 'retryableErrorItems', 'supersededItems')) {
                if (($run.$field -isnot [int] -and $run.$field -isnot [long]) -or
                    $run.$field -lt 0) {
                    throw "Invalid ${field} for Preparer run '$($run.runId)'."
                }
            }
            if ($run.completedItems + $run.retryableErrorItems + $run.supersededItems -gt $run.totalItems) {
                throw "Item counts exceed totalItems for Preparer run '$($run.runId)'."
            }

            if (-not $IncludeCompleted -and $run.state.isTerminal) {
                continue
            }

            $visibleRuns += $run
            $percent = if ($run.totalItems -eq 0) {
                'n/a'
            } else {
                '{0:0.#}%' -f ([math]::Floor(1000.0 * $run.completedItems / $run.totalItems) / 10)
            }

            [pscustomobject]@{
                RunId = $run.runId
                Status = $run.status
                Tickets = "$($run.completedItems)/$($run.totalItems)"
                Complete = $percent
                Errors = $run.retryableErrorItems
                Superseded = $run.supersededItems
            }
        }
    )

    "Preparer runs - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    if ($rows.Count -eq 0) {
        if ($IncludeCompleted) {
            'No Preparer runs found.'
        } else {
            'No active Preparer runs.'
        }
    } else {
        $rows | Format-Table -AutoSize | Out-String -Width 200
        'Tickets/Complete count successfully completed items, not grouping or snapshot finalization.'
        foreach ($run in $visibleRuns) {
            $errorProperty = $run.PSObject.Properties['error']
            if ($null -ne $errorProperty -and -not [string]::IsNullOrWhiteSpace($errorProperty.Value)) {
                Write-Warning "Run '$($run.runId)': $($errorProperty.Value)"
            }
        }
    }

    if ($response.truncated) {
        Write-Warning 'Only the first 100 runs are shown; the server prioritizes non-terminal runs.'
    }

    if ($Watch) {
        Start-Sleep -Seconds $IntervalSeconds
    }
} while ($Watch)
