<#
  Minimal stock Windows PowerShell bootstrap for this standalone repository.
  Installs Microsoft's pinned .NET 10 PowerShell host before CI application code.
  This file intentionally has no parent-repository, AWS or scheduled-task dependency.
#>
$ErrorActionPreference = 'Stop'
$ciHostPath = Join-Path $env:ProgramFiles 'PowerShell/7/pwsh.exe'
function Test-CiPowerShell {
    if (-not (Test-Path -LiteralPath $ciHostPath -PathType Leaf)) { return $false }
    try {
        $probe = '@{Version=$PSVersionTable.PSVersion.ToString();Edition=$PSVersionTable.PSEdition;RuntimeMajor=[Environment]::Version.Major} | ConvertTo-Json -Compress'
        $raw = & $ciHostPath -NoLogo -NoProfile -NonInteractive -Command $probe
        if ($LASTEXITCODE) { return $false }
        $info = ($raw -join "`n") | ConvertFrom-Json
        return $info.Edition -eq 'Core' -and [version]$info.Version -ge [version]'7.6.5' -and $info.RuntimeMajor -eq 10
    }
    catch { return $false }
}
if (-not (Test-CiPowerShell)) {
    # Fixed Microsoft release and official SHA-256, matching the parent host pin:
    # https://github.com/PowerShell/PowerShell/releases/tag/v7.6.5
    $uri = 'https://github.com/PowerShell/PowerShell/releases/download/v7.6.5/PowerShell-7.6.5-win-x64.msi'
    $expected = '3a87c24e044ec792047d734c841917ee4323a535e25f645ae6c33141a35fca8d'
    $msi = Join-Path $env:TEMP ('PowerShell-ci-' + [Guid]::NewGuid().ToString('N') + '.msi')
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $msi
        if ((Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash -ine $expected) {
            throw 'PowerShell installer digest differs from the pinned Microsoft release; no installer was executed.'
        }
        $arguments = @('/i', ('"' + $msi + '"'), '/qn', '/norestart',
            'ADD_PATH=1', 'ENABLE_PSREMOTING=0', 'REGISTER_MANIFEST=0', 'USE_MU=0', 'ENABLE_MU=0')
        $process = Start-Process -FilePath msiexec.exe -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -notin @(0, 3010)) { throw "PowerShell installation failed with MSI exit $($process.ExitCode)." }
    }
    finally {
        if (Test-Path -LiteralPath $msi) { Remove-Item -LiteralPath $msi -Force }
    }
}
if (-not (Test-CiPowerShell)) { throw 'CI requires stable PowerShell 7.6.5 or later running on .NET 10.' }
$ciHostPath
