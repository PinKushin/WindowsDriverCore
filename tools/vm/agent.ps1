<#
.SYNOPSIS
    Runs inside the guest's INTERACTIVE desktop session and executes queued jobs.

.DESCRIPTION
    PowerShell Direct (Invoke-Command -VMName) lands in session 0, which has no
    desktop, so nothing driven from the host can automate a UI. This agent runs as
    the logged-in user in session 2 and is the bridge: the host writes a .ps1 into
    the queue directory, the agent runs it where a desktop exists, and writes back
    a log and a result.

    FOUR failure modes have actually happened here. The first three are why the
    launch line looks the way it does; the last two are why this file exists at
    all rather than the simpler version it replaced.

    1. An error escaping the loop ended it while -NoExit kept the window open, so
       the agent LOOKED alive and consumed nothing. Now the loop body is guarded
       AND the loop itself is wrapped, so an escape is logged and the loop
       restarts instead of ending.

    2. Tee-Object reading the child's stdout blocked forever when a grandchild
       inherited the pipe handle and outlived the job. A block, not an error, so
       no try/catch helps. No pipe is used: output is redirected to a FILE.

    3. Start-Process -RedirectStandardOutput was tried instead and COST 9 TESTS.
       Redirection forces UseShellExecute=false, which changes how the job's child
       applications sit relative to the desktop, and this suite drives real
       windows. So: call operator, output to a file. (The 9-test claim was later
       shown to be an alarm-store artifact rather than the launcher - see
       docs/LIMITATIONS.md - but the call operator is kept because it is the
       simplest thing that works and nothing argues for changing it.)

    4. "ALIVE" WAS NOT OBSERVABLE. Twice the process existed while the loop was
       dead, and the only symptom was jobs sitting unconsumed. There is now a
       HEARTBEAT file, so session 0 can tell a live agent from a hung one without
       guessing from a process list.

    5. AN INTERRUPTED JOB LOOKED LIKE A FINISHED ONE. Ctrl+C killed a running job,
       the finally block wrote the literal "done", and the empty log read as a
       completed recording. The result file now carries the child's EXIT CODE and
       a status word, so interrupted, failed and ok are distinguishable.

    Single instance is enforced with a system-wide mutex. Two agents polling one
    queue can grab the same job and run it twice concurrently - and since these
    jobs bind port 4723 and drive the same applications, that corrupts results
    rather than merely wasting time.

.PARAMETER QueuePath
    Directory watched for *.ps1 jobs.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -NoExit -File C:\baseline\agent.ps1
#>
[CmdletBinding()]
param(
    [string] $QueuePath = 'C:\baseline\cmd'
)

$ErrorActionPreference = 'Continue'

[System.IO.Directory]::CreateDirectory($QueuePath) | Out-Null

$heartbeatPath = Join-Path $QueuePath '_agent.heartbeat'
$sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId

# One agent per machine. A mutex rather than a lock file: it is released by the
# OS when the process dies, so a killed agent does not leave a stale lock the way
# an abandoned file would.
$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Global\WindowsDriverCoreGuestAgent', [ref] $createdNew)
if (-not $createdNew) {
    Write-Host 'ANOTHER AGENT IS ALREADY RUNNING. Refusing to start a second one.' -ForegroundColor Red
    Write-Host 'Two agents polling one queue can run the same job twice, concurrently,'
    Write-Host 'and these jobs bind port 4723 - so the result would be wrong, not just slow.'
    return
}

function Write-Heartbeat {
    param([string] $State, [string] $Job, [string] $StartedUtc = '')

    # Written every loop, so a reader in session 0 can tell a live agent from a
    # process whose loop has died. Best-effort: a heartbeat that throws must not
    # be what stops the agent.
    try {
        $beat = [ordered]@{
            utc       = (Get-Date).ToUniversalTime().ToString('o')
            pid       = $PID
            session   = $sessionId
            state     = $State
            job       = $Job

            # WHEN THE JOB STARTED, because the beat itself FREEZES while one
            # runs: the agent blocks in the call operator and cannot update it.
            # So "running" and "wedged" look identical from session 0 unless the
            # reader can see how long it has been running. Measured 2026-08-11:
            # an agent sat in "running" for 25 minutes with no child process, no
            # log output and the job still queued, and the only symptom was a
            # poller timing out with nothing to say.
            startedUtc = $StartedUtc
        } | ConvertTo-Json -Compress

        [System.IO.File]::WriteAllText($heartbeatPath, $beat)
    } catch {
        Write-Host ('heartbeat failed: ' + $_.Exception.Message)
    }
}

Write-Host ("Agent up in session $sessionId (pid $PID). Watching $QueuePath")
Write-Host 'Ctrl+C stops the agent, and also kills any job it is running.'

try {
    while ($true) {
        # The loop itself is wrapped as well as its body: mode 1 above was an
        # error escaping to here and ending the loop while the window stayed open.
        try {
            Write-Heartbeat -State 'idle' -Job ''

            $job = Get-ChildItem $QueuePath -Filter *.ps1 -ErrorAction SilentlyContinue |
                   Sort-Object CreationTime | Select-Object -First 1

            if ($null -eq $job) {
                Start-Sleep -Seconds 3
                continue
            }

            $name = [System.IO.Path]::GetFileNameWithoutExtension($job.Name)
            $log  = Join-Path $QueuePath ($name + '.log')
            $done = Join-Path $QueuePath ($name + '.done')
            $path = $job.FullName

            $startedUtc = (Get-Date).ToUniversalTime().ToString('o')
            Write-Heartbeat -State 'running' -Job $name -StartedUtc $startedUtc
            Write-Host ('[' + (Get-Date -Format HH:mm:ss) + '] running ' + $job.Name)

            $exitCode = -1
            try {
                # Call operator with output to a FILE: see modes 2 and 3 above.
                & powershell.exe -ExecutionPolicy Bypass -File $path *> $log
                $exitCode = $LASTEXITCODE
            }
            finally {
                # The job is removed whatever happened, so a job that kills the
                # agent cannot be picked up again on restart and kill it again.
                [System.IO.File]::Delete($path)

                # Mode 5: the RESULT, not the word "done". An interrupted job and
                # a successful one used to be indistinguishable from session 0.
                $status = if ($exitCode -eq 0) { 'ok' }
                          elseif ($exitCode -eq -1) { 'interrupted' }
                          else { 'failed' }

                $result = [ordered]@{
                    status   = $status
                    exitCode = $exitCode
                    utc      = (Get-Date).ToUniversalTime().ToString('o')
                    logBytes = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
                } | ConvertTo-Json -Compress

                [System.IO.File]::WriteAllText($done, $result)
                Write-Host ('[' + (Get-Date -Format HH:mm:ss) + '] ' + $status + ' (' + $exitCode + ') ' + $name)
            }
        }
        catch {
            Write-Host ('[' + (Get-Date -Format HH:mm:ss) + '] AGENT ERROR: ' + $_.Exception.Message)
            Write-Heartbeat -State 'error' -Job ''
            Start-Sleep -Seconds 3
        }
    }
}
finally {
    Write-Heartbeat -State 'stopped' -Job ''
    $mutex.ReleaseMutex()
    $mutex.Dispose()
    Write-Host 'Agent stopped.'
}
