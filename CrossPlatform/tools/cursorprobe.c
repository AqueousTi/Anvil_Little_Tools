#include <stdio.h>
#include <X11/Xlib.h>
#include <X11/extensions/Xfixes.h>
int main(int argc, char **argv) {
    Display *d = XOpenDisplay(NULL);
    int ev, er;
    if (!d || !XFixesQueryExtension(d, &ev, &er)) return 1;
    XFixesCursorImage *img = XFixesGetCursorImage(d);
    if (!img) return 3;
    printf("name=\"%s\" %dx%d hotspot=(%d,%d)\n", img->name ? img->name : "", img->width, img->height, img->xhot, img->yhot);
    if (argc > 1) {
        for (int y = 0; y < img->height; y++) {
            for (int x = 0; x < img->width; x++) {
                unsigned int a = (img->pixels[y * img->width + x] >> 24) & 0xff;
                unsigned int r = (img->pixels[y * img->width + x] >> 16) & 0xff;
                unsigned int g = (img->pixels[y * img->width + x] >> 8) & 0xff;
                unsigned int b = img->pixels[y * img->width + x] & 0xff;
                if (a < 100) putchar(' ');
                else if (r > 200 && g < 100 && b < 100) putchar('R');
                else if (r < 100 && g < 100 && b < 100) putchar('#');
                else if (r > 200 && g > 200 && b > 200) putchar('.');
                else putchar('+');
            }
            putchar('\n');
        }
    }
    XFree(img); XCloseDisplay(d); return 0;
}
