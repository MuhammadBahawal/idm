param(
    [string]$HostExe = "src\MyDM.NativeHost\bin\Debug\net8.0\MyDM.NativeHost.exe",
    [string]$TestUrl = "https://raw.githubusercontent.com/github/gitignore/main/README.md",
    [string]$FileName = "smoke-readme.txt",
    [int]$WaitSeconds = 3
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $HostExe)) {
    Write-Error "Native host executable not found: $HostExe"
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = (Resolve-Path $HostExe).Path
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
$null = $proc.Start()

function Send-Native([object]$obj) {
    $json = $obj | ConvertTo-Json -Compress -Depth 20
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $len = [System.BitConverter]::GetBytes([int]$bytes.Length)
    $proc.StandardInput.BaseStream.Write($len, 0, 4)
    $proc.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
    $proc.StandardInput.BaseStream.Flush()
}

function Read-Exactly([System.IO.Stream]$stream, [int]$count) {
    $buffer = New-Object byte[] $count
    $offset = 0
    while ($offset -lt $count) {
        $read = $stream.Read($buffer, $offset, $count - $offset)
        if ($read -le 0) {
            throw "Stream closed while reading $count bytes"
        }
        $offset += $read
    }
    return $buffer
}

function Read-Native() {
    $lenBytes = Read-Exactly $proc.StandardOutput.BaseStream 4
    $len = [System.BitConverter]::ToInt32($lenBytes, 0)
    $payload = Read-Exactly $proc.StandardOutput.BaseStream $len
    [System.Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json
}

try {
    Send-Native @{ type = "ping"; payload = @{ requestId = "smoke-ping" } }
    $ping = Read-Native
    Write-Host "Ping:" ($ping | ConvertTo-Json -Compress -Depth 20)

    Send-Native @{
        type = "add_download"
        payload = @{
            requestId = "smoke-add"
            url = $TestUrl
            filename = $FileName
            referrer = "https://example.com/"
        }
    }
    $added = Read-Native
    Write-Host "Add:" ($added | ConvertTo-Json -Compress -Depth 20)

    Start-Sleep -Seconds $WaitSeconds

    Send-Native @{ type = "get_status"; payload = @{ requestId = "smoke-status" } }
    $status = Read-Native
    Write-Host "Status:" ($status | ConvertTo-Json -Compress -Depth 20)
}
finally {
    if (-not $proc.HasExited) {
        $proc.Kill()
        $proc.WaitForExit()
    }
}
