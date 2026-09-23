/* Local test helper (dev-only): map a plain managed full screen window of a
 * given colour so a translucent panel can be measured against a known backdrop.
 *
 *   backdrop <white|black|#RRGGBB> [width] [height]
 *
 * Prints "backdrop <winid>" and stays alive until killed. Raise the panel after
 * it with xraisetool; an always-on-top panel stays above it.
 */
#include <X11/Xlib.h>
#include <X11/Xatom.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

int main(int argc, char **argv) {
    if (argc < 2) { fprintf(stderr, "usage: %s <white|black|#RRGGBB> [w] [h] [--or]\n", argv[0]); return 2; }
    int override = 0;
    for (int i = 1; i < argc; i++) if (strcmp(argv[i], "--or") == 0) override = 1;
    unsigned long pixel;
    if (strcmp(argv[1], "white") == 0) pixel = 0xFFFFFF;
    else if (strcmp(argv[1], "black") == 0) pixel = 0x000000;
    else pixel = strtoul(argv[1] + (argv[1][0] == '#' ? 1 : 0), NULL, 16);

    Display *d = XOpenDisplay(NULL);
    if (!d) { fprintf(stderr, "cannot open display\n"); return 1; }
    int screen = DefaultScreen(d);
    int w = argc > 2 ? atoi(argv[2]) : 2560;
    int h = argc > 3 ? atoi(argv[3]) : 1440;

    XSetWindowAttributes attrs;
    attrs.background_pixel = pixel;
    attrs.override_redirect = override ? True : False;
    attrs.event_mask = ExposureMask;
    Window win = XCreateWindow(d, RootWindow(d, screen), 0, 0, w, h, 0,
                               CopyFromParent, InputOutput, CopyFromParent,
                               CWBackPixel | CWOverrideRedirect | CWEventMask, &attrs);
    XStoreName(d, win, "LittleTools Backdrop");
    /* No decorations, normal type, keep it out of the taskbar. */
    Atom motif = XInternAtom(d, "_MOTIF_WM_HINTS", False);
    struct { unsigned long flags, functions, decorations; long input_mode; unsigned long status; } hints;
    memset(&hints, 0, sizeof hints);
    hints.flags = 2; hints.decorations = 0;
    XChangeProperty(d, win, motif, motif, 32, PropModeReplace, (unsigned char *)&hints, 5);
    Atom type = XInternAtom(d, "_NET_WM_WINDOW_TYPE", False);
    Atom normal = XInternAtom(d, "_NET_WM_WINDOW_TYPE_NORMAL", False);
    XChangeProperty(d, win, type, XA_ATOM, 32, PropModeReplace, (unsigned char *)&normal, 1);
    Atom skip = XInternAtom(d, "_NET_WM_STATE_SKIP_TASKBAR", False);
    Atom state = XInternAtom(d, "_NET_WM_STATE", False);
    XChangeProperty(d, win, state, XA_ATOM, 32, PropModeReplace, (unsigned char *)&skip, 1);

    XMapWindow(d, win);
    /* The window manager likes to place a new window wherever it wants, so the
       geometry is re-asserted until it sticks (or forever for override-redirect). */
    for (int i = 0; i < 40; i++) {
        /* Keep the 32px top bar (and the tray icons in it) visible: the panel
           menu opens below the bar and has to be measurable against this window. */
        XMoveResizeWindow(d, win, 0, 32, w, h > 32 ? h - 32 : h);
        XSync(d, False);
        usleep(50000);
    }
    printf("backdrop 0x%lx %dx%d+0+0 #%06lx\n", (unsigned long)win, w, h, pixel);
    fflush(stdout);
    for (;;) { XEvent e; XNextEvent(d, &e); }
    return 0;
}
