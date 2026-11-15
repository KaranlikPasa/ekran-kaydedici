"""Lightweight D-Bus StatusNotifierItem tray icon using Gio (zero extra deps)."""

from __future__ import annotations

from pathlib import Path
from typing import Callable, Optional

from gi.repository import Gio, GLib

SNI_XML = """
<node>
  <interface name="org.kde.StatusNotifierItem">
    <property name="Category" type="s" access="read"/>
    <property name="Id" type="s" access="read"/>
    <property name="Title" type="s" access="read"/>
    <property name="Status" type="s" access="read"/>
    <property name="IconName" type="s" access="read"/>
    <property name="IconThemePath" type="s" access="read"/>
    <property name="ItemIsMenu" type="b" access="read"/>
    <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
    <method name="Activate">
      <arg name="x" type="i" direction="in"/>
      <arg name="y" type="i" direction="in"/>
    </method>
    <method name="SecondaryActivate">
      <arg name="x" type="i" direction="in"/>
      <arg name="y" type="i" direction="in"/>
    </method>
    <method name="ContextMenu">
      <arg name="x" type="i" direction="in"/>
      <arg name="y" type="i" direction="in"/>
    </method>
    <method name="Scroll">
      <arg name="delta" type="i" direction="in"/>
      <arg name="orientation" type="s" direction="in"/>
    </method>
    <signal name="NewStatus">
      <arg name="status" type="s"/>
    </signal>
    <signal name="NewToolTip"/>
  </interface>
</node>
"""

SNI_PATH = "/StatusNotifierItem"
WATCHER_NAME = "org.kde.StatusNotifierWatcher"
WATCHER_PATH = "/StatusNotifierWatcher"


class TrayIcon:
    """Exposes a clickable StatusNotifierItem in the Wayland/Quickshell tray."""

    def __init__(
        self,
        connection: Gio.DBusConnection,
        on_activate: Callable[[], None],
        on_secondary_activate: Optional[Callable[[], None]] = None,
    ) -> None:
        self._conn = connection
        self._on_activate = on_activate
        self._on_secondary = on_secondary_activate
        self._status = "Active"
        self._tooltip_title = "Ekran Kaydedici"
        self._tooltip_desc = "Anlık Tekrar · F8"
        self._reg_id: int = 0
        self._watch_id: int = 0

        node_info = Gio.DBusNodeInfo.new_for_xml(SNI_XML)
        iface = node_info.lookup_interface("org.kde.StatusNotifierItem")

        self._reg_id = self._conn.register_object(
            SNI_PATH,
            iface,
            self._handle_method_call,
            self._handle_get_property,
            None,
        )

        self._watch_id = Gio.bus_watch_name_on_connection(
            self._conn,
            WATCHER_NAME,
            Gio.BusNameWatcherFlags.NONE,
            self._on_watcher_appeared,
            None,
        )

    def _on_watcher_appeared(
        self, _conn: Gio.DBusConnection, _name: str, _owner: str
    ) -> None:
        bus_name = self._conn.get_unique_name()
        if not bus_name:
            return
        self._conn.call(
            WATCHER_NAME,
            WATCHER_PATH,
            WATCHER_NAME,
            "RegisterStatusNotifierItem",
            GLib.Variant("(s)", (bus_name,)),
            None,
            Gio.DBusCallFlags.NONE,
            -1,
            None,
            None,
        )

    def update(self, active: bool, description: str) -> None:
        """Update tray tooltip and notify listeners."""
        self._status = "Active"
        self._tooltip_desc = description if active else f"{description} (Durduruldu)"
        try:
            self._conn.emit_signal(
                None,
                SNI_PATH,
                "org.kde.StatusNotifierItem",
                "NewToolTip",
                None,
            )
        except GLib.Error:
            pass

    def _handle_method_call(
        self,
        _conn: Gio.DBusConnection,
        _sender: str,
        _path: str,
        _iface: str,
        method_name: str,
        _params: GLib.Variant,
        invocation: Gio.DBusMethodInvocation,
    ) -> None:
        if method_name in ("Activate", "ContextMenu"):
            GLib.idle_add(self._invoke_activate)
        elif method_name == "SecondaryActivate" and self._on_secondary is not None:
            GLib.idle_add(self._invoke_secondary)
        invocation.return_value(None)

    def _invoke_activate(self) -> bool:
        self._on_activate()
        return False

    def _invoke_secondary(self) -> bool:
        if self._on_secondary is not None:
            self._on_secondary()
        return False

    def _handle_get_property(
        self,
        _conn: Gio.DBusConnection,
        _sender: str,
        _path: str,
        _iface: str,
        prop_name: str,
    ) -> GLib.Variant:
        icon_theme = str(Path.home() / ".local" / "share" / "icons")
        if prop_name == "Category":
            return GLib.Variant("s", "ApplicationStatus")
        if prop_name == "Id":
            return GLib.Variant("s", "ekran-kaydedici")
        if prop_name == "Title":
            return GLib.Variant("s", "Ekran Kaydedici")
        if prop_name == "Status":
            return GLib.Variant("s", self._status)
        if prop_name == "IconName":
            return GLib.Variant("s", "ekran-kaydedici")
        if prop_name == "IconThemePath":
            return GLib.Variant("s", icon_theme)
        if prop_name == "ItemIsMenu":
            return GLib.Variant("b", False)
        if prop_name == "ToolTip":
            return GLib.Variant(
                "(sa(iiay)ss)",
                ("ekran-kaydedici", [], self._tooltip_title, self._tooltip_desc),
            )
        return GLib.Variant("s", "")

    def destroy(self) -> None:
        if self._watch_id:
            Gio.bus_unwatch_name(self._watch_id)
            self._watch_id = 0
        if self._reg_id:
            self._conn.unregister_object(self._reg_id)
            self._reg_id = 0
