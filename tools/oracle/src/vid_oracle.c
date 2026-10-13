/*
 * vid_oracle.c: the oracle's video driver, vid_win.c with the machine taken out (design.md decision 44). The game's
 * side of vid_win.c is kept: the mode list, VID_SetMode and its windowed and full-screen modes, the commands, the
 * Video menu, the first frame's switch to _vid_default_mode_win, the buffer's locking, the cvars (they go into
 * config.cfg), the remapping of colormap entries 0 to another black (GDI cannot remap palette index 0), S_Init between
 * the hidden first mode set and the real one, the surface cache, ClearAllStates, AppActivate's part, and what it does
 * with the mouse (in_win.c's, over in_oracle.c's virtual screen).
 *
 * The machine is the one Winquake.exe 1.09 was audited on (design.md §3.4): a 1920x1080 desktop, so the windowed
 * default is stretched (mode 1) unless -noautostretch; the windowed modes 320x240, 640x480 and 800x600 (the last two
 * stretched by vid_stretch_by_2); and DirectDraw (MGL's DDRAW8.DRV) with the 8-bit full-screen modes 320x240, 640x480,
 * 800x600 and 1024x768, each of one page, so vid_win.c draws them in a memory DC and blits it (vid.numpages 1). Every
 * frame stays in memory. The window's client area is at the screen's top left; the window keeps the focus, which it
 * gets when it is first shown. -oraclevid <width> <height> makes mode 0 a window of that size (the traces' other
 * sizes); the oracle's runs start in it with -startwindowed -noautostretch (Trace-Demos.ps1).
 *
 * Not part of it: -dibonly and the full-screen DIB modes (VID_InitFullDIB, only reached without DirectDraw modes),
 * the window's position (vid_window_x and vid_window_y are kept as cvars), minimizing, the palette of an 8-bit
 * desktop, and the loading disc (WinQuake draws it on the screen and restores it: never in the frame).
 */
#include "quakedef.h"
#include "d_local.h"
#include "winquake.h"
#include "trace.h"

#define MAX_MODE_LIST	30
#define VID_ROW_SIZE	3

qboolean	dibonly;

viddef_t	vid;				// global video state

int			window_center_x, window_center_y, window_x, window_y, window_width, window_height;
RECT		window_rect;

static qboolean	startwindowed = 0, windowed_mode_set;
static int		firstupdate = 1;
static qboolean	vid_initialized = false, vid_palettized;
static int		lockcount;
static int		vid_fulldib_on_focus_mode;
static qboolean	force_minimized, in_mode_set, is_mode0x13, force_mode_set;
static int		vid_stretched, windowed_mouse;
static qboolean	palette_changed, vid_mode_set, hide_window;

#define MODE_WINDOWED			0
#define MODE_SETTABLE_WINDOW	2
#define NO_MODE					(MODE_WINDOWED - 1)
#define MODE_FULLSCREEN_DEFAULT	(MODE_WINDOWED + 3)

// Note that 0 is MODE_WINDOWED
cvar_t		vid_mode = {"vid_mode","0", false};
// Note that 0 is MODE_WINDOWED
cvar_t		_vid_default_mode = {"_vid_default_mode","0", true};
// Note that 3 is MODE_FULLSCREEN_DEFAULT
cvar_t		_vid_default_mode_win = {"_vid_default_mode_win","3", true};
cvar_t		vid_wait = {"vid_wait","0"};
cvar_t		vid_nopageflip = {"vid_nopageflip","0", true};
cvar_t		_vid_wait_override = {"_vid_wait_override", "0", true};
cvar_t		vid_config_x = {"vid_config_x","800", true};
cvar_t		vid_config_y = {"vid_config_y","600", true};
cvar_t		vid_stretch_by_2 = {"vid_stretch_by_2","1", true};
cvar_t		_windowed_mouse = {"_windowed_mouse","0", true};
cvar_t		vid_fullscreen_mode = {"vid_fullscreen_mode","3", true};
cvar_t		vid_windowed_mode = {"vid_windowed_mode","0", true};
cvar_t		block_switch = {"block_switch","0", true};
cvar_t		vid_window_x = {"vid_window_x", "0", true};
cvar_t		vid_window_y = {"vid_window_y", "0", true};

int			vid_modenum = NO_MODE;
int			vid_testingmode, vid_realmode;
double		vid_testendtime;
int			vid_default = MODE_WINDOWED;
static int	windowed_default;

modestate_t	modestate = MS_UNINIT;

static byte		*vid_surfcache;
static int		vid_surfcachesize;

unsigned char	vid_curpal[256*3];

unsigned short	d_8to16table[256];
unsigned	d_8to24table[256];

void (*vid_menudrawfn)(void);
void (*vid_menukeyfn)(int key);

// winquake.h: what the shared code asks of the Windows video driver
qboolean	DDActive;
qboolean	ActiveApp = true, Minimized;

typedef struct {
	modestate_t	type;
	int			width;
	int			height;
	int			modenum;
	int			mode13;
	int			stretched;
	int			dib;
	int			fullscreen;
	int			bpp;
	int			halfscreen;
	char		modedesc[13];
} vmode_t;

static vmode_t	modelist[MAX_MODE_LIST];
static int		nummodes;

static vmode_t	badmode;

void VID_MenuDraw (void);
void VID_MenuKey (int key);
void AppActivate(BOOL fActive, BOOL minimize);

/*
===============================================================================

THE VIRTUAL MACHINE

===============================================================================
*/

// GetDeviceCaps (hdc, HORZRES): the desktop's width
#define ORACLE_DESKTOP_WIDTH	ORACLE_SCREEN_WIDTH

// MGL_availableModes and MGL_modeResolution: DirectDraw's 8-bit modes, by MGL's mode numbers (none is VGA's mode 0x13)
static const struct { int mode, width, height, bits; } oracle_mgl_modes[] = {
	{1, 320, 240, 8},
	{2, 640, 480, 8},
	{3, 800, 600, 8},
	{4, 1024, 768, 8},
};
#define ORACLE_MGL_MODES	(int)(sizeof(oracle_mgl_modes) / sizeof(oracle_mgl_modes[0]))

// MGL_modeDriverName
static const char *oracle_mode_driver = "DDRAW8.DRV";

// -oraclevid: mode 0's size
static int oracle_mode0_width = 320, oracle_mode0_height = 240;

// the windowed modes' DIB (MGL_createMemoryDC: rows of 4-byte multiples) and the full-screen modes' memory DC, with
// the z-buffer and the surface cache (VID_AllocBuffers' hunk block)
static byte		*dibdc, *memdc;
static int		dc_rowbytes;
static short	*zbuffer;

// whether the window has been shown: Windows activates it then
static qboolean	window_shown;

// The buffers start after some padding: a sprite whose clipped left edge rounds below the view's on its first row
// writes a pixel and a depth value just before the frame and the z-buffer (design.md §3.3, D_SpriteDrawSpans),
// which land there rather than in the heap's own bookkeeping
#define BUFFER_GUARD	64

static void *GuardedAlloc (int size)
{
	return (byte *)calloc (size + BUFFER_GUARD, 1) + BUFFER_GUARD;
}

static void GuardedFree (void *buffer)
{
	if (buffer)
		free ((byte *)buffer - BUFFER_GUARD);
}

static byte *MemoryDC (byte *old, int width, int height)
{
	GuardedFree (old);
	dc_rowbytes = (width + 3) & ~3;
	return GuardedAlloc (dc_rowbytes * height);
}

/*
===============================================================================

VID_WIN.C

===============================================================================
*/

/*
================
VID_UpdateWindowStatus
================
*/
void VID_UpdateWindowStatus (void)
{

	window_rect.left = window_x;
	window_rect.top = window_y;
	window_rect.right = window_x + window_width;
	window_rect.bottom = window_y + window_height;
	window_center_x = (window_rect.left + window_rect.right) / 2;
	window_center_y = (window_rect.top + window_rect.bottom) / 2;

	IN_UpdateClipCursor ();
}


/*
================
ClearAllStates
================
*/
void ClearAllStates (void)
{
	int		i;

// send an up event for each key, to make sure the server clears them all
	for (i=0 ; i<256 ; i++)
	{
		Key_Event (i, false);
	}

	Key_ClearStates ();
	IN_ClearStates ();
}


/*
================
VID_CheckAdequateMem
================
*/
qboolean VID_CheckAdequateMem (int width, int height)
{
	int		tbuffersize;

	tbuffersize = width * height * sizeof (*d_pzbuffer);

	tbuffersize += D_SurfaceCacheForRes (width, height);

// see if there's enough memory, allowing for the normal mode 0x13 pixel,
// z, and surface buffers
	if ((host_parms.memsize - tbuffersize + SURFCACHE_SIZE_AT_320X200 +
		 0x10000 * 3) < minimum_memory)
	{
		return false;		// not enough memory for mode
	}

	return true;
}


/*
================
VID_AllocBuffers

The z-buffer and the surface cache in a block of their own rather than on the hunk.
================
*/
qboolean VID_AllocBuffers (int width, int height)
{
	int		tsize, tbuffersize;

	tbuffersize = width * height * sizeof (*d_pzbuffer);

	tsize = D_SurfaceCacheForRes (width, height);

	tbuffersize += tsize;

// see if there's enough memory, allowing for the normal mode 0x13 pixel,
// z, and surface buffers
	if ((host_parms.memsize - tbuffersize + SURFCACHE_SIZE_AT_320X200 +
		 0x10000 * 3) < minimum_memory)
	{
		Con_SafePrintf ("Not enough memory for video mode\n");
		return false;		// not enough memory for mode
	}

	vid_surfcachesize = tsize;

	if (d_pzbuffer)
	{
		D_FlushCaches ();
		d_pzbuffer = NULL;
	}

	GuardedFree (zbuffer);
	zbuffer = GuardedAlloc (tbuffersize);
	d_pzbuffer = zbuffer;

	vid_surfcache = (byte *)d_pzbuffer +
			width * height * sizeof (*d_pzbuffer);

	return true;
}


/*
================
VID_InitMGLDIB: the windowed modes
================
*/
void VID_InitMGLDIB (void)
{
	modelist[0].type = MS_WINDOWED;
	modelist[0].width = oracle_mode0_width;
	modelist[0].height = oracle_mode0_height;
	sprintf (modelist[0].modedesc, "%dx%d", oracle_mode0_width, oracle_mode0_height);
	modelist[0].mode13 = 0;
	modelist[0].modenum = MODE_WINDOWED;
	modelist[0].stretched = 0;
	modelist[0].dib = 1;
	modelist[0].fullscreen = 0;
	modelist[0].halfscreen = 0;
	modelist[0].bpp = 8;

	modelist[1].type = MS_WINDOWED;
	modelist[1].width = 640;
	modelist[1].height = 480;
	strcpy (modelist[1].modedesc, "640x480");
	modelist[1].mode13 = 0;
	modelist[1].modenum = MODE_WINDOWED + 1;
	modelist[1].stretched = 1;
	modelist[1].dib = 1;
	modelist[1].fullscreen = 0;
	modelist[1].halfscreen = 0;
	modelist[1].bpp = 8;

	modelist[2].type = MS_WINDOWED;
	modelist[2].width = 800;
	modelist[2].height = 600;
	strcpy (modelist[2].modedesc, "800x600");
	modelist[2].mode13 = 0;
	modelist[2].modenum = MODE_WINDOWED + 2;
	modelist[2].stretched = 1;
	modelist[2].dib = 1;
	modelist[2].fullscreen = 0;
	modelist[2].halfscreen = 0;
	modelist[2].bpp = 8;

// automatically stretch the default mode up if > 640x480 desktop resolution
	if ((ORACLE_DESKTOP_WIDTH > 640) && !COM_CheckParm("-noautostretch"))
	{
		vid_default = MODE_WINDOWED + 1;
	}
	else
	{
		vid_default = MODE_WINDOWED;
	}

	windowed_default = vid_default;

	nummodes = 3;	// reserve space for windowed mode

	DDActive = 0;
}


/*
================
VID_InitMGLFull: the full-screen modes DirectDraw offers
================
*/
void VID_InitMGLFull (void)
{
	int			i, xRes, yRes, bits, lowres, curmode, mode;
	int			lowstretchedres, stretchedmode, lowstretched;

	lowres = lowstretchedres = 99999;
	lowstretched = 0;
	curmode = 0;
	mode = 0;
	stretchedmode = 0;

// find the lowest-res mode, or a mode we can stretch up to and get
// lowest-res that way
	for (i = 0; i < ORACLE_MGL_MODES; i++)
	{
		xRes = oracle_mgl_modes[i].width;
		yRes = oracle_mgl_modes[i].height;
		bits = oracle_mgl_modes[i].bits;

		if ((bits == 8) &&
			(xRes <= MAXWIDTH) &&
			(yRes <= MAXHEIGHT) &&
			(curmode < MAX_MODE_LIST))
		{
			if (xRes < lowres)
			{
				lowres = xRes;
				mode = i;
			}

			if ((xRes < lowstretchedres) && ((xRes >> 1) >= 320))
			{
				lowstretchedres = xRes >> 1;
				stretchedmode = i;
			}
		}

		curmode++;
	}

// if there's a mode we can stretch by 2 up to, thereby effectively getting
// a lower-res mode than the lowest-res real but still at least 320x200, that
// will be our default mode
	if (lowstretchedres < lowres)
	{
		mode = stretchedmode;
		lowres = lowstretchedres;
		lowstretched = 1;
	}

// build the mode list, leaving room for the low-res stretched mode, if any
	nummodes++;		// leave room for default mode

	for (i = 0; i < ORACLE_MGL_MODES; i++)
	{
		xRes = oracle_mgl_modes[i].width;
		yRes = oracle_mgl_modes[i].height;
		bits = oracle_mgl_modes[i].bits;

		if ((bits == 8) &&
			(xRes <= MAXWIDTH) &&
			(yRes <= MAXHEIGHT) &&
			(nummodes < MAX_MODE_LIST))
		{
			if (i == mode)
			{
				if (lowstretched)
				{
					stretchedmode = nummodes;
					curmode = nummodes++;
				}
				else
				{
					curmode = MODE_FULLSCREEN_DEFAULT;
				}
			}
			else
			{
				curmode = nummodes++;
			}

			modelist[curmode].type = MS_FULLSCREEN;
			modelist[curmode].width = xRes;
			modelist[curmode].height = yRes;
			sprintf (modelist[curmode].modedesc, "%dx%d", xRes, yRes);
			modelist[curmode].mode13 = 0;
			modelist[curmode].modenum = oracle_mgl_modes[i].mode;
			modelist[curmode].stretched = 0;
			modelist[curmode].dib = 0;
			modelist[curmode].fullscreen = 1;
			modelist[curmode].halfscreen = 0;
			modelist[curmode].bpp = 8;
		}
	}

	if (lowstretched)
	{
		modelist[MODE_FULLSCREEN_DEFAULT] = modelist[stretchedmode];
		modelist[MODE_FULLSCREEN_DEFAULT].stretched = 1;
		modelist[MODE_FULLSCREEN_DEFAULT].width >>= 1;
		modelist[MODE_FULLSCREEN_DEFAULT].height >>= 1;
		sprintf (modelist[MODE_FULLSCREEN_DEFAULT].modedesc, "%dx%d",
				 modelist[MODE_FULLSCREEN_DEFAULT].width,
				 modelist[MODE_FULLSCREEN_DEFAULT].height);
	}

	vid_default = MODE_FULLSCREEN_DEFAULT;
}


/*
=================
VID_NumModes
=================
*/
int VID_NumModes (void)
{
	return nummodes;
}


/*
=================
VID_GetModePtr
=================
*/
vmode_t *VID_GetModePtr (int modenum)
{

	if ((modenum >= 0) && (modenum < nummodes))
		return &modelist[modenum];
	else
		return &badmode;
}


/*
=================
VID_CheckModedescFixup
=================
*/
void VID_CheckModedescFixup (int mode)
{
	int		x, y, stretch;

	if (mode == MODE_SETTABLE_WINDOW)
	{
		modelist[mode].stretched = (int)vid_stretch_by_2.value;
		stretch = modelist[mode].stretched;

		if (vid_config_x.value < (320 << stretch))
			vid_config_x.value = 320 << stretch;

		if (vid_config_y.value < (200 << stretch))
			vid_config_y.value = 200 << stretch;

		x = (int)vid_config_x.value;
		y = (int)vid_config_y.value;
		sprintf (modelist[mode].modedesc, "%dx%d", x, y);
		modelist[mode].width = x;
		modelist[mode].height = y;
	}
}


/*
=================
VID_GetModeDescriptionMemCheck
=================
*/
char *VID_GetModeDescriptionMemCheck (int mode)
{
	char		*pinfo;
	vmode_t		*pv;

	if ((mode < 0) || (mode >= nummodes))
		return NULL;

	VID_CheckModedescFixup (mode);

	pv = VID_GetModePtr (mode);
	pinfo = pv->modedesc;

	if (VID_CheckAdequateMem (pv->width, pv->height))
	{
		return pinfo;
	}
	else
	{
		return NULL;
	}
}


/*
=================
VID_GetModeDescription
=================
*/
char *VID_GetModeDescription (int mode)
{
	char		*pinfo;
	vmode_t		*pv;

	if ((mode < 0) || (mode >= nummodes))
		return NULL;

	VID_CheckModedescFixup (mode);

	pv = VID_GetModePtr (mode);
	pinfo = pv->modedesc;
	return pinfo;
}


/*
=================
VID_GetModeDescription2

Tacks on "windowed" or "fullscreen"
=================
*/
char *VID_GetModeDescription2 (int mode)
{
	static char	pinfo[40];
	vmode_t		*pv;

	if ((mode < 0) || (mode >= nummodes))
		return NULL;

	VID_CheckModedescFixup (mode);

	pv = VID_GetModePtr (mode);

	if (modelist[mode].type == MS_FULLSCREEN)
	{
		sprintf(pinfo,"%s fullscreen", pv->modedesc);
	}
	else if (modelist[mode].type == MS_FULLDIB)
	{
		sprintf(pinfo,"%s fullscreen", pv->modedesc);
	}
	else
	{
		sprintf(pinfo, "%s windowed", pv->modedesc);
	}

	return pinfo;
}


// KJB: Added this to return the mode driver name in description for console

char *VID_GetExtModeDescription (int mode)
{
	static char	pinfo[40];
	vmode_t		*pv;

	if ((mode < 0) || (mode >= nummodes))
		return NULL;

	VID_CheckModedescFixup (mode);

	pv = VID_GetModePtr (mode);
	if (modelist[mode].type == MS_FULLSCREEN)
	{
		sprintf(pinfo,"%s fullscreen %s",pv->modedesc,
				oracle_mode_driver);
	}
	else if (modelist[mode].type == MS_FULLDIB)
	{
		sprintf(pinfo,"%s fullscreen DIB", pv->modedesc);
	}
	else
	{
		sprintf(pinfo, "%s windowed", pv->modedesc);
	}

	return pinfo;
}


// vid_win.c: DestroyDIBWindow, DestroyFullscreenWindow: the DCs
static void DestroyDIBWindow (void)
{

	if (modestate == MS_WINDOWED)
	{
	// destroy the associated MGL DC's; the window gets reused
		GuardedFree (dibdc);
		dibdc = NULL;
	}
}


static void DestroyFullscreenWindow (void)
{

	if (modestate == MS_FULLSCREEN)
	{
	// destroy the existing fullscreen mode and DC's
		GuardedFree (memdc);
		memdc = NULL;
	}
}


/*
================
VID_SetWindowedMode

The window is shown the first time a mode is set in it, and Windows then activates it (WM_ACTIVATE).
================
*/
qboolean VID_SetWindowedMode (int modenum)
{
	qboolean		stretched;
	int				DIBWidth, DIBHeight;

	if (!windowed_mode_set)
	{
		if (COM_CheckParm ("-resetwinpos"))
		{
			Cvar_SetValue ("vid_window_x", 0.0);
			Cvar_SetValue ("vid_window_y", 0.0);
		}

		windowed_mode_set;
	}

	VID_CheckModedescFixup (modenum);

	DDActive = 0;

	DestroyFullscreenWindow ();

	GuardedFree (dibdc);
	dibdc = NULL;

	stretched = modelist[modenum].stretched;

	DIBWidth = modelist[modenum].width;
	DIBHeight = modelist[modenum].height;

	if (stretched)
	{
		DIBWidth >>= 1;
		DIBHeight >>= 1;
	}

// the first time we're called to set the mode, create the window we'll use
// for the rest of the session
	if (!vid_mode_set)
		vid_mode_set = true;

	if (hide_window)
		return true;

// position and show the DIB window: the first time, Windows activates it
	if (!window_shown)
	{
		window_shown = true;
		AppActivate (true, false);
		ClearAllStates ();
	}

	modestate = MS_WINDOWED;
	vid_fulldib_on_focus_mode = 0;

	/* Create the MGL window DC and the MGL memory DC */
	dibdc = MemoryDC (dibdc, DIBWidth, DIBHeight);

	vid.buffer = vid.conbuffer = vid.direct = dibdc;
	vid.rowbytes = vid.conrowbytes = dc_rowbytes;
	vid.numpages = 1;
	vid.maxwarpwidth = WARP_WIDTH;
	vid.maxwarpheight = WARP_HEIGHT;
	vid.height = vid.conheight = DIBHeight;
	vid.width = vid.conwidth = DIBWidth;
	vid.aspect = ((float)vid.height / (float)vid.width) *
				(320.0 / 240.0);

	vid_stretched = stretched;

	return true;
}


/*
================
VID_SetFullscreenMode

createDisplayDC's memory DC: the display has one page.
================
*/
qboolean VID_SetFullscreenMode (int modenum)
{

	DDActive = 1;

	DestroyDIBWindow ();

	// Destroy old DC's, resetting back to fullscreen mode
	GuardedFree (memdc);
	memdc = NULL;

	memdc = MemoryDC (memdc, modelist[modenum].width, modelist[modenum].height);
	vid.numpages = 1;

	modestate = MS_FULLSCREEN;
	vid_fulldib_on_focus_mode = 0;

	vid.buffer = vid.conbuffer = vid.direct = NULL;
	vid.maxwarpwidth = WARP_WIDTH;
	vid.maxwarpheight = WARP_HEIGHT;
	vid.height = vid.conheight = modelist[modenum].height;
	vid.width = vid.conwidth = modelist[modenum].width;
	vid.aspect = ((float)vid.height / (float)vid.width) *
				(320.0 / 240.0);

	vid_stretched = modelist[modenum].stretched;

// needed because we're not getting WM_MOVE messages fullscreen on NT
	window_x = 0;
	window_y = 0;

// shouldn't be needed, but Kendall needs to let us get the activation
// message for this not to be needed on NT
	AppActivate (true, false);

	return true;
}


void VID_RestoreOldMode (int original_mode)
{
	static qboolean	inerror = false;

	if (inerror)
		return;

	in_mode_set = false;
	inerror = true;

// make sure mode set happens (video mode changes)
	vid_modenum = original_mode - 1;

	if (!VID_SetMode (original_mode, vid_curpal))
	{
		vid_modenum = MODE_WINDOWED - 1;

		if (!VID_SetMode (windowed_default, vid_curpal))
			Sys_Error ("Can't set any video mode");
	}

	inerror = false;
}


void VID_SetDefaultMode (void)
{

	if (vid_initialized)
		VID_SetMode (0, vid_curpal);

	IN_DeactivateMouse ();
}


int VID_SetMode (int modenum, unsigned char *palette)
{
	int				original_mode, temp;
	qboolean		stat;

	while ((modenum >= nummodes) || (modenum < 0))
	{
		if (vid_modenum == NO_MODE)
		{
			if (modenum == vid_default)
			{
				modenum = windowed_default;
			}
			else
			{
				modenum = vid_default;
			}

			Cvar_SetValue ("vid_mode", (float)modenum);
		}
		else
		{
			Cvar_SetValue ("vid_mode", (float)vid_modenum);
			return 0;
		}
	}

	if (!force_mode_set && (modenum == vid_modenum))
		return true;

// so Con_Printfs don't mess us up by forcing vid and snd updates
	temp = scr_disabled_for_loading;
	scr_disabled_for_loading = true;
	in_mode_set = true;

	CDAudio_Pause ();
	S_ClearBuffer ();

	if (vid_modenum == NO_MODE)
		original_mode = windowed_default;
	else
		original_mode = vid_modenum;

	// Set either the fullscreen or windowed mode
	if (modelist[modenum].type == MS_WINDOWED)
	{
		if (_windowed_mouse.value)
		{
			stat = VID_SetWindowedMode(modenum);
			IN_ActivateMouse ();
			IN_HideMouse ();
		}
		else
		{
			IN_DeactivateMouse ();
			IN_ShowMouse ();
			stat = VID_SetWindowedMode(modenum);
		}
	}
	else
	{
		stat = VID_SetFullscreenMode(modenum);
		IN_ActivateMouse ();
		IN_HideMouse ();
	}

	window_width = vid.width << vid_stretched;
	window_height = vid.height << vid_stretched;
	VID_UpdateWindowStatus ();

	CDAudio_Resume ();
	scr_disabled_for_loading = temp;

	if (!stat)
	{
		VID_RestoreOldMode (original_mode);
		return false;
	}

	if (hide_window)
		return true;

	vid_palettized = false;

	VID_SetPalette (palette);

	vid_modenum = modenum;
	Cvar_SetValue ("vid_mode", (float)vid_modenum);

	if (!VID_AllocBuffers (vid.width, vid.height))
	{
	// couldn't get memory for this mode; try to fall back to previous mode
		VID_RestoreOldMode (original_mode);
		return false;
	}

	D_InitCaches (vid_surfcache, vid_surfcachesize);

// fix the leftover Alt from any Alt-Tab or the like that switched us away
	ClearAllStates ();

	if (!msg_suppress_1)
		Con_SafePrintf ("%s\n", VID_GetModeDescription (vid_modenum));

	VID_SetPalette (palette);

	in_mode_set = false;
	vid.recalc_refdef = 1;

	return true;
}


/*
================
VID_LockBuffer, VID_UnlockBuffer

In the full-screen modes the frame is only at hand while locked: locking sets vid.buffer and vid.rowbytes.
================
*/
void VID_LockBuffer (void)
{

	if (dibdc)
		return;

	lockcount++;

	if (lockcount > 1)
		return;

	if (memdc)
	{
		// Update surface pointer for linear access modes
		vid.buffer = vid.conbuffer = vid.direct = memdc;
		vid.rowbytes = vid.conrowbytes = dc_rowbytes;
	}

	if (r_dowarp)
		d_viewbuffer = r_warpbuffer;
	else
		d_viewbuffer = (void *)(byte *)vid.buffer;

	if (r_dowarp)
		screenwidth = WARP_WIDTH;
	else
		screenwidth = vid.rowbytes;

	if (lcd_x.value)
		screenwidth <<= 1;
}


void VID_UnlockBuffer (void)
{
	if (dibdc)
		return;

	lockcount--;

	if (lockcount > 0)
		return;

	if (lockcount < 0)
		Sys_Error ("Unbalanced unlock");

// to turn up any unlocked accesses
	vid.buffer = vid.conbuffer = vid.direct = d_viewbuffer = NULL;

}


int VID_ForceUnlockedAndReturnState (void)
{
	int	lk;

	if (!lockcount)
		return 0;

	lk = lockcount;

	if (dibdc)
	{
		lockcount = 0;
	}
	else
	{
		lockcount = 1;
		VID_UnlockBuffer ();
	}

	return lk;
}


void VID_ForceLockState (int lk)
{

	if (!dibdc && lk)
	{
		lockcount = 0;
		VID_LockBuffer ();
	}

	lockcount = lk;
}


void	VID_SetPalette (unsigned char *palette)
{

	if (!Minimized)
		palette_changed = true;

	memcpy (vid_curpal, palette, sizeof(vid_curpal));
	Trace_Palette (vid_curpal);
}


void	VID_ShiftPalette (unsigned char *palette)
{
	VID_SetPalette (palette);
}


/*
=================
VID_DescribeCurrentMode_f
=================
*/
void VID_DescribeCurrentMode_f (void)
{
	Con_Printf ("%s\n", VID_GetExtModeDescription (vid_modenum));
}


/*
=================
VID_NumModes_f
=================
*/
void VID_NumModes_f (void)
{

	if (nummodes == 1)
		Con_Printf ("%d video mode is available\n", nummodes);
	else
		Con_Printf ("%d video modes are available\n", nummodes);
}


/*
=================
VID_DescribeMode_f
=================
*/
void VID_DescribeMode_f (void)
{
	int		modenum;

	modenum = Q_atoi (Cmd_Argv(1));

	Con_Printf ("%s\n", VID_GetExtModeDescription (modenum));
}


/*
=================
VID_DescribeModes_f
=================
*/
void VID_DescribeModes_f (void)
{
	int			i, lnummodes;
	char		*pinfo;
	qboolean	na;
	vmode_t		*pv;

	na = false;

	lnummodes = VID_NumModes ();

	for (i=0 ; i<lnummodes ; i++)
	{
		pv = VID_GetModePtr (i);
		pinfo = VID_GetExtModeDescription (i);

		if (VID_CheckAdequateMem (pv->width, pv->height))
		{
			Con_Printf ("%2d: %s\n", i, pinfo);
		}
		else
		{
			Con_Printf ("**: %s\n", pinfo);
			na = true;
		}
	}

	if (na)
	{
		Con_Printf ("\n[**: not enough system RAM for mode]\n");
	}
}


/*
=================
VID_TestMode_f
=================
*/
void VID_TestMode_f (void)
{
	int		modenum;
	double	testduration;

	if (!vid_testingmode)
	{
		modenum = Q_atoi (Cmd_Argv(1));

		if (VID_SetMode (modenum, vid_curpal))
		{
			vid_testingmode = 1;
			testduration = Q_atof (Cmd_Argv(2));
			if (testduration == 0)
				testduration = 5.0;
			vid_testendtime = realtime + testduration;
		}
	}
}


/*
=================
VID_Windowed_f
=================
*/
void VID_Windowed_f (void)
{

	VID_SetMode ((int)vid_windowed_mode.value, vid_curpal);
}


/*
=================
VID_Fullscreen_f
=================
*/
void VID_Fullscreen_f (void)
{

	VID_SetMode ((int)vid_fullscreen_mode.value, vid_curpal);
}


/*
=================
VID_Minimize_f

(Minimizing is not part of the virtual machine: the window stays.)
=================
*/
void VID_Minimize_f (void)
{
}


/*
=================
VID_ForceMode_f
=================
*/
void VID_ForceMode_f (void)
{
	int		modenum;

	if (!vid_testingmode)
	{
		modenum = Q_atoi (Cmd_Argv(1));

		force_mode_set = 1;
		VID_SetMode (modenum, vid_curpal);
		force_mode_set = 0;
	}
}


void	VID_Init (unsigned char *palette)
{
	int		i, bestmatch, bestmatchmetric, t, dr, dg, db;
	int		basenummodes;
	byte	*ptmp;

	Cvar_RegisterVariable (&vid_mode);
	Cvar_RegisterVariable (&vid_wait);
	Cvar_RegisterVariable (&vid_nopageflip);
	Cvar_RegisterVariable (&_vid_wait_override);
	Cvar_RegisterVariable (&_vid_default_mode);
	Cvar_RegisterVariable (&_vid_default_mode_win);
	Cvar_RegisterVariable (&vid_config_x);
	Cvar_RegisterVariable (&vid_config_y);
	Cvar_RegisterVariable (&vid_stretch_by_2);
	Cvar_RegisterVariable (&_windowed_mouse);
	Cvar_RegisterVariable (&vid_fullscreen_mode);
	Cvar_RegisterVariable (&vid_windowed_mode);
	Cvar_RegisterVariable (&block_switch);
	Cvar_RegisterVariable (&vid_window_x);
	Cvar_RegisterVariable (&vid_window_y);

	Cmd_AddCommand ("vid_testmode", VID_TestMode_f);
	Cmd_AddCommand ("vid_nummodes", VID_NumModes_f);
	Cmd_AddCommand ("vid_describecurrentmode", VID_DescribeCurrentMode_f);
	Cmd_AddCommand ("vid_describemode", VID_DescribeMode_f);
	Cmd_AddCommand ("vid_describemodes", VID_DescribeModes_f);
	Cmd_AddCommand ("vid_forcemode", VID_ForceMode_f);
	Cmd_AddCommand ("vid_windowed", VID_Windowed_f);
	Cmd_AddCommand ("vid_fullscreen", VID_Fullscreen_f);
	Cmd_AddCommand ("vid_minimize", VID_Minimize_f);

	// -oraclevid <width> <height>: mode 0's size
	t = COM_CheckParm ("-oraclevid");
	if (t && t < com_argc - 2)
	{
		oracle_mode0_width = Q_atoi (com_argv[t + 1]);
		oracle_mode0_height = Q_atoi (com_argv[t + 2]);
		if (oracle_mode0_width < 320 || oracle_mode0_height < 200 || oracle_mode0_width > MAXWIDTH || oracle_mode0_height > MAXHEIGHT)
			Sys_Error ("-oraclevid: %dx%d is not between 320x200 and %dx%d", oracle_mode0_width, oracle_mode0_height, MAXWIDTH, MAXHEIGHT);
	}

	if (COM_CheckParm ("-dibonly"))
		dibonly = true;

	VID_InitMGLDIB ();

	basenummodes = nummodes;

	if (!dibonly)
		VID_InitMGLFull ();

	vid.maxwarpwidth = WARP_WIDTH;
	vid.maxwarpheight = WARP_HEIGHT;
	vid.colormap = host_colormap;
	vid.fullbright = 256 - LittleLong (*((int *)vid.colormap + 2048));
	vid_testingmode = 0;

// GDI doesn't let us remap palette index 0, so we'll remap color
// mappings from that black to another one
	bestmatchmetric = 256*256*3;

	for (i=1 ; i<256 ; i++)
	{
		dr = palette[0] - palette[i*3];
		dg = palette[1] - palette[i*3+1];
		db = palette[2] - palette[i*3+2];

		t = (dr * dr) + (dg * dg) + (db * db);

		if (t < bestmatchmetric)
		{
			bestmatchmetric = t;
			bestmatch = i;

			if (t == 0)
				break;
		}
	}

	for (i=0, ptmp = vid.colormap ; i<(1<<(VID_CBITS+8)) ; i++, ptmp++)
	{
		if (*ptmp == 0)
			*ptmp = bestmatch;
	}

	if (COM_CheckParm("-startwindowed"))
	{
		startwindowed = 1;
		vid_default = windowed_default;
	}

// sound initialization has to go here, preceded by a windowed mode set,
// so there's a window for DirectSound to work with but we're not yet
// fullscreen so the "hardware already in use" dialog is visible if it
// gets displayed

// keep the window minimized until we're ready for the first real mode set
	hide_window = true;
	VID_SetMode (MODE_WINDOWED, palette);
	hide_window = false;
	S_Init ();

	vid_initialized = true;

	force_mode_set = true;
	VID_SetMode (vid_default, palette);
	force_mode_set = false;

	vid_realmode = vid_modenum;

	VID_SetPalette (palette);

	vid_menudrawfn = VID_MenuDraw;
	vid_menukeyfn = VID_MenuKey;

	strcpy (badmode.modedesc, "Bad mode");
}


void	VID_Shutdown (void)
{
}


/*
================
VID_Update

The frame stays in memory: no FlipScreen.
================
*/
void	VID_Update (vrect_t *rects)
{

	if (firstupdate)
	{
		if ((_vid_default_mode_win.value != vid_default) &&
			(!startwindowed || (_vid_default_mode_win.value < MODE_FULLSCREEN_DEFAULT)))
		{
			firstupdate = 0;

			if (COM_CheckParm ("-resetwinpos"))
			{
				Cvar_SetValue ("vid_window_x", 0.0);
				Cvar_SetValue ("vid_window_y", 0.0);
			}

			if ((_vid_default_mode_win.value < 0) ||
				(_vid_default_mode_win.value >= nummodes))
			{
				Cvar_SetValue ("_vid_default_mode_win", windowed_default);
			}

			Cvar_SetValue ("vid_mode", _vid_default_mode_win.value);
		}
	}

	if (vid_testingmode)
	{
		if (realtime >= vid_testendtime)
		{
			VID_SetMode (vid_realmode, vid_curpal);
			vid_testingmode = 0;
		}
	}
	else
	{
		if ((int)vid_mode.value != vid_realmode)
		{
			VID_SetMode ((int)vid_mode.value, vid_curpal);
			Cvar_SetValue ("vid_mode", (float)vid_modenum);
								// so if mode set fails, we don't keep on
								//  trying to set that mode
			vid_realmode = vid_modenum;
		}
	}

// handle the mouse state when windowed if that's changed
	if (modestate == MS_WINDOWED)
	{
		if ((int)_windowed_mouse.value != windowed_mouse)
		{
			if (_windowed_mouse.value)
			{
				IN_ActivateMouse ();
				IN_HideMouse ();
			}
			else
			{
				IN_DeactivateMouse ();
				IN_ShowMouse ();
			}

			windowed_mouse = (int)_windowed_mouse.value;
		}
	}
}


/*
================
D_BeginDirectRect, D_EndDirectRect

The loading disc: drawn into the screen's buffer around a file's read and taken out again, what was under it restored
(every mode has one page). The frame is the same afterwards; FlipScreen would show the rectangle.
================
*/
static byte	backingbuf[48*24];

void D_BeginDirectRect (int x, int y, byte *pbitmap, int width, int height)
{
	int		i, j, reps, repshift;

	if (!vid_initialized)
		return;

	if (vid.aspect > 1.5)
	{
		reps = 2;
		repshift = 1;
	}
	else
	{
		reps = 1;
		repshift = 0;
	}

	VID_LockBuffer ();

	if (!vid.direct)
		Sys_Error ("NULL vid.direct pointer");

	for (i=0 ; i<(height << repshift) ; i += reps)
	{
		for (j=0 ; j<reps ; j++)
		{
			memcpy (&backingbuf[(i + j) * 24],
					vid.direct + x + ((y << repshift) + i + j) * vid.rowbytes,
					width);
			memcpy (vid.direct + x + ((y << repshift) + i + j) * vid.rowbytes,
					&pbitmap[(i >> repshift) * width],
					width);
		}
	}

	VID_UnlockBuffer ();
}

void D_EndDirectRect (int x, int y, int width, int height)
{
	int		i, j, reps, repshift;

	if (!vid_initialized)
		return;

	if (vid.aspect > 1.5)
	{
		reps = 2;
		repshift = 1;
	}
	else
	{
		reps = 1;
		repshift = 0;
	}

	VID_LockBuffer ();

	if (!vid.direct)
		Sys_Error ("NULL vid.direct pointer");

	for (i=0 ; i<(height << repshift) ; i += reps)
	{
		for (j=0 ; j<reps ; j++)
		{
			memcpy (vid.direct + x + ((y << repshift) + i + j) * vid.rowbytes,
					&backingbuf[(i + j) * 24],
					width);
		}
	}

	VID_UnlockBuffer ();
}


/*
================
AppActivate

The window keeps the focus: this runs when it is first shown and when a full-screen mode is set. The desktop is not
palettized.
================
*/
void AppActivate(BOOL fActive, BOOL minimize)
{
	static BOOL	sound_active;

	ActiveApp = fActive;

// messy, but it seems to work
	if (vid_fulldib_on_focus_mode)
	{
		Minimized = minimize;

		if (Minimized)
			ActiveApp = false;
	}

	if (vid_initialized)
	{
		if (!Minimized)
			VID_SetPalette (vid_curpal);

		scr_fullupdate = 0;
	}

// enable/disable sound on focus gain/loss
	if (!ActiveApp && sound_active)
	{
		S_BlockSound ();
		S_ClearBuffer ();
		sound_active = false;
	}
	else if (ActiveApp && !sound_active)
	{
		S_UnblockSound ();
		S_ClearBuffer ();
		sound_active = true;
	}

// minimize/restore fulldib windows/mouse-capture normal windows on demand
	if (!in_mode_set)
	{
		if (ActiveApp)
		{
			if ((modestate == MS_WINDOWED) && _windowed_mouse.value)
			{
				IN_ActivateMouse ();
				IN_HideMouse ();
			}
		}

		if (!ActiveApp)
		{
			if ((modestate == MS_WINDOWED) && _windowed_mouse.value)
			{
				IN_DeactivateMouse ();
				IN_ShowMouse ();
			}
		}
	}
}


/*
================
VID_HandlePause
================
*/
void VID_HandlePause (qboolean pause)
{

	if ((modestate == MS_WINDOWED) && _windowed_mouse.value)
	{
		if (pause)
		{
			IN_DeactivateMouse ();
			IN_ShowMouse ();
		}
		else
		{
			IN_ActivateMouse ();
			IN_HideMouse ();
		}
	}
}


/*
===================================================================

THE VIDEO MENU

===================================================================
*/

extern void M_Menu_Options_f (void);
extern void M_Print (int cx, int cy, char *str);
extern void M_PrintWhite (int cx, int cy, char *str);
extern void M_DrawCharacter (int cx, int line, int num);
extern void M_DrawTransPic (int x, int y, qpic_t *pic);
extern void M_DrawPic (int x, int y, qpic_t *pic);

static int	vid_line, vid_wmodes;

typedef struct
{
	int		modenum;
	char	*desc;
	int		iscur;
	int		ismode13;
	int		width;
} modedesc_t;

#define MAX_COLUMN_SIZE		5
#define MODE_AREA_HEIGHT	(MAX_COLUMN_SIZE + 6)
#define MAX_MODEDESCS		(MAX_COLUMN_SIZE*3)

static modedesc_t	modedescs[MAX_MODEDESCS];

/*
================
VID_MenuDraw
================
*/
void VID_MenuDraw (void)
{
	qpic_t		*p;
	char		*ptr;
	int			lnummodes, i, j, k, column, row, dup, dupmode;
	char		temp[100];
	vmode_t		*pv;
	modedesc_t	tmodedesc;

	p = Draw_CachePic ("gfx/vidmodes.lmp");
	M_DrawPic ( (320-p->width)/2, 4, p);

	for (i=0 ; i<3 ; i++)
	{
		ptr = VID_GetModeDescriptionMemCheck (i);
		modedescs[i].modenum = modelist[i].modenum;
		modedescs[i].desc = ptr;
		modedescs[i].ismode13 = 0;
		modedescs[i].iscur = 0;

		if (vid_modenum == i)
			modedescs[i].iscur = 1;
	}

	vid_wmodes = 3;
	lnummodes = VID_NumModes ();

	for (i=3 ; i<lnummodes ; i++)
	{
		ptr = VID_GetModeDescriptionMemCheck (i);
		pv = VID_GetModePtr (i);

	// we only have room for 15 fullscreen modes, so don't allow
	// 360-wide modes, because if there are 5 320-wide modes and
	// 5 360-wide modes, we'll run out of space
		if (ptr && ((pv->width != 360) || COM_CheckParm("-allow360")))
		{
			dup = 0;

			for (j=3 ; j<vid_wmodes ; j++)
			{
				if (!strcmp (modedescs[j].desc, ptr))
				{
					dup = 1;
					dupmode = j;
					break;
				}
			}

			if (dup || (vid_wmodes < MAX_MODEDESCS))
			{
				if (!dup || !modedescs[dupmode].ismode13 || COM_CheckParm("-noforcevga"))
				{
					if (dup)
					{
						k = dupmode;
					}
					else
					{
						k = vid_wmodes;
					}

					modedescs[k].modenum = i;
					modedescs[k].desc = ptr;
					modedescs[k].ismode13 = pv->mode13;
					modedescs[k].iscur = 0;
					modedescs[k].width = pv->width;

					if (i == vid_modenum)
						modedescs[k].iscur = 1;

					if (!dup)
						vid_wmodes++;
				}
			}
		}
	}

// sort the modes on width (to handle picking up oddball dibonly modes
// after all the others)
	for (i=3 ; i<(vid_wmodes-1) ; i++)
	{
		for (j=(i+1) ; j<vid_wmodes ; j++)
		{
			if (modedescs[i].width > modedescs[j].width)
			{
				tmodedesc = modedescs[i];
				modedescs[i] = modedescs[j];
				modedescs[j] = tmodedesc;
			}
		}
	}


	M_Print (13*8, 36, "Windowed Modes");

	column = 16;
	row = 36+2*8;

	for (i=0 ; i<3; i++)
	{
		if (modedescs[i].iscur)
			M_PrintWhite (column, row, modedescs[i].desc);
		else
			M_Print (column, row, modedescs[i].desc);

		column += 13*8;
	}

	if (vid_wmodes > 3)
	{
		M_Print (12*8, 36+4*8, "Fullscreen Modes");

		column = 16;
		row = 36+6*8;

		for (i=3 ; i<vid_wmodes ; i++)
		{
			if (modedescs[i].iscur)
				M_PrintWhite (column, row, modedescs[i].desc);
			else
				M_Print (column, row, modedescs[i].desc);

			column += 13*8;

			if (((i - 3) % VID_ROW_SIZE) == (VID_ROW_SIZE - 1))
			{
				column = 16;
				row += 8;
			}
		}
	}

// line cursor
	if (vid_testingmode)
	{
		sprintf (temp, "TESTING %s",
				modedescs[vid_line].desc);
		M_Print (13*8, 36 + MODE_AREA_HEIGHT * 8 + 8*4, temp);
		M_Print (9*8, 36 + MODE_AREA_HEIGHT * 8 + 8*6,
				"Please wait 5 seconds...");
	}
	else
	{
		M_Print (9*8, 36 + MODE_AREA_HEIGHT * 8 + 8,
				"Press Enter to set mode");
		M_Print (6*8, 36 + MODE_AREA_HEIGHT * 8 + 8*3,
				"T to test mode for 5 seconds");
		ptr = VID_GetModeDescription2 (vid_modenum);

		if (ptr)
		{
			sprintf (temp, "D to set default: %s", ptr);
			M_Print (2*8, 36 + MODE_AREA_HEIGHT * 8 + 8*5, temp);
		}

		ptr = VID_GetModeDescription2 ((int)_vid_default_mode_win.value);

		if (ptr)
		{
			sprintf (temp, "Current default: %s", ptr);
			M_Print (3*8, 36 + MODE_AREA_HEIGHT * 8 + 8*6, temp);
		}

		M_Print (15*8, 36 + MODE_AREA_HEIGHT * 8 + 8*8,
				"Esc to exit");

		row = 36 + 2*8 + (vid_line / VID_ROW_SIZE) * 8;
		column = 8 + (vid_line % VID_ROW_SIZE) * 13*8;

		if (vid_line >= 3)
			row += 3*8;

		M_DrawCharacter (column, row, 12+((int)(realtime*4)&1));
	}
}


/*
================
VID_MenuKey
================
*/
void VID_MenuKey (int key)
{
	if (vid_testingmode)
		return;

	switch (key)
	{
	case K_ESCAPE:
		S_LocalSound ("misc/menu1.wav");
		M_Menu_Options_f ();
		break;

	case K_LEFTARROW:
		S_LocalSound ("misc/menu1.wav");
		vid_line = ((vid_line / VID_ROW_SIZE) * VID_ROW_SIZE) +
				   ((vid_line + 2) % VID_ROW_SIZE);

		if (vid_line >= vid_wmodes)
			vid_line = vid_wmodes - 1;
		break;

	case K_RIGHTARROW:
		S_LocalSound ("misc/menu1.wav");
		vid_line = ((vid_line / VID_ROW_SIZE) * VID_ROW_SIZE) +
				   ((vid_line + 4) % VID_ROW_SIZE);

		if (vid_line >= vid_wmodes)
			vid_line = (vid_line / VID_ROW_SIZE) * VID_ROW_SIZE;
		break;

	case K_UPARROW:
		S_LocalSound ("misc/menu1.wav");
		vid_line -= VID_ROW_SIZE;

		if (vid_line < 0)
		{
			vid_line += ((vid_wmodes + (VID_ROW_SIZE - 1)) /
					VID_ROW_SIZE) * VID_ROW_SIZE;

			while (vid_line >= vid_wmodes)
				vid_line -= VID_ROW_SIZE;
		}
		break;

	case K_DOWNARROW:
		S_LocalSound ("misc/menu1.wav");
		vid_line += VID_ROW_SIZE;

		if (vid_line >= vid_wmodes)
		{
			vid_line -= ((vid_wmodes + (VID_ROW_SIZE - 1)) /
					VID_ROW_SIZE) * VID_ROW_SIZE;

			while (vid_line < 0)
				vid_line += VID_ROW_SIZE;
		}
		break;

	case K_ENTER:
		S_LocalSound ("misc/menu1.wav");
		VID_SetMode (modedescs[vid_line].modenum, vid_curpal);
		break;

	case 'T':
	case 't':
		S_LocalSound ("misc/menu1.wav");
	// have to set this before setting the mode because WM_PAINT
	// happens during the mode set and does a VID_Update, which
	// checks vid_testingmode
		vid_testingmode = 1;
		vid_testendtime = realtime + 5.0;

		if (!VID_SetMode (modedescs[vid_line].modenum, vid_curpal))
		{
			vid_testingmode = 0;
		}
		break;

	case 'D':
	case 'd':
		S_LocalSound ("misc/menu1.wav");
		firstupdate = 0;
		Cvar_SetValue ("_vid_default_mode_win", vid_modenum);
		break;

	default:
		break;
	}
}


/*
===============================================================================

THE TRACES

===============================================================================
*/

// the trace's I line (trace.c): vid_win.c's part of the mouse
int Oracle_WindowedMouse (void)
{
	return windowed_mouse;
}

// the frame, locked or not (the V line and -traceshot): the DIB or the full-screen memory DC, its row length
byte *Oracle_Frame (int *rowbytes)
{
	*rowbytes = dc_rowbytes;
	return dibdc ? dibdc : memdc;
}
