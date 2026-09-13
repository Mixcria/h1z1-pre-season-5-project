"""Record the reference game's own window with FFmpeg; no game injection or input hooks.

Pass an existing Start-ReferenceCapture run directory. Create STOP in that directory
to finalize video, or let the one-hour limit expire. All evidence stays local.
"""
import argparse
import datetime as dt
import json
import os
from pathlib import Path
import shutil
import subprocess
import time


def utc():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run_directory", type=Path)
    parser.add_argument("--client", default=r"C:\Games\ROTK\H1Z1.exe")
    parser.add_argument("--seconds", type=int, default=3600)
    args = parser.parse_args()
    run = args.run_directory.resolve()
    if not (run / "session.json").is_file():
        parser.error("run_directory must contain session.json")
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        parser.error("ffmpeg is not on PATH")
    hidden = subprocess.CREATE_NO_WINDOW
    state = {"schemaVersion": 1, "status": "armed-waiting-for-game", "armedUtc": utc(),
             "watcherPid": os.getpid(), "client": args.client, "maximumSeconds": args.seconds,
             "capture": "Windows Graphics Capture of the game window", "requestedFps": 60,
             "audioRecorded": False, "inputRecorded": False, "segments": [],
             "timingNote": "Invocation/progress UTC are approximate anchors, not exact input or frame presentation timestamps."}
    status_path = run / "video-session.json"
    if status_path.is_file():
        previous = json.loads(status_path.read_text(encoding="utf-8"))
        state["segments"] = previous.get("segments", [])
        state["previousArmedUtc"] = previous.get("armedUtc")

    def save():
        temporary = status_path.with_suffix(".tmp")
        temporary.write_text(json.dumps(state, indent=2), encoding="utf-8")
        temporary.replace(status_path)

    def event(kind, text):
        with (run / "events.jsonl").open("a", encoding="utf-8") as stream:
            stream.write(json.dumps({"utc": utc(), "kind": kind, "text": text}) + "\n")

    def game():
        # Process identity only: never collect launcher arguments or credentials.
        command = "Get-Process -Name H1Z1 -ErrorAction SilentlyContinue | Select-Object Id,Path,MainWindowHandle | ConvertTo-Json -Compress"
        result = subprocess.run(["powershell.exe", "-NoProfile", "-Command", command],
                                capture_output=True, text=True, creationflags=hidden, timeout=10)
        if not result.stdout.strip():
            return None
        records = json.loads(result.stdout)
        if isinstance(records, dict):
            records = [records]
        matches = [p for p in records if str(p.get("Path", "")).lower() == args.client.lower()
                   and p.get("MainWindowHandle", 0)]
        return matches[0] if len(matches) == 1 else None

    save()
    event("note", "Window video recorder armed: 60 fps requested; no audio or input recording.")
    deadline = time.monotonic() + args.seconds
    process = None
    try:
        while time.monotonic() < deadline and not (run / "STOP").exists():
            target = game()
            if not target:
                time.sleep(2)
                continue
            index = len(state["segments"]) + 1
            filename = f"gameplay-{index:03d}.mkv"
            progress = run / f"video-{index:03d}.progress.txt"
            log = run / f"video-{index:03d}.stderr.log"
            source = f"gfxcapture=hwnd={target['MainWindowHandle']}:max_framerate=60:width=-2:height=-2:resize_mode=scale_aspect:capture_cursor=1"
            command = [ffmpeg, "-hide_banner", "-loglevel", "info", "-stats_period", "1",
                       "-f", "lavfi", "-i", source, "-an", "-vf", "hwdownload,format=bgra", "-c:v", "h264_amf",
                       "-quality", "speed", "-rc", "cbr", "-b:v", "12M", "-g", "120",
                       "-fps_mode", "passthrough", "-t", str(max(1, int(deadline - time.monotonic()))),
                       "-fs", str(8 * 1024**3), "-progress", str(progress), "-nostats", str(run / filename)]
            segment = {"file": filename, "invokedUtc": utc(), "gamePid": target["Id"],
                       "windowHandle": target["MainWindowHandle"], "arguments": command,
                       "status": "starting", "progressFile": progress.name, "logFile": log.name}
            state["segments"].append(segment)
            state["status"] = "starting-video"
            save()
            with log.open("w", encoding="utf-8") as logfile:
                process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                                           stderr=logfile, creationflags=hidden)
                segment["ffmpegPid"] = process.pid
                save()
                event("note", f"Game-window video invoked: {filename}; FFmpeg PID {process.pid}.")
                while process.poll() is None:
                    if time.monotonic() >= deadline or (run / "STOP").exists():
                        break
                    active = game()
                    if not active or active["Id"] != target["Id"]:
                        segment["stopReason"] = "game-window-closed-or-changed"
                        break
                    if segment["status"] == "starting" and progress.is_file():
                        rows = progress.read_text(encoding="utf-8", errors="replace").splitlines()
                        if any(row.startswith("frame=") and int(row.split("=", 1)[1]) > 0 for row in rows):
                            segment["status"] = "recording"
                            segment["firstProgressObservedUtc"] = utc()
                            state["status"] = "recording"
                            save()
                    time.sleep(2)
                if process.poll() is None:
                    process.stdin.write(b"q\n")
                    process.stdin.flush()
                    try:
                        process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        process.terminate()
                        process.wait(timeout=10)
                        segment["forcedTermination"] = True
                segment["exitCode"] = process.returncode
                segment["endedUtc"] = utc()
                segment["status"] = "ended" if process.returncode == 0 else "failed"
                segment["bytes"] = (run / filename).stat().st_size if (run / filename).exists() else 0
                state["status"] = "armed-waiting-for-game"
                save()
                event("note", f"Video {filename} ended; exit {process.returncode}; {segment['bytes']} bytes.")
                process = None
            if segment["status"] == "failed":
                state["status"] = "video-failed-inspect-log"
                save()
                return 1
            time.sleep(2)
        state["status"] = "stopped" if (run / "STOP").exists() else "time-limit-reached"
        state["endedUtc"] = utc()
        save()
    except Exception as error:
        state["status"] = "video-error"
        state["error"] = str(error)
        save()
        raise
    finally:
        if process is not None and process.poll() is None:
            try:
                process.stdin.write(b"q\n")
                process.stdin.flush()
                process.wait(timeout=15)
            except (OSError, subprocess.TimeoutExpired):
                process.terminate()


if __name__ == "__main__":
    raise SystemExit(main())
