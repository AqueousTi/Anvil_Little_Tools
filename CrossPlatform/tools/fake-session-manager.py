#!/usr/bin/env python3
"""A stand-in for GNOME's org.gnome.SessionManager on a private session bus.

Real logout cannot be tested without logging the user out, so
`session-logout-verify.sh` starts a private `dbus-daemon`, puts this service on
it, and points the app at that bus with DBUS_SESSION_BUS_ADDRESS. The app then
talks a *real* D-Bus link (real bus, real name, real property read, real signal
delivery) to a session manager we control.

Interface (same name/path/interface as GNOME, plus two controls):

  property  org.gnome.SessionManager.SessionIsActive   b  (read)
  signal    org.gnome.SessionManager.SessionOver
  method    org.gnome.SessionManager.SetSessionActive(b)   test control
  method    org.gnome.SessionManager.EmitSessionOver()     test control

`SetSessionActive` also emits the `PropertiesChanged` signal the way
gnome-session does, so a client that watches properties is tested as well.

  fake-session-manager.py --active true|false [--log FILE]
"""
import argparse
import sys
import time

try:
    from gi.repository import GLib, Gio
except ImportError:  # pragma: no cover - the harness has PyGObject installed
    sys.stderr.write("python3-gi is required (from gi.repository import Gio)\n")
    sys.exit(2)

INTERFACE = "org.gnome.SessionManager"
PROPERTIES = "org.freedesktop.DBus.Properties"
PATH = "/org/gnome/SessionManager"

INTROSPECTION = """
<node>
  <interface name='org.gnome.SessionManager'>
    <property name='SessionIsActive' type='b' access='read'/>
    <signal name='SessionOver'/>
    <method name='Logout'><arg type='u' direction='in'/></method>
    <method name='SetSessionActive'><arg type='b' direction='in'/></method>
    <method name='EmitSessionOver'/>
  </interface>
</node>
"""


class FakeSessionManager:
    def __init__(self, active, log_path):
        self.active = active
        self.log_path = log_path
        self.connection = None

    def log(self, message):
        if not self.log_path:
            return
        with open(self.log_path, "a", encoding="utf-8") as handle:
            handle.write(f"{time.time():.3f} {message}\n")

    def on_get_property(self, _connection, _sender, _path, interface, name):
        if interface != INTERFACE or name != "SessionIsActive":
            raise GLib.Error(f"unknown property {interface}.{name}")
        self.log(f"get SessionIsActive -> {str(self.active).lower()}")
        return GLib.Variant("b", self.active)

    def set_active(self, active):
        self.active = active
        self.connection.emit_signal(
            None, PATH, PROPERTIES, "PropertiesChanged",
            GLib.Variant("(sa{sv}as)", (INTERFACE, {"SessionIsActive": GLib.Variant("b", active)}, [])))
        self.log(f"properties-changed SessionIsActive -> {str(active).lower()}")

    def emit_session_over(self):
        self.connection.emit_signal(None, PATH, INTERFACE, "SessionOver", None)
        self.log("signal SessionOver")

    def on_method_call(self, connection, _sender, _path, interface, method, parameters, invocation):
        if interface != INTERFACE:
            invocation.return_error_literal(Gio.IOErrorEnum, Gio.IOErrorEnum.FAILED, "wrong interface")
            return
        if method == "SetSessionActive":
            (active,) = parameters.unpack()
            self.set_active(bool(active))
            invocation.return_value(None)
        elif method == "EmitSessionOver":
            self.emit_session_over()
            invocation.return_value(None)
        elif method == "Logout":
            self.log("method Logout (ignored by the stand-in)")
            invocation.return_value(None)
        else:
            invocation.return_value(None)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--active", choices=["true", "false"], default="true")
    parser.add_argument("--log", default=None)
    args = parser.parse_args()

    manager = FakeSessionManager(args.active == "true", args.log)
    node = Gio.DBusNodeInfo.new_for_xml(INTROSPECTION)
    connection = Gio.bus_get_sync(Gio.BusType.SESSION, None)
    manager.connection = connection
    connection.register_object(
        PATH, node.interfaces[0], manager.on_method_call, manager.on_get_property, None)

    if not Gio.bus_own_name_on_connection(
            connection, INTERFACE, Gio.BusNameOwnerFlags.NONE, None, None):
        sys.stderr.write(f"could not own {INTERFACE}\n")
        return 1

    manager.log(f"owning {INTERFACE} on {connection.get_unique_name()}")
    print(f"fake {INTERFACE} ready on {PATH} (SessionIsActive={str(manager.active).lower()})", flush=True)
    GLib.MainLoop().run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
