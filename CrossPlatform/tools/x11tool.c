/* X11 helper for verifying Little Tools hotkeys on a real display.
 *
 *   x11tool grab-check          report whether each hotkey grab is available
 *   x11tool send <key> <mods>   inject a key chord with XTest (mods: shift,ctrl,alt)
 *
 * Build: gcc -O2 -o x11tool x11tool.c -lX11 -lXtst
 */
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <unistd.h>
#include <X11/Xlib.h>
#include <X11/keysym.h>
#include <X11/extensions/XTest.h>

static int grab_denied = 0;
static int on_error(Display *d, XErrorEvent *e) { (void)d; if (e->error_code == BadAccess) grab_denied = 1; return 0; }

struct chord { const char *name; KeySym sym; unsigned int mods; };
static struct chord chords[] = {
    { "Shift+Backspace",   XK_BackSpace, ShiftMask },
    { "Ctrl+Backspace",    XK_BackSpace, ControlMask },
    { "Ctrl+Alt+X",        XK_x,         ControlMask | Mod1Mask },
    { "Ctrl+Alt+Q",        XK_q,         ControlMask | Mod1Mask },
};

static int check(Display *d) {
    Window root = DefaultRootWindow(d);
    for (unsigned i = 0; i < sizeof(chords) / sizeof(chords[0]); i++) {
        KeyCode code = XKeysymToKeycode(d, chords[i].sym);
        int ok = 0, denied = 0;
        unsigned int variants[4] = { 0, LockMask, Mod2Mask, LockMask | Mod2Mask };
        for (int v = 0; v < 4; v++) {
            grab_denied = 0;
            XGrabKey(d, code, chords[i].mods | variants[v], root, True, GrabModeAsync, GrabModeAsync);
            XSync(d, False);
            if (grab_denied) denied++; else ok++;
        }
        printf("%-16s ok=%d denied=%d\n", chords[i].name, ok, denied);
        for (int v = 0; v < 4; v++) XUngrabKey(d, code, chords[i].mods | variants[v], root);
        XSync(d, False);
    }
    return 0;
}

static int send_chord(Display *d, const char *key, const char *mods) {
    KeySym sym = XStringToKeysym(key);
    if (sym == NoSymbol) { fprintf(stderr, "unknown key: %s\n", key); return 2; }
    KeyCode code = XKeysymToKeycode(d, sym);
    KeyCode shift = XKeysymToKeycode(d, XK_Shift_L);
    KeyCode ctrl = XKeysymToKeycode(d, XK_Control_L);
    KeyCode alt = XKeysymToKeycode(d, XK_Alt_L);
    int use_shift = strstr(mods, "shift") != NULL;
    int use_ctrl = strstr(mods, "ctrl") != NULL;
    int use_alt = strstr(mods, "alt") != NULL;
    if (use_shift) XTestFakeKeyEvent(d, shift, True, 0);
    if (use_ctrl) XTestFakeKeyEvent(d, ctrl, True, 0);
    if (use_alt) XTestFakeKeyEvent(d, alt, True, 0);
    XTestFakeKeyEvent(d, code, True, 0);
    XTestFakeKeyEvent(d, code, False, 0);
    if (use_alt) XTestFakeKeyEvent(d, alt, False, 0);
    if (use_ctrl) XTestFakeKeyEvent(d, ctrl, False, 0);
    if (use_shift) XTestFakeKeyEvent(d, shift, False, 0);
    XSync(d, False);
    printf("sent %s+%s\n", mods, key);
    return 0;
}

static int click_at(Display *d, int x, int y) {
    XTestFakeMotionEvent(d, -1, x, y, 0);
    XSync(d, False);
    usleep(60000);
    XTestFakeButtonEvent(d, 1, True, 0);
    XSync(d, False);
    usleep(40000);
    XTestFakeButtonEvent(d, 1, False, 0);
    XSync(d, False);
    usleep(40000);
    printf("clicked %d,%d\n", x, y);
    return 0;
}

static int drag_to(Display *d, int x1, int y1, int x2, int y2, int hold) {
    XTestFakeMotionEvent(d, -1, x1, y1, 0); XSync(d, False); usleep(80000);
    XTestFakeButtonEvent(d, 1, True, 0);    XSync(d, False); usleep(120000);
    for (int step = 1; step <= 12; step++) {
        int x = x1 + (x2 - x1) * step / 12;
        int y = y1 + (y2 - y1) * step / 12;
        XTestFakeMotionEvent(d, -1, x, y, 0); XSync(d, False); usleep(60000);
    }
    if (!hold) { XTestFakeButtonEvent(d, 1, False, 0); XSync(d, False); }
    printf("dragged %d,%d -> %d,%d hold=%d\n", x1, y1, x2, y2, hold);
    return 0;
}

static int move_to(Display *d, int x, int y) {
    XTestFakeMotionEvent(d, -1, x, y, 0); XSync(d, False); usleep(120000);
    printf("moved %d,%d\n", x, y);
    return 0;
}

static int release_button(Display *d) {
    XTestFakeButtonEvent(d, 1, False, 0); XSync(d, False); usleep(60000);
    printf("released\n");
    return 0;
}

int main(int argc, char **argv) {
    Display *d = XOpenDisplay(NULL);
    if (!d) { fprintf(stderr, "cannot open display\n"); return 1; }
    XSetErrorHandler(on_error);
    int status = 0;
    if (argc >= 2 && strcmp(argv[1], "grab-check") == 0) status = check(d);
    else if (argc >= 4 && strcmp(argv[1], "send") == 0) status = send_chord(d, argv[2], argv[3]);
    else if (argc >= 4 && strcmp(argv[1], "click") == 0) status = click_at(d, atoi(argv[2]), atoi(argv[3]));
    else if (argc >= 7 && strcmp(argv[1], "drag") == 0) status = drag_to(d, atoi(argv[2]), atoi(argv[3]), atoi(argv[4]), atoi(argv[5]), atoi(argv[6]));
    else if (argc >= 4 && strcmp(argv[1], "move") == 0) status = move_to(d, atoi(argv[2]), atoi(argv[3]));
    else if (argc >= 2 && strcmp(argv[1], "release") == 0) status = release_button(d);
    else { fprintf(stderr, "usage: %s grab-check | send <key> <mods> | click <x> <y>\n", argv[0]); status = 2; }
    XCloseDisplay(d);
    return status;
}
