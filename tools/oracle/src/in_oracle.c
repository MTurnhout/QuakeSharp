/*
 * in_oracle.c: the oracle's mouse and joystick (design.md decision 43). WinQuake's in_win.c is compiled unchanged; the
 * Win32 calls it makes come here instead of to user32 and winmm, and a virtual machine answers them:
 *
 * - a screen of ORACLE_SCREEN_WIDTH x ORACLE_SCREEN_HEIGHT with a cursor, which -traceevents moves (@mouse <dx> <dy>)
 *   and whose buttons it sets (@buttons <state>), each delivered as the window procedure would get it: the cursor
 *   confined to the screen, or to the rectangle ClipCursor gives, as Windows confines it (the right and bottom edges
 *   excluded); a move is followed by WM_MOUSEMOVE's IN_MouseEvent, a button change by its WM_xBUTTONxxx's;
 * - no joystick: winmm's 16 slots, each answering JOYERR_UNPLUGGED, so in_win.c prints "joystick not found -- no valid
 *   joysticks (a7)" as on a Windows machine without one (design.md §3.4);
 * - the system's mouse parameters, Windows' defaults (thresholds 6 and 10, acceleration 1), which in_win.c reads and
 *   sets while it has the mouse.
 *
 * DirectInput (-dinput) is not part of it. QuakeSharp's VirtualInput answers the same calls the same way.
 */
#include <windows.h>
#include <mmsystem.h>
#include <string.h>

#define ORACLE_SCREEN_WIDTH		1920
#define ORACLE_SCREEN_HEIGHT	1080
#define ORACLE_JOYSTICKS		16
#define ORACLE_WHEEL_MESSAGE	0xc0f0		// RegisterWindowMessage's number for "MSWHEEL_ROLLMSG"

void IN_MouseEvent (int mstate);	// in_win.c

// winquake.h: the instance in_win.c hands DirectInput (never, without -dinput)
HINSTANCE global_hInstance;

static int cursor_x = ORACLE_SCREEN_WIDTH / 2, cursor_y = ORACLE_SCREEN_HEIGHT / 2;
static int clipping;
static RECT clip;
static int buttons;			// MK_LBUTTON, MK_RBUTTON, MK_MBUTTON as in_win.c's mstate: 1, 2, 4
static int cursor_count;	// ShowCursor's display counter
static int captured;
static int mouse_parms[3] = {6, 10, 1};

// the cursor confined to the screen and to the clip rectangle
static void Confine (void)
{
	int left = 0, top = 0, right = ORACLE_SCREEN_WIDTH, bottom = ORACLE_SCREEN_HEIGHT;

	if (clipping)
	{
		if (clip.left > left) left = clip.left;
		if (clip.top > top) top = clip.top;
		if (clip.right < right) right = clip.right;
		if (clip.bottom < bottom) bottom = clip.bottom;
	}
	if (cursor_x >= right) cursor_x = right - 1;
	if (cursor_x < left) cursor_x = left;
	if (cursor_y >= bottom) cursor_y = bottom - 1;
	if (cursor_y < top) cursor_y = top;
}

BOOL WINAPI GetCursorPos (LPPOINT point)
{
	point->x = cursor_x;
	point->y = cursor_y;
	return TRUE;
}

BOOL WINAPI SetCursorPos (int x, int y)
{
	cursor_x = x;
	cursor_y = y;
	Confine ();
	return TRUE;
}

BOOL WINAPI ClipCursor (const RECT *rect)
{
	clipping = rect != NULL;
	if (rect)
		clip = *rect;
	Confine ();
	return TRUE;
}

int WINAPI ShowCursor (BOOL show)
{
	return show ? ++cursor_count : --cursor_count;
}

HWND WINAPI SetCapture (HWND window)
{
	captured = 1;
	return NULL;
}

BOOL WINAPI ReleaseCapture (void)
{
	captured = 0;
	return TRUE;
}

BOOL WINAPI SystemParametersInfoA (UINT action, UINT param, PVOID data, UINT winini)
{
	switch (action)
	{
	case SPI_GETMOUSE:
		memcpy (data, mouse_parms, sizeof(mouse_parms));
		return TRUE;
	case SPI_SETMOUSE:
		memcpy (mouse_parms, data, sizeof(mouse_parms));
		return TRUE;
	}
	return FALSE;
}

UINT WINAPI RegisterWindowMessageA (LPCSTR name)
{
	return ORACLE_WHEEL_MESSAGE;
}

UINT WINAPI joyGetNumDevs (void)
{
	return ORACLE_JOYSTICKS;
}

MMRESULT WINAPI joyGetPosEx (UINT id, LPJOYINFOEX info)
{
	return JOYERR_UNPLUGGED;
}

MMRESULT WINAPI joyGetDevCapsA (UINT_PTR id, LPJOYCAPSA caps, UINT size)
{
	return JOYERR_UNPLUGGED;
}

// -traceevents' "@mouse <dx> <dy>": the mouse moved; the window procedure gets WM_MOUSEMOVE with the buttons' state
void Oracle_MouseMove (int dx, int dy)
{
	cursor_x += dx;
	cursor_y += dy;
	Confine ();
	IN_MouseEvent (buttons);
}

// -traceevents' "@buttons <state>": the buttons pressed (1 left, 2 right, 4 middle); the window procedure gets the
// button's message with their state
void Oracle_MouseButtons (int state)
{
	buttons = state;
	IN_MouseEvent (buttons);
}

// the trace's I line (trace.c): the virtual machine's state as ints (the cursor, the clip rectangle or zeros, the
// buttons, the display counter, the capture, the mouse parameters); returns how many
int Oracle_InputDevice (int *out)
{
	int n = 0;

	out[n++] = cursor_x;
	out[n++] = cursor_y;
	out[n++] = clipping;
	out[n++] = clipping ? clip.left : 0;
	out[n++] = clipping ? clip.top : 0;
	out[n++] = clipping ? clip.right : 0;
	out[n++] = clipping ? clip.bottom : 0;
	out[n++] = buttons;
	out[n++] = cursor_count;
	out[n++] = captured;
	out[n++] = mouse_parms[0];
	out[n++] = mouse_parms[1];
	out[n++] = mouse_parms[2];
	return n;
}
