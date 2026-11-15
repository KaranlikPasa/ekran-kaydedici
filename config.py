"""Configuration storage for Ekran Kaydedici."""

from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from pathlib import Path

CONFIG_DIR = Path.home() / ".config" / "ekran-kaydedici"
CONFIG_FILE = CONFIG_DIR / "config.json"

REPLAY_DURATION_OPTIONS = [15, 30, 60, 90]


@dataclass
class AppConfig:
    """User preferences persisted across restarts."""

    replay_enabled: bool = True
    replay_seconds: int = 30
    system_audio: bool = True
    mic_audio: bool = False
    fps: int = 30

    @classmethod
    def load(cls) -> "AppConfig":
        """Load config from disk, falling back to safe defaults."""
        if not CONFIG_FILE.exists():
            return cls()
        try:
            data = json.loads(CONFIG_FILE.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return cls()

        replay_secs = data.get("replay_seconds", 30)
        if replay_secs not in REPLAY_DURATION_OPTIONS:
            replay_secs = 30

        fps = data.get("fps", 30)
        if fps not in (30, 60):
            fps = 30

        return cls(
            replay_enabled=bool(data.get("replay_enabled", True)),
            replay_seconds=int(replay_secs),
            system_audio=bool(data.get("system_audio", True)),
            mic_audio=bool(data.get("mic_audio", False)),
            fps=int(fps),
        )

    def save(self) -> None:
        """Persist current config to disk."""
        CONFIG_DIR.mkdir(parents=True, exist_ok=True)
        CONFIG_FILE.write_text(
            json.dumps(asdict(self), indent=2, ensure_ascii=False) + "\n",
            encoding="utf-8",
        )
