param(
    [string]$HostExe = "src\MyDM.NativeHost\bin\Debug\net8.0\MyDM.NativeHost.exe",
    [string]$TestUrl = "https://raw.githubusercontent.com/github/gitignore/main/README.md",
    [string]$FileName = "smoke-readme.txt",
    [int]$WaitSeconds = 3
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Python is required for this smoke script. Install Python or run an equivalent native-messaging test."
}

$hostPath = (Resolve-Path $HostExe).Path
$py = @"
import json
import struct
import subprocess
import sys
import time

host_exe = r'''$hostPath'''
test_url = r'''$TestUrl'''
file_name = r'''$FileName'''
wait_seconds = int(r'''$WaitSeconds''')

p = subprocess.Popen([host_exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)

def send(msg):
    data = json.dumps(msg, separators=(',', ':')).encode('utf-8')
    p.stdin.write(struct.pack('<I', len(data)))
    p.stdin.write(data)
    p.stdin.flush()

def read_msg():
    raw_len = p.stdout.read(4)
    if not raw_len:
        return None
    length = struct.unpack('<I', raw_len)[0]
    payload = p.stdout.read(length)
    return json.loads(payload.decode('utf-8'))

try:
    send({"type": "ping", "payload": {"requestId": "smoke-ping"}})
    print("Ping:", json.dumps(read_msg(), separators=(',', ':')))

    send({
        "type": "add_download",
        "payload": {
            "requestId": "smoke-add",
            "url": test_url,
            "filename": file_name,
            "referrer": "https://example.com/"
        }
    })
    add_resp = read_msg()
    print("Add:", json.dumps(add_resp, separators=(',', ':')))

    download_id = None
    if add_resp and add_resp.get("type") == "download_added":
        download_id = add_resp.get("payload", {}).get("downloadId")

    time.sleep(wait_seconds)

    send({
        "type": "get_status",
        "payload": {
            "requestId": "smoke-status",
            "downloadId": download_id
        }
    })
    print("Status:", json.dumps(read_msg(), separators=(',', ':')))
finally:
    try:
        p.kill()
    except Exception:
        pass
"@

$py | python -
