/* Local test helper (dev-only): raise/activate an X11 window by id so a
 * screenshot is not taken of whatever the user's browser happens to cover.
 *   xraisetool <windowid>
 * Mirrors what a taskbar click does: XRaiseWindow plus the EWMH
 * _NET_ACTIVE_WINDOW client message the window manager listens for.
 */
#include <X11/Xlib.h>
#include <X11/Xatom.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int main(int argc, char **argv) {
    if (argc < 2) { fprintf(stderr, "usage: %s <windowid>\n", argv[0]); return 2; }
    Window w = (Window)strtoul(argv[1], NULL, 0);
    Display *d = XOpenDisplay(NULL);
    if (!d) { fprintf(stderr, "cannot open display\n"); return 1; }
    XRaiseWindow(d, w);
    Atom active = XInternAtom(d, "_NET_ACTIVE_WINDOW", False);
    XEvent e;
    memset(&e, 0, sizeof e);
    e.xclient.type = ClientMessage;
    e.xclient.window = w;
    e.xclient.message_type = active;
    e.xclient.format = 32;
    e.xclient.data.l[0] = 1; /* source: application */
    e.xclient.data.l[1] = CurrentTime;
    XSendEvent(d, DefaultRootWindow(d), False,
               SubstructureRedirectMask | SubstructureNotifyMask, &e);
    XSync(d, False);
    XCloseDisplay(d);
    printf("raised 0x%lx\n", (unsigned long)w);
    return 0;
}
