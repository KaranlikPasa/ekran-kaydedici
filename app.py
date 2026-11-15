#!/usr/bin/env python3
"""Ekran Kaydedici — Medal tarzı hafif Wayland/Hyprland ekran kaydedici."""

from __future__ import annotations

import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Optional

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, Gdk, Gio, GLib, Gtk

from config import REPLAY_DURATION_OPTIONS, AppConfig
from recorder import (
    DEFAULT_OUTPUT_DIR,
    ReplayBuffer,
    ScreenRecorder,
    get_latest_recording,
    select_region,
    send_notification,
)
from tray import TrayIcon

APP_ID = "tr.eymen.EkranKaydedici"
APP_TITLE = "Ekran Kaydedici"

CUSTOM_CSS = """
.rec-timer {
    font-size: 32px;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.5px;
}
.rec-dot-idle {
    color: alpha(@window_fg_color, 0.35);
    font-size: 16px;
}
.rec-dot-replay {
    color: #2ec27e;
    font-size: 16px;
}
.rec-dot-active {
    color: #e01b24;
    font-size: 16px;
}
.rec-primary-btn {
    min-height: 44px;
    font-size: 15px;
    font-weight: 600;
}
"""


def format_duration(seconds: int) -> str:
    """Format elapsed seconds as MM:SS or HH:MM:SS."""
    mins, secs = divmod(max(0, seconds), 60)
    hours, mins = divmod(mins, 60)
    if hours > 0:
        return f"{hours:02d}:{mins:02d}:{secs:02d}"
    return f"{mins:02d}:{secs:02d}"


def format_size(path: Path) -> str:
    """Format file size in human-readable MB/KB."""
    try:
        size = path.stat().st_size
    except OSError:
        return ""
    if size >= 1024 * 1024:
        return f"{size / (1024 * 1024):.1f} MB"
    return f"{max(1, size // 1024)} KB"


class EkranKaydediciWindow(Adw.ApplicationWindow):
    """Compact Medal-style single-window UI for Ekran Kaydedici."""

    def __init__(self, app: "EkranKaydediciApp") -> None:
        super().__init__(application=app, title=APP_TITLE)
        self.app = app
        self.set_default_size(390, -1)
        self.set_resizable(False)

        self.timer_id: Optional[int] = None
        self.is_selecting_region = False
        self._syncing_ui = False

        self._build_ui()
        self.sync_from_state()
        self.connect("close-request", self._on_close_request)

    def _build_ui(self) -> None:
        toolbar_view = Adw.ToolbarView()
        header = Adw.HeaderBar()
        header.set_title_widget(Adw.WindowTitle(title=APP_TITLE, subtitle=""))

        quit_btn = Gtk.Button(icon_name="application-exit-symbolic")
        quit_btn.set_tooltip_text("Uygulamadan tamamen çık")
        quit_btn.connect("clicked", lambda _b: self.app.quit_completely())
        header.pack_end(quit_btn)

        toolbar_view.add_top_bar(header)

        content = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=14)
        content.set_margin_top(14)
        content.set_margin_bottom(16)
        content.set_margin_start(16)
        content.set_margin_end(16)

        # Status Card
        status_card = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        status_card.add_css_class("card")

        status_inner = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        status_inner.set_margin_top(14)
        status_inner.set_margin_bottom(14)
        status_inner.set_margin_start(14)
        status_inner.set_margin_end(14)
        status_inner.set_halign(Gtk.Align.CENTER)

        headline_row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        headline_row.set_halign(Gtk.Align.CENTER)

        self.dot_label = Gtk.Label(label="●")
        self.dot_label.add_css_class("rec-dot-idle")
        headline_row.append(self.dot_label)

        self.headline_label = Gtk.Label(label="Hazır")
        self.headline_label.add_css_class("title-3")
        headline_row.append(self.headline_label)

        self.status_label = Gtk.Label(label="Anlık Tekrar arka planda çalışıyor")
        self.status_label.add_css_class("dim-label")

        status_inner.append(headline_row)
        status_inner.append(self.status_label)
        status_card.append(status_inner)
        content.append(status_card)

        # Medal Clip Action Button
        self.clip_btn = Gtk.Button(label="Klip Kaydet (F8)")
        self.clip_btn.add_css_class("suggested-action")
        self.clip_btn.add_css_class("pill")
        self.clip_btn.add_css_class("rec-primary-btn")
        self.clip_btn.connect("clicked", lambda _b: self.app.trigger_save_clip())
        content.append(self.clip_btn)

        # Medal & Audio Settings
        settings_group = Adw.PreferencesGroup()

        self.replay_row = Adw.SwitchRow(
            title="Anlık Tekrar (Medal Modu)",
            subtitle="Arka planda son saniyeleri hazır tutar",
        )
        self.replay_row.connect("notify::active", self._on_replay_toggled)
        settings_group.add(self.replay_row)

        duration_model = Gtk.StringList.new(
            [f"{sec} saniye" for sec in REPLAY_DURATION_OPTIONS]
        )
        self.duration_row = Adw.ComboRow(title="Klip Süresi", model=duration_model)
        self.duration_row.connect("notify::selected", self._on_duration_changed)
        settings_group.add(self.duration_row)

        self.system_audio_row = Adw.SwitchRow(title="Sistem Sesi")
        self.system_audio_row.connect("notify::active", self._on_audio_changed)
        settings_group.add(self.system_audio_row)

        self.mic_row = Adw.SwitchRow(title="Mikrofon")
        self.mic_row.connect("notify::active", self._on_audio_changed)
        settings_group.add(self.mic_row)

        self.settings_group = settings_group
        content.append(settings_group)

        # Manual Record Compact Row
        manual_group = Adw.PreferencesGroup(title="Manuel Kayıt")
        manual_row = Adw.ActionRow(title="Kayıt Alanı")

        mode_box = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=0)
        mode_box.add_css_class("linked")
        mode_box.set_valign(Gtk.Align.CENTER)

        self.btn_fullscreen = Gtk.ToggleButton(label="Tam Ekran")
        self.btn_region = Gtk.ToggleButton(label="Bölge")
        self.btn_region.set_group(self.btn_fullscreen)
        self.btn_fullscreen.set_active(True)

        mode_box.append(self.btn_fullscreen)
        mode_box.append(self.btn_region)
        manual_row.add_suffix(mode_box)
        manual_group.add(manual_row)

        self.manual_btn = Gtk.Button(label="Manuel Kaydı Başlat")
        self.manual_btn.add_css_class("pill")
        self.manual_btn.set_margin_top(8)
        self.manual_btn.connect("clicked", self._on_manual_clicked)

        manual_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        manual_box.append(manual_group)
        manual_box.append(self.manual_btn)
        content.append(manual_box)

        # Last Saved Clip / Recording
        self.last_group = Adw.PreferencesGroup()
        self.last_row = Adw.ActionRow(title="Son Kayıt", subtitle="")

        play_btn = Gtk.Button(icon_name="media-playback-start-symbolic")
        play_btn.set_valign(Gtk.Align.CENTER)
        play_btn.add_css_class("flat")
        play_btn.set_tooltip_text("Kaydı Oynat")
        play_btn.connect("clicked", self._on_play_last)

        folder_btn = Gtk.Button(icon_name="folder-open-symbolic")
        folder_btn.set_valign(Gtk.Align.CENTER)
        folder_btn.add_css_class("flat")
        folder_btn.set_tooltip_text("Kayıt Klasörünü Aç")
        folder_btn.connect("clicked", self._on_open_folder)

        self.last_row.add_suffix(play_btn)
        self.last_row.add_suffix(folder_btn)
        self.last_group.add(self.last_row)
        content.append(self.last_group)

        toolbar_view.set_content(content)
        self.set_content(toolbar_view)

    def sync_from_state(self) -> None:
        """Synchronize UI widgets with current app config and recorder status."""
        self._syncing_ui = True
        cfg = self.app.config
        self.replay_row.set_active(cfg.replay_enabled)
        try:
            idx = REPLAY_DURATION_OPTIONS.index(cfg.replay_seconds)
        except ValueError:
            idx = 1
        self.duration_row.set_selected(idx)
        self.system_audio_row.set_active(cfg.system_audio)
        self.mic_row.set_active(cfg.mic_audio)
        self._syncing_ui = False

        self.refresh_status()
        self.refresh_last_file()

    def refresh_status(self, message: Optional[str] = None) -> None:
        """Update top status card, buttons, and timer display."""
        is_manual = self.app.manual_recorder.is_recording
        replay_on = self.app.config.replay_enabled and self.app.replay_buffer.is_running

        self.dot_label.remove_css_class("rec-dot-idle")
        self.dot_label.remove_css_class("rec-dot-replay")
        self.dot_label.remove_css_class("rec-dot-active")
        self.headline_label.remove_css_class("rec-timer")

        if is_manual:
            self.dot_label.add_css_class("rec-dot-active")
            self.headline_label.add_css_class("rec-timer")
            elapsed = int(time.monotonic() - self.app.manual_start_ts)
            self.headline_label.set_text(format_duration(elapsed))
            self.status_label.set_text(message or "Manuel kayıt devam ediyor...")

            self.manual_btn.set_label("Kaydı Durdur")
            self.manual_btn.add_css_class("destructive-action")
            self.clip_btn.set_sensitive(False)
            self.settings_group.set_sensitive(False)
            self.btn_fullscreen.set_sensitive(False)
            self.btn_region.set_sensitive(False)
        else:
            self.manual_btn.set_label("Manuel Kaydı Başlat")
            self.manual_btn.remove_css_class("destructive-action")
            self.settings_group.set_sensitive(True)
            self.btn_fullscreen.set_sensitive(True)
            self.btn_region.set_sensitive(True)

            secs = self.app.config.replay_seconds
            self.clip_btn.set_label(f"Son {secs} Saniyeyi Kaydet (F8)")

            if replay_on:
                self.dot_label.add_css_class("rec-dot-replay")
                self.headline_label.set_text(f"Anlık Tekrar · F8")
                self.status_label.set_text(
                    message or f"Son {secs} sn RAM'de hazır (düşük tüketim)"
                )
                self.clip_btn.set_sensitive(not self.app.is_saving_clip)
            elif self.app.config.replay_enabled:
                self.dot_label.add_css_class("rec-dot-idle")
                self.headline_label.set_text("Başlatılıyor...")
                self.status_label.set_text(message or "Arka plan tamponu hazırlanıyor")
                self.clip_btn.set_sensitive(False)
            else:
                self.dot_label.add_css_class("rec-dot-idle")
                self.headline_label.set_text("Anlık Tekrar Kapalı")
                self.status_label.set_text(message or "Klip için Anlık Tekrar'ı açabilirsin")
                self.clip_btn.set_sensitive(False)

    def refresh_last_file(self) -> None:
        last = self.app.last_saved_file
        if last and last.exists():
            size_str = format_size(last)
            self.last_row.set_title(last.name)
            self.last_row.set_subtitle(
                f"{size_str} · ~/Videos/EkranKayitlari"
                if size_str
                else "~/Videos/EkranKayitlari"
            )
            self.last_group.set_visible(True)
        else:
            self.last_group.set_visible(False)

    def _on_replay_toggled(self, row: Adw.SwitchRow, _pspec: object) -> None:
        if self._syncing_ui:
            return
        self.app.update_config(replay_enabled=row.get_active())

    def _on_duration_changed(self, row: Adw.ComboRow, _pspec: object) -> None:
        if self._syncing_ui:
            return
        idx = row.get_selected()
        if 0 <= idx < len(REPLAY_DURATION_OPTIONS):
            self.app.update_config(replay_seconds=REPLAY_DURATION_OPTIONS[idx])

    def _on_audio_changed(self, _row: Adw.SwitchRow, _pspec: object) -> None:
        if self._syncing_ui:
            return
        self.app.update_config(
            system_audio=self.system_audio_row.get_active(),
            mic_audio=self.mic_row.get_active(),
        )

    def _on_manual_clicked(self, _btn: Gtk.Button) -> None:
        if self.is_selecting_region:
            return
        if self.app.manual_recorder.is_recording:
            self._stop_manual_recording()
        else:
            self._start_manual_recording()

    def _start_manual_recording(self) -> None:
        use_region = self.btn_region.get_active()
        if use_region:
            self.is_selecting_region = True
            self.manual_btn.set_sensitive(False)
            self.set_visible(False)

            def pick_worker() -> None:
                time.sleep(0.22)
                geom = select_region()
                GLib.idle_add(self._on_region_selected, geom)

            threading.Thread(target=pick_worker, daemon=True).start()
        else:
            self._begin_manual_capture(None)

    def _on_region_selected(self, geom: Optional[str]) -> bool:
        self.is_selecting_region = False
        self.manual_btn.set_sensitive(True)
        self.set_visible(True)
        self.present()

        if not geom:
            self.refresh_status("Bölge seçimi iptal edildi")
            return False

        self._begin_manual_capture(geom)
        return False

    def _begin_manual_capture(self, geometry: Optional[str]) -> None:
        self.manual_btn.set_sensitive(False)
        self.refresh_status("Manuel kayıt başlatılıyor...")

        def worker() -> None:
            err = self.app.start_manual_recording(geometry)
            GLib.idle_add(self._on_manual_started, err, geometry)

        threading.Thread(target=worker, daemon=True).start()

    def _on_manual_started(
        self, err: Optional[str], geometry: Optional[str]
    ) -> bool:
        self.manual_btn.set_sensitive(True)
        if err:
            self.refresh_status(f"Başlatılamadı: {err}")
            return False

        mode_text = "Bölge kaydediliyor..." if geometry else "Tam ekran kaydediliyor..."
        self.refresh_status(mode_text)
        if self.timer_id is not None:
            GLib.source_remove(self.timer_id)
        self.timer_id = GLib.timeout_add(500, self._on_manual_tick)
        return False

    def _on_manual_tick(self) -> bool:
        if not self.app.manual_recorder.is_recording:
            self.timer_id = None
            self.app.finish_manual_recording(unexpected=True)
            return False
        elapsed = int(time.monotonic() - self.app.manual_start_ts)
        self.headline_label.set_text(format_duration(elapsed))
        return True

    def _stop_manual_recording(self) -> None:
        self.manual_btn.set_sensitive(False)
        self.status_label.set_text("Kayıt kaydediliyor...")
        if self.timer_id is not None:
            GLib.source_remove(self.timer_id)
            self.timer_id = None

        def worker() -> None:
            self.app.finish_manual_recording(unexpected=False)

        threading.Thread(target=worker, daemon=True).start()

    def _on_play_last(self, _btn: Gtk.Button) -> None:
        last = self.app.last_saved_file
        if last and last.exists():
            subprocess.Popen(
                ["xdg-open", str(last)],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                start_new_session=True,
            )

    def _on_open_folder(self, _btn: Gtk.Button) -> None:
        DEFAULT_OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
        subprocess.Popen(
            ["xdg-open", str(DEFAULT_OUTPUT_DIR)],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            start_new_session=True,
        )

    def _on_close_request(self, _win: Gtk.Window) -> bool:
        # Keep running lightly in the background when Medal Replay or recording is active.
        if self.app.config.replay_enabled or self.app.manual_recorder.is_recording:
            self.set_visible(False)
            return True
        self.app.quit_completely()
        return False


class EkranKaydediciApp(Adw.Application):
    """Single-instance Medal-style application with background replay buffer & F8 action."""

    def __init__(self) -> None:
        super().__init__(
            application_id=APP_ID,
            flags=Gio.ApplicationFlags.HANDLES_COMMAND_LINE,
        )
        GLib.set_application_name(APP_TITLE)

        self.config = AppConfig.load()
        self.manual_recorder = ScreenRecorder(DEFAULT_OUTPUT_DIR)
        self.manual_start_ts: float = 0.0
        self.last_saved_file: Optional[Path] = get_latest_recording(DEFAULT_OUTPUT_DIR)
        self.is_saving_clip = False
        self._held = False
        self._shutting_down = False

        self.replay_buffer = ReplayBuffer(
            output_dir=DEFAULT_OUTPUT_DIR,
            on_state_change=self._on_replay_state_changed,
        )
        self.tray: Optional[TrayIcon] = None
        self.window: Optional[EkranKaydediciWindow] = None

    def do_startup(self) -> None:
        Adw.Application.do_startup(self)

        if not self._held:
            self.hold()
            self._held = True

        provider = Gtk.CssProvider()
        provider.load_from_data(CUSTOM_CSS.encode("utf-8"))
        display = Gdk.Display.get_default()
        if display is not None:
            Gtk.StyleContext.add_provider_for_display(
                display,
                provider,
                Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION,
            )

        self._register_actions()

        conn = self.get_dbus_connection()
        if conn is not None:
            self.tray = TrayIcon(
                conn,
                on_activate=self.toggle_window,
                on_secondary_activate=self.trigger_save_clip,
            )

        if self.config.replay_enabled:
            threading.Thread(target=self._apply_replay_state, daemon=True).start()

    def _register_actions(self) -> None:
        actions = [
            ("save-clip", lambda _a, _p: self.trigger_save_clip()),
            ("show-window", lambda _a, _p: self.show_window()),
            ("quit-app", lambda _a, _p: self.quit_completely()),
        ]
        for name, cb in actions:
            act = Gio.SimpleAction.new(name, None)
            act.connect("activate", cb)
            self.add_action(act)

    def do_command_line(self, command_line: Gio.ApplicationCommandLine) -> int:
        args = command_line.get_arguments()
        background = "--background" in args
        if not background:
            self.show_window()
        return 0

    def do_activate(self) -> None:
        self.show_window()

    def show_window(self) -> None:
        if self.window is None:
            self.window = EkranKaydediciWindow(self)
        self.window.sync_from_state()
        self.window.set_visible(True)
        self.window.present()

    def toggle_window(self) -> None:
        if self.window is not None and self.window.get_visible():
            if self.config.replay_enabled or self.manual_recorder.is_recording:
                self.window.set_visible(False)
                return
        self.show_window()

    def _on_replay_state_changed(self, active: bool, detail: str) -> None:
        def update_ui() -> bool:
            if self.tray is not None:
                self.tray.update(active, detail)
            if self.window is not None and not self.manual_recorder.is_recording:
                self.window.refresh_status(detail if active else None)
            return False

        GLib.idle_add(update_ui)

    def _apply_replay_state(self) -> None:
        if self._shutting_down or self.manual_recorder.is_recording:
            return
        if self.config.replay_enabled:
            try:
                self.replay_buffer.start(
                    replay_seconds=self.config.replay_seconds,
                    include_system_audio=self.config.system_audio,
                    include_mic=self.config.mic_audio,
                    fps=self.config.fps,
                )
            except Exception as exc:
                GLib.idle_add(self._set_status_msg, f"Tampon başlatılamadı: {exc}")
        else:
            self.replay_buffer.stop()

    def _set_status_msg(self, msg: str) -> bool:
        if self.window is not None:
            self.window.refresh_status(msg)
        return False

    def update_config(
        self,
        *,
        replay_enabled: Optional[bool] = None,
        replay_seconds: Optional[int] = None,
        system_audio: Optional[bool] = None,
        mic_audio: Optional[bool] = None,
    ) -> None:
        if replay_enabled is not None:
            self.config.replay_enabled = replay_enabled
        if replay_seconds is not None:
            self.config.replay_seconds = replay_seconds
        if system_audio is not None:
            self.config.system_audio = system_audio
        if mic_audio is not None:
            self.config.mic_audio = mic_audio
        self.config.save()

        if self.window is not None:
            self.window.refresh_status("Ayar uygulanıyor...")
        threading.Thread(target=self._apply_replay_state, daemon=True).start()

    def trigger_save_clip(self) -> None:
        if self.is_saving_clip:
            return
        if self.manual_recorder.is_recording:
            send_notification(
                "Ekran Kaydedici",
                "Manuel kayıt devam ederken klip alınamaz.",
            )
            return
        if not self.config.replay_enabled or not self.replay_buffer.is_running:
            send_notification(
                "Anlık Tekrar Kapalı",
                "F8 ile klip almak için Anlık Tekrar'ı açın.",
            )
            return

        self.is_saving_clip = True
        if self.window is not None:
            self.window.refresh_status("Klip kaydediliyor...")

        def worker() -> None:
            saved = self.replay_buffer.save_clip()
            GLib.idle_add(self._on_clip_saved, saved)

        threading.Thread(target=worker, daemon=True).start()

    def _on_clip_saved(self, saved: Optional[Path]) -> bool:
        self.is_saving_clip = False
        if saved is not None:
            self.last_saved_file = saved
            size_str = format_size(saved)
            send_notification(
                "Klip Kaydedildi!",
                f"{saved.name} ({size_str})",
            )
            if self.window is not None:
                self.window.refresh_last_file()
                self.window.refresh_status(f"Klip kaydedildi: {saved.name}")
        else:
            send_notification(
                "Klip Alınamadı",
                "Tampon henüz yeterli görüntü biriktirmedi, birazdan tekrar F8'e basın.",
                urgency="low",
            )
            if self.window is not None:
                self.window.refresh_status("Tampon hazırlanıyor, birazdan tekrar dene")
        return False

    def start_manual_recording(self, geometry: Optional[str]) -> Optional[str]:
        if self.replay_buffer.is_running:
            self.replay_buffer.stop()
        try:
            self.manual_recorder.start(
                geometry=geometry,
                include_system_audio=self.config.system_audio,
                include_mic=self.config.mic_audio,
                fps=self.config.fps,
            )
            self.manual_start_ts = time.monotonic()
            return None
        except Exception as exc:
            if self.config.replay_enabled:
                self._apply_replay_state()
            return str(exc)

    def finish_manual_recording(self, unexpected: bool) -> None:
        saved = self.manual_recorder.stop()
        if self.config.replay_enabled and not self._shutting_down:
            self._apply_replay_state()

        def update_ui() -> bool:
            if saved is not None:
                self.last_saved_file = saved
                send_notification("Kayıt Tamamlandı", saved.name)
            if self.window is not None:
                self.window.manual_btn.set_sensitive(True)
                self.window.refresh_last_file()
                msg = (
                    "Kayıt beklenmedik şekilde durdu"
                    if unexpected
                    else ("Kayıt tamamlandı" if saved else None)
                )
                self.window.refresh_status(msg)
            return False

        GLib.idle_add(update_ui)

    def quit_completely(self) -> None:
        self._shutting_down = True
        if self.manual_recorder.is_recording:
            self.manual_recorder.stop()
        self.replay_buffer.stop()
        if self.tray is not None:
            self.tray.destroy()
            self.tray = None
        if self._held:
            self._held = False
            self.release()
        self.quit()


def main() -> int:
    app = EkranKaydediciApp()
    return app.run(sys.argv)


if __name__ == "__main__":
    raise SystemExit(main())
