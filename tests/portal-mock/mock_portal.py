#!/usr/bin/env python3
"""Minimal org.freedesktop.portal.ScreenCast implementation for automated tests.

Serves PipeWire video nodes (MOCK_NODE, comma separated) through the same D-Bus protocol as
xdg-desktop-portal: CreateSession/SelectSources/Start answer through
org.freedesktop.portal.Request::Response signals and OpenPipeWireRemote passes a
connected PipeWire socket as a unix fd.
"""
import os
import socket

import dbus
import dbus.service
from dbus.mainloop.glib import DBusGMainLoop
from gi.repository import GLib

DBusGMainLoop(set_as_default=True)
bus = dbus.SessionBus()
NODES = [int(n) for n in os.environ["MOCK_NODE"].split(",") if n]
WIDTH, HEIGHT = int(os.environ.get("MOCK_WIDTH", "1280")), int(os.environ.get("MOCK_HEIGHT", "720"))
IFACE = "org.freedesktop.portal.ScreenCast"


def sender_path(sender):
    return sender[1:].replace(".", "_")


class Request(dbus.service.Object):
    @dbus.service.signal("org.freedesktop.portal.Request", signature="ua{sv}")
    def Response(self, code, results):
        pass


class Session(dbus.service.Object):
    @dbus.service.method("org.freedesktop.portal.Session", in_signature="", out_signature="")
    def Close(self):
        print("mock: session closed", flush=True)


class ScreenCast(dbus.service.Object):
    def __init__(self):
        super().__init__(bus, "/org/freedesktop/portal/desktop")

    def _request(self, sender, token, results):
        path = "/org/freedesktop/portal/desktop/request/%s/%s" % (sender_path(sender), token)
        request = Request(bus, path)

        def respond():
            request.Response(dbus.UInt32(0), dbus.Dictionary(results, signature="sv"))
            return False

        GLib.timeout_add(50, respond)
        return dbus.ObjectPath(path)

    @dbus.service.method(IFACE, in_signature="a{sv}", out_signature="o", sender_keyword="sender")
    def CreateSession(self, options, sender=None):
        session = "/org/freedesktop/portal/desktop/session/%s/%s" % (sender_path(sender), options["session_handle_token"])
        Session(bus, session)
        print("mock: CreateSession", session, flush=True)
        return self._request(sender, options["handle_token"], {"session_handle": dbus.String(session)})

    @dbus.service.method(IFACE, in_signature="oa{sv}", out_signature="o", sender_keyword="sender")
    def SelectSources(self, session, options, sender=None):
        print("mock: SelectSources", {k: str(v) for k, v in options.items()}, flush=True)
        return self._request(sender, options["handle_token"], {})

    @dbus.service.method(IFACE, in_signature="osa{sv}", out_signature="o", sender_keyword="sender")
    def Start(self, session, parent_window, options, sender=None):
        streams = dbus.Array(signature="(ua{sv})")
        for index, node in enumerate(NODES):
            props = dbus.Dictionary({
                "position": dbus.Struct((dbus.Int32(index * WIDTH), dbus.Int32(0)), signature="ii"),
                "size": dbus.Struct((dbus.Int32(WIDTH), dbus.Int32(HEIGHT)), signature="ii"),
                "source_type": dbus.UInt32(1),
                "id": dbus.String("mock-output-%d" % index),
            }, signature="sv")
            streams.append(dbus.Struct((dbus.UInt32(node), props), signature="ua{sv}"))
        print("mock: Start ->", NODES, flush=True)
        return self._request(sender, options["handle_token"], {"streams": streams, "restore_token": dbus.String("mock-restore-token")})

    @dbus.service.method(IFACE, in_signature="oa{sv}", out_signature="h")
    def OpenPipeWireRemote(self, session, options):
        s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        s.connect(os.path.join(os.environ["XDG_RUNTIME_DIR"], "pipewire-0"))
        print("mock: OpenPipeWireRemote", flush=True)
        return dbus.types.UnixFd(s)

    @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="ss", out_signature="v")
    def Get(self, interface, prop):
        return {"version": dbus.UInt32(5), "AvailableCursorModes": dbus.UInt32(7), "AvailableSourceTypes": dbus.UInt32(7)}[prop]


name = dbus.service.BusName("org.freedesktop.portal.Desktop", bus, replace_existing=True, allow_replacement=True, do_not_queue=True)
ScreenCast()
print("mock portal ready", flush=True)
GLib.MainLoop().run()
