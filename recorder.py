"""Backend helpers and Medal-style replay buffer for Ekran Kaydedici."""

from __future__ import annotations

import json
import os
import shutil
import signal
import subprocess
import tempfile
import threading
import time
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Callable, Optional


DEFAULT_OUTPUT_DIR = Path.home() / "Videos" / "EkranKayitlari"
RUNTIME_BASE = Path(os.environ.get("XDG_RUNTIME_DIR") or "/tmp")
BUFFER_DIR = RUNTIME_BASE / "ekran-kaydedici-buffer"
RENDER_DEVICE = "/dev/dri/renderD128"


def send_notification(title: str, body: str, urgency: str = "normal") -> None:
    """Send a desktop notification asynchronously via notify-send."""
    try:
        subprocess.Popen(
            [
                "notify-send",
                "-a",
                "Ekran Kaydedici",
                "-i",
                "ekran-kaydedici",
                "-u",
                urgency,
                title,
                body,
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            start_new_session=True,
        )
    except OSError:
        pass


def _nice_worker() -> None:
    """Lower subprocess CPU priority slightly so games/apps never stutter."""
    try:
        os.nice(5)
    except OSError:
        pass


@dataclass
class AudioMixSession:
    """Tracks temporary PipeWire/PulseAudio modules for mixed audio capture."""

    module_ids: list[str] = field(default_factory=list)
    source_name: Optional[str] = None

    def cleanup(self) -> None:
        """Unload any temporary loopback and null-sink modules in reverse order."""
        for mod_id in reversed(self.module_ids):
            subprocess.run(
                ["pactl", "unload-module", mod_id],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=False,
            )
        self.module_ids.clear()
        self.source_name = None


def get_focused_monitor() -> Optional[str]:
    """Return the focused Hyprland monitor name, or the first available monitor."""
    try:
        raw = subprocess.check_output(
            ["hyprctl", "monitors", "-j"],
            text=True,
            stderr=subprocess.DEVNULL,
            timeout=2,
        )
        monitors = json.loads(raw)
        for mon in monitors:
            if mon.get("focused"):
                return mon.get("name")
        if monitors:
            return monitors[0].get("name")
    except (subprocess.SubprocessError, json.JSONDecodeError, OSError):
        pass
    return None


def get_system_monitor_source() -> Optional[str]:
    """Return the default audio output's monitor source name."""
    try:
        sink = subprocess.check_output(
            ["pactl", "get-default-sink"],
            text=True,
            stderr=subprocess.DEVNULL,
            timeout=2,
        ).strip()
        if sink:
            return f"{sink}.monitor"
    except (subprocess.SubprocessError, OSError):
        pass
    return None


def get_microphone_source() -> Optional[str]:
    """Return a non-monitor microphone source name, preferring the default source."""
    try:
        default_src = subprocess.check_output(
            ["pactl", "get-default-source"],
            text=True,
            stderr=subprocess.DEVNULL,
            timeout=2,
        ).strip()
        if default_src and not default_src.endswith(".monitor"):
            return default_src

        raw = subprocess.check_output(
            ["pactl", "list", "short", "sources"],
            text=True,
            stderr=subprocess.DEVNULL,
            timeout=2,
        )
        for line in raw.splitlines():
            parts = line.split("\t")
            if len(parts) >= 2 and not parts[1].endswith(".monitor"):
                return parts[1]
    except (subprocess.SubprocessError, OSError):
        pass
    return None


def prepare_audio_source(
    include_system: bool, include_mic: bool
) -> tuple[Optional[str], AudioMixSession]:
    """Resolve the audio source for wf-recorder, creating a mix sink if needed."""
    session = AudioMixSession()
    sys_src = get_system_monitor_source() if include_system else None
    mic_src = get_microphone_source() if include_mic else None

    if sys_src and mic_src:
        sink_name = f"ekran_kaydedici_mix_{os.getpid()}"
        try:
            null_id = subprocess.check_output(
                [
                    "pactl",
                    "load-module",
                    "module-null-sink",
                    f"sink_name={sink_name}",
                    "sink_properties=device.description=EkranKaydediciMix",
                ],
                text=True,
                stderr=subprocess.DEVNULL,
                timeout=3,
            ).strip()
            session.module_ids.append(null_id)

            for src in (sys_src, mic_src):
                loop_id = subprocess.check_output(
                    [
                        "pactl",
                        "load-module",
                        "module-loopback",
                        f"source={src}",
                        f"sink={sink_name}",
                        "latency_msec=20",
                    ],
                    text=True,
                    stderr=subprocess.DEVNULL,
                    timeout=3,
                ).strip()
                session.module_ids.append(loop_id)

            session.source_name = f"{sink_name}.monitor"
            return session.source_name, session
        except (subprocess.SubprocessError, OSError):
            session.cleanup()
            return sys_src or mic_src, session

    return sys_src or mic_src, session


def get_video_encoder_args(fps: int = 30, gop: int = 30) -> list[str]:
    """Use hardware VAAPI (zero-copy DMA-BUF) when available, else fast libx264."""
    if os.path.exists(RENDER_DEVICE):
        return [
            "-c",
            "h264_vaapi",
            "-d",
            RENDER_DEVICE,
            "-F",
            "scale_vaapi=format=nv12",
            "-p",
            "qp=25",
            "-p",
            f"g={gop}",
            "-b",
            "0",
            "-r",
            str(fps),
        ]
    return [
        "--pixel-format",
        "yuv420p",
        "-c",
        "libx264",
        "-p",
        "preset=ultrafast",
        "-p",
        "crf=23",
        "-p",
        f"g={gop}",
        "-r",
        str(fps),
    ]


def _align_even_geometry(raw_geom: str) -> Optional[str]:
    """Ensure slurp geometry width/height are even numbers for H.264/VAAPI."""
    try:
        pos_part, size_part = raw_geom.strip().split()
        w_str, h_str = size_part.split("x")
        w = max(2, int(w_str) & ~1)
        h = max(2, int(h_str) & ~1)
        return f"{pos_part} {w}x{h}"
    except ValueError:
        return raw_geom.strip() or None


def select_region() -> Optional[str]:
    """Prompt the user to draw a screen region with slurp. Returns geometry or None."""
    try:
        res = subprocess.run(
            ["slurp"],
            capture_output=True,
            text=True,
            check=False,
        )
        if res.returncode == 0 and res.stdout.strip():
            return _align_even_geometry(res.stdout)
    except OSError:
        pass
    return None


def make_output_path(
    prefix: str = "ekran", output_dir: Path = DEFAULT_OUTPUT_DIR
) -> Path:
    """Create the output directory if needed and return a timestamped MP4 path."""
    output_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
    return output_dir / f"{prefix}_{stamp}.mp4"


def get_latest_recording(output_dir: Path = DEFAULT_OUTPUT_DIR) -> Optional[Path]:
    """Return the most recently modified MP4 file in output_dir, if any."""
    if not output_dir.exists():
        return None
    files = [
        p for p in output_dir.glob("*.mp4") if p.is_file() and p.stat().st_size > 0
    ]
    if not files:
        return None
    return max(files, key=lambda p: p.stat().st_mtime)


def _terminate_process(proc: Optional[subprocess.Popen[bytes]], sig: int = signal.SIGINT) -> None:
    """Stop a subprocess cleanly, escalating only if it refuses to exit."""
    if proc is None or proc.poll() is not None:
        return
    try:
        proc.send_signal(sig)
        proc.wait(timeout=6.0)
    except subprocess.TimeoutExpired:
        proc.terminate()
        try:
            proc.wait(timeout=2.5)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=2.0)
    except OSError:
        pass


def _parse_m3u8_segments(m3u8_path: Path, max_seconds: int) -> list[str]:
    """Parse HLS playlist and return the newest segment filenames covering max_seconds."""
    if not m3u8_path.exists():
        return []
    entries: list[tuple[float, str]] = []
    pending_dur = 1.0
    for line in m3u8_path.read_text(encoding="utf-8", errors="ignore").splitlines():
        stripped = line.strip()
        if not stripped:
            continue
        if stripped.startswith("#EXTINF:"):
            val = stripped[len("#EXTINF:") :].split(",", 1)[0]
            try:
                pending_dur = max(0.2, float(val))
            except ValueError:
                pending_dur = 1.0
        elif not stripped.startswith("#"):
            entries.append((pending_dur, stripped))
            pending_dur = 1.0

    if not entries:
        return []

    selected: list[str] = []
    total = 0.0
    for dur, seg in reversed(entries):
        selected.append(seg)
        total += dur
        if total >= max_seconds:
            break
    selected.reverse()
    return selected


class ScreenRecorder:
    """Manages a manual wf-recorder process and its temporary audio resources."""

    def __init__(self, output_dir: Path = DEFAULT_OUTPUT_DIR) -> None:
        self.output_dir = output_dir
        self.process: Optional[subprocess.Popen[bytes]] = None
        self.audio_session: Optional[AudioMixSession] = None
        self.current_file: Optional[Path] = None

    @property
    def is_recording(self) -> bool:
        return self.process is not None and self.process.poll() is None

    def start(
        self,
        *,
        geometry: Optional[str] = None,
        include_system_audio: bool = True,
        include_mic: bool = False,
        fps: int = 30,
    ) -> Path:
        """Start recording to a new MP4 file and return its path."""
        if self.is_recording:
            raise RuntimeError("Kayıt zaten devam ediyor.")

        output_path = make_output_path("ekran", self.output_dir)
        audio_source, audio_session = prepare_audio_source(
            include_system_audio, include_mic
        )

        cmd = ["wf-recorder", "-y", "-t", "-f", str(output_path)]
        cmd.extend(get_video_encoder_args(fps=fps, gop=fps))

        if geometry:
            cmd.extend(["--geometry", geometry])
        else:
            monitor = get_focused_monitor()
            if monitor:
                cmd.extend(["-o", monitor])

        if audio_source:
            cmd.append(f"--audio={audio_source}")

        try:
            self.process = subprocess.Popen(
                cmd,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
                preexec_fn=_nice_worker,
            )
            self.audio_session = audio_session
            self.current_file = output_path
            return output_path
        except Exception:
            audio_session.cleanup()
            raise

    def stop(self) -> Optional[Path]:
        """Gracefully stop wf-recorder with SIGINT so the MP4 container finalizes."""
        proc = self.process
        finished_path = self.current_file

        _terminate_process(proc, signal.SIGINT)

        self.process = None
        if self.audio_session is not None:
            self.audio_session.cleanup()
            self.audio_session = None
        self.current_file = None

        if finished_path and finished_path.exists() and finished_path.stat().st_size > 0:
            return finished_path
        return None


class ReplayBuffer:
    """Medal-style rolling RAM replay buffer backed by VAAPI wf-recorder + ffmpeg HLS."""

    def __init__(
        self,
        output_dir: Path = DEFAULT_OUTPUT_DIR,
        buffer_dir: Path = BUFFER_DIR,
        on_state_change: Optional[Callable[[bool, str], None]] = None,
    ) -> None:
        self.output_dir = output_dir
        self.buffer_dir = buffer_dir
        self.on_state_change = on_state_change

        self._lock = threading.RLock()
        self._clip_lock = threading.Lock()
        self._wf_proc: Optional[subprocess.Popen[bytes]] = None
        self._ff_proc: Optional[subprocess.Popen[bytes]] = None
        self._audio_session: Optional[AudioMixSession] = None

        self._want_running = False
        self._stop_event = threading.Event()
        self._supervisor_thread: Optional[threading.Thread] = None

        self.replay_seconds = 30
        self.include_system_audio = True
        self.include_mic = False
        self.fps = 30

    @property
    def is_running(self) -> bool:
        with self._lock:
            return (
                self._wf_proc is not None
                and self._wf_proc.poll() is None
                and self._ff_proc is not None
                and self._ff_proc.poll() is None
            )

    def _notify_state(self, active: bool, detail: str) -> None:
        if self.on_state_change is not None:
            self.on_state_change(active, detail)

    def _clean_buffer_dir(self) -> None:
        if self.buffer_dir.exists():
            shutil.rmtree(self.buffer_dir, ignore_errors=True)
        self.buffer_dir.mkdir(parents=True, exist_ok=True)

    def _spawn_pipeline(self) -> None:
        with self._lock:
            self._stop_processes_locked()
            self._clean_buffer_dir()

            audio_source, audio_session = prepare_audio_source(
                self.include_system_audio, self.include_mic
            )
            monitor = get_focused_monitor()

            wf_cmd = ["wf-recorder", "-y", "-m", "matroska", "-f", "/dev/stdout"]
            wf_cmd.extend(get_video_encoder_args(fps=self.fps, gop=self.fps))
            if monitor:
                wf_cmd.extend(["-o", monitor])
            if audio_source:
                wf_cmd.append(f"--audio={audio_source}")

            list_size = max(5, self.replay_seconds + 3)
            m3u8_path = self.buffer_dir / "stream.m3u8"
            ff_cmd = [
                "ffmpeg",
                "-hide_banner",
                "-loglevel",
                "error",
                "-i",
                "pipe:0",
                "-c",
                "copy",
                "-f",
                "hls",
                "-hls_time",
                "1",
                "-hls_list_size",
                str(list_size),
                "-hls_flags",
                "delete_segments+temp_file",
                str(m3u8_path),
            ]

            try:
                wf = subprocess.Popen(
                    wf_cmd,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.DEVNULL,
                    preexec_fn=_nice_worker,
                )
                assert wf.stdout is not None
                ff = subprocess.Popen(
                    ff_cmd,
                    stdin=wf.stdout,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    preexec_fn=_nice_worker,
                )
                wf.stdout.close()
            except Exception:
                audio_session.cleanup()
                raise

            self._wf_proc = wf
            self._ff_proc = ff
            self._audio_session = audio_session

    def _stop_processes_locked(self) -> None:
        _terminate_process(self._wf_proc, signal.SIGINT)
        _terminate_process(self._ff_proc, signal.SIGTERM)
        self._wf_proc = None
        self._ff_proc = None
        if self._audio_session is not None:
            self._audio_session.cleanup()
            self._audio_session = None

    def start(
        self,
        *,
        replay_seconds: int = 30,
        include_system_audio: bool = True,
        include_mic: bool = False,
        fps: int = 30,
    ) -> None:
        """Start or restart the rolling background replay buffer."""
        with self._lock:
            self.replay_seconds = replay_seconds
            self.include_system_audio = include_system_audio
            self.include_mic = include_mic
            self.fps = fps
            self._want_running = True
            self._stop_event.clear()
            self._spawn_pipeline()

            if self._supervisor_thread is None or not self._supervisor_thread.is_alive():
                self._supervisor_thread = threading.Thread(
                    target=self._supervisor_loop,
                    daemon=True,
                )
                self._supervisor_thread.start()

        self._notify_state(True, f"Anlık Tekrar aktif (Son {replay_seconds} sn · F8)")

    def stop(self) -> None:
        """Stop the replay buffer and clean up RAM chunks + audio modules."""
        with self._lock:
            self._want_running = False
            self._stop_event.set()
            self._stop_processes_locked()
            shutil.rmtree(self.buffer_dir, ignore_errors=True)
        self._notify_state(False, "Anlık Tekrar kapalı")

    def _supervisor_loop(self) -> None:
        """Automatically recover if audio device or monitor resets while running."""
        recent_failures: list[float] = []
        while not self._stop_event.wait(1.5):
            with self._lock:
                if not self._want_running:
                    break
                wf_alive = self._wf_proc is not None and self._wf_proc.poll() is None
                ff_alive = self._ff_proc is not None and self._ff_proc.poll() is None
                if wf_alive and ff_alive:
                    continue

                now = time.monotonic()
                recent_failures = [t for t in recent_failures if now - t < 20.0]
                recent_failures.append(now)
                if len(recent_failures) >= 5:
                    self._want_running = False
                    self._stop_processes_locked()
                    self._notify_state(False, "Arka plan tamponu durduruldu")
                    break

                try:
                    self._spawn_pipeline()
                except Exception:
                    pass

    def save_clip(self) -> Optional[Path]:
        """Snapshot the last replay_seconds into an MP4 clip without stopping capture."""
        if not self._clip_lock.acquire(blocking=False):
            return None

        try:
            if not self.is_running:
                return None

            # Wait one HLS segment boundary so the action right at F8 press is flushed.
            time.sleep(1.1)

            m3u8_path = self.buffer_dir / "stream.m3u8"
            segments = _parse_m3u8_segments(m3u8_path, self.replay_seconds)
            if not segments:
                return None

            with tempfile.TemporaryDirectory(
                dir=RUNTIME_BASE, prefix="ekran-snap-"
            ) as snap_raw:
                snap_dir = Path(snap_raw)
                copied: list[Path] = []
                for seg in segments:
                    src = self.buffer_dir / seg
                    if src.exists():
                        dst = snap_dir / seg
                        shutil.copy2(src, dst)
                        copied.append(dst)

                if not copied:
                    return None

                concat_file = snap_dir / "list.txt"
                concat_file.write_text(
                    "\n".join(f"file '{p}'" for p in copied) + "\n",
                    encoding="utf-8",
                )

                out_path = make_output_path("klip", self.output_dir)
                res = subprocess.run(
                    [
                        "ffmpeg",
                        "-y",
                        "-hide_banner",
                        "-loglevel",
                        "error",
                        "-f",
                        "concat",
                        "-safe",
                        "0",
                        "-i",
                        str(concat_file),
                        "-c",
                        "copy",
                        "-movflags",
                        "+faststart",
                        str(out_path),
                    ],
                    capture_output=True,
                    text=True,
                    check=False,
                )
                if res.returncode == 0 and out_path.exists() and out_path.stat().st_size > 0:
                    return out_path

                if out_path.exists():
                    out_path.unlink(missing_ok=True)
                return None
        finally:
            self._clip_lock.release()
