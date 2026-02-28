param(
    [string]$HostExe = "src\MyDM.NativeHost\bin\Debug\net8.0\MyDM.NativeHost.exe",
    [string[]]$Urls = @(
        "https://www.youtube.com/watch?v=jNQXAC9IVRw",
        "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
    ),
    [string]$Quality = "360p"
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Python is required for this smoke script. Install Python or run an equivalent native-messaging test."
}

$hostPath = (Resolve-Path $HostExe).Path
$urlJson = ($Urls | ConvertTo-Json -Compress)

$py = @"
import json
import struct
import subprocess
import sys

host_exe = r'''$hostPath'''
urls = json.loads(r'''$urlJson''')
quality = r'''$Quality'''

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
    send({"type": "ping", "payload": {"requestId": "yt-smoke-ping"}})
    print("Ping:", json.dumps(read_msg(), separators=(',', ':')))

    ok_count = 0
    for idx, url in enumerate(urls, start=1):
        request_id = f"yt-smoke-{idx}"
        send({
            "type": "add_youtube_download",
            "payload": {
                "requestId": request_id,
                "videoUrl": url,
                "filename": f"yt_smoke_{idx}.mp4",
                "title": f"yt_smoke_{idx}",
                "quality": quality,
                "referrer": "https://www.youtube.com/"
            }
        })
        response = read_msg()
        print(f"Download {idx}:", json.dumps(response, separators=(',', ':')))
        if response and response.get("type") == "download_added":
            ok_count += 1

    if ok_count != len(urls):
        raise SystemExit(f"YouTube smoke failed: {ok_count}/{len(urls)} successful downloads")
finally:
    try:
        p.kill()
    except Exception:
        pass
"@

$py | python -
