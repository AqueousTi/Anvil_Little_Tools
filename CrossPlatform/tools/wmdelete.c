#include <X11/Xlib.h>
#include <X11/Xatom.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
int main(int argc,char**argv){
  if(argc<2){puts("usage: wmdelete <windowid>");return 1;}
  Display*d=XOpenDisplay(NULL); if(!d)return 1;
  Window w=(Window)strtoul(argv[1],NULL,0);
  Atom proto=XInternAtom(d,"WM_PROTOCOLS",False);
  Atom del=XInternAtom(d,"WM_DELETE_WINDOW",False);
  XEvent e; memset(&e,0,sizeof e);
  e.xclient.type=ClientMessage; e.xclient.window=w; e.xclient.message_type=proto;
  e.xclient.format=32; e.xclient.data.l[0]=del; e.xclient.data.l[1]=CurrentTime;
  XSendEvent(d,w,False,NoEventMask,&e); XFlush(d); XCloseDisplay(d);
  puts("sent WM_DELETE_WINDOW"); return 0;
}
