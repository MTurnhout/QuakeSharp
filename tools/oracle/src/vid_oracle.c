/*
 * vid_oracle.c: the oracle's video driver, standing in for vid_win.c with one windowed DIB mode. The frame stays in
 * memory, rendered at the size -oraclevid <width> <height> asks for (320x240, WinQuake's default windowed mode, by
 * default). What vid_win.c does that the game shows is kept: its cvars and commands (they go into config.cfg), the
 * remapping of colormap entries 0 to another black (GDI cannot remap palette index 0), S_Init between the two mode
 * sets of VID_Init, the pixel aspect, the surface cache, the mode description on the console and ClearAllStates.
 */
#include "quakedef.h"
#include "d_local.h"
#include "winquake.h"
#include "trace.h"

viddef_t	vid;				// global video state

unsigned short	d_8to16table[256];
unsigned	d_8to24table[256];

byte		vid_curpal[256*3];

void (*vid_menudrawfn)(void);
void (*vid_menukeyfn)(int key);

// winquake.h: what the shared code asks of the Windows video driver
modestate_t	modestate = MS_UNINIT;
qboolean	DDActive;
qboolean	ActiveApp = true, Minimized;
int			window_center_x, window_center_y;
qboolean	mouseinitialized;

// vid_win.c's cvars
cvar_t		vid_mode = {"vid_mode","0", false};
cvar_t		_vid_default_mode = {"_vid_default_mode","0", true};
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

static int		mode_width = 320, mode_height = 240;
static qboolean	hide_window;
static byte		*vid_buffer;
static short	*zbuffer;
static byte		*surfcache;

void	VID_SetPalette (unsigned char *palette)
{
	memcpy (vid_curpal, palette, sizeof(vid_curpal));
	Trace_Palette (vid_curpal);
}

void	VID_ShiftPalette (unsigned char *palette)
{
	VID_SetPalette (palette);
}

// vid_win.c: VID_MenuDraw: the list of modes (one mode here: nothing to list)
static void VID_MenuDraw (void)
{
}

void M_Menu_Options_f (void);

// vid_win.c: VID_MenuKey: Escape goes back to the options (the other keys choose among the modes: one here)
static void VID_MenuKey (int key)
{
	switch (key)
	{
	case K_ESCAPE:
		S_LocalSound ("misc/menu1.wav");
		M_Menu_Options_f ();
		break;
	}
}

// vid_win.c's mode commands, which have nothing to do here
static void VID_NoMode_f (void)
{
}

// vid_win.c: ClearAllStates
// send an up event for each key, to make sure the server clears them all
static void ClearAllStates (void)
{
	int		i;

	for (i=0 ; i<256 ; i++)
	{
		Key_Event (i, false);
	}

	Key_ClearStates ();
	IN_ClearStates ();
}

// vid_win.c: VID_SetMode with VID_SetWindowedMode (the vid fields) and VID_AllocBuffers. There is one mode.
int VID_SetMode (int modenum, unsigned char *palette)
{
	int		temp, surfcachesize;

	// so Con_Printfs don't mess us up by forcing vid and snd updates
	temp = scr_disabled_for_loading;
	scr_disabled_for_loading = true;

	CDAudio_Pause ();
	S_ClearBuffer ();

	modestate = MS_WINDOWED;
	vid.numpages = 1;
	vid.maxwarpwidth = WARP_WIDTH;
	vid.maxwarpheight = WARP_HEIGHT;
	vid.height = vid.conheight = mode_height;
	vid.width = vid.conwidth = mode_width;
	vid.aspect = ((float)vid.height / (float)vid.width) *
				(320.0 / 240.0);

	CDAudio_Resume ();
	scr_disabled_for_loading = temp;

	if (hide_window)
		return true;

	VID_SetPalette (palette);
	Cvar_SetValue ("vid_mode", 0);

	if (d_pzbuffer)
		D_FlushCaches ();
	free (vid_buffer);
	free (zbuffer);
	free (surfcache);
	vid_buffer = calloc (mode_width * mode_height, 1);
	vid.buffer = vid.conbuffer = vid.direct = vid_buffer;
	vid.rowbytes = vid.conrowbytes = mode_width;
	zbuffer = calloc (mode_width * mode_height, sizeof(*zbuffer));
	d_pzbuffer = zbuffer;
	surfcachesize = D_SurfaceCacheForRes (mode_width, mode_height);
	surfcache = calloc (surfcachesize, 1);

	D_InitCaches (surfcache, surfcachesize);

	ClearAllStates ();

	if (!msg_suppress_1)
		Con_SafePrintf ("%dx%d\n", mode_width, mode_height);

	VID_SetPalette (palette);
	vid.recalc_refdef = 1;
	return true;
}

// vid_win.c: VID_Init
void	VID_Init (unsigned char *palette)
{
	int		i, t, bestmatch, bestmatchmetric, dr, dg, db;
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

	Cmd_AddCommand ("vid_testmode", VID_NoMode_f);
	Cmd_AddCommand ("vid_nummodes", VID_NoMode_f);
	Cmd_AddCommand ("vid_describecurrentmode", VID_NoMode_f);
	Cmd_AddCommand ("vid_describemode", VID_NoMode_f);
	Cmd_AddCommand ("vid_describemodes", VID_NoMode_f);
	Cmd_AddCommand ("vid_forcemode", VID_NoMode_f);
	Cmd_AddCommand ("vid_windowed", VID_NoMode_f);
	Cmd_AddCommand ("vid_fullscreen", VID_NoMode_f);
	Cmd_AddCommand ("vid_minimize", VID_NoMode_f);

	t = COM_CheckParm ("-oraclevid");
	if (t && t < com_argc - 2)
	{
		mode_width = Q_atoi (com_argv[t + 1]);
		mode_height = Q_atoi (com_argv[t + 2]);
		if (mode_width < 320 || mode_height < 200 || mode_width > MAXWIDTH || mode_height > MAXHEIGHT)
			Sys_Error ("-oraclevid: %dx%d is not between 320x200 and %dx%d", mode_width, mode_height, MAXWIDTH, MAXHEIGHT);
	}

	vid.maxwarpwidth = WARP_WIDTH;
	vid.maxwarpheight = WARP_HEIGHT;
	vid.colormap = host_colormap;
	vid.fullbright = 256 - LittleLong (*((int *)vid.colormap + 2048));

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

// sound initialization has to go here, preceded by a windowed mode set
	hide_window = true;
	VID_SetMode (0, palette);
	hide_window = false;
	S_Init ();

	VID_SetMode (0, palette);

	VID_SetPalette (palette);

	vid_menudrawfn = VID_MenuDraw;
	vid_menukeyfn = VID_MenuKey;
}

void	VID_Shutdown (void)
{
}

void	VID_Update (vrect_t *rects)
{
}

void VID_LockBuffer (void)
{
}

void VID_UnlockBuffer (void)
{
}

int VID_ForceUnlockedAndReturnState (void)
{
	return 0;
}

void VID_ForceLockState (int lk)
{
}

void VID_HandlePause (qboolean pause)
{
}

void VID_SetDefaultMode (void)
{
}

/*
================
D_BeginDirectRect, D_EndDirectRect

vid_win.c draws the loading disc straight to the screen and restores what was under it: never in the frame.
================
*/
void D_BeginDirectRect (int x, int y, byte *pbitmap, int width, int height)
{
}

void D_EndDirectRect (int x, int y, int width, int height)
{
}
