/*
 * cd_oracle.c: the oracle's CD drive. WinQuake's cd_win.c is compiled unchanged; the MCI commands it sends come here
 * instead of to winmm, and a virtual drive answers them, holding Quake's disc: track 1 data, tracks 2-11 audio
 * (design.md §7.2, decision 37). The oracle has no window procedure, so no MCI notification ever arrives: as on this
 * disc, no track ends. QuakeSharp's VirtualCdDrive answers the same commands the same way.
 */
#include <windows.h>

// winquake.h: the window cd_win.c asks MCI to notify
HWND mainwindow;

#define ORACLE_CD_DEVICE	1
#define ORACLE_CD_TRACKS	11
#define ORACLE_CD_LENGTH	MCI_MAKE_MSF(4, 0, 0)	// every track's length; nothing plays to its end

#define CD_STOPPED	0
#define CD_PLAYING	1
#define CD_PAUSED	2

static int cd_open;			// MCI_OPEN done, MCI_CLOSE not
static int cd_door_open;
static int cd_mode;			// CD_STOPPED, CD_PLAYING, CD_PAUSED
static int cd_track;		// the track playing, paused or stopped in (0 before any)

static MCIERROR Status (DWORD flags, MCI_STATUS_PARMS *parms)
{
	switch (parms->dwItem)
	{
	case MCI_STATUS_READY:
		parms->dwReturn = !cd_door_open;
		return 0;

	case MCI_STATUS_NUMBER_OF_TRACKS:
		if (cd_door_open)
			return MCIERR_HARDWARE;
		parms->dwReturn = ORACLE_CD_TRACKS;
		return 0;

	case MCI_CDA_STATUS_TYPE_TRACK:
	case MCI_STATUS_LENGTH:
		if (cd_door_open)
			return MCIERR_HARDWARE;
		if (!(flags & MCI_TRACK) || parms->dwTrack < 1 || parms->dwTrack > ORACLE_CD_TRACKS)
			return MCIERR_OUTOFRANGE;
		if (parms->dwItem == MCI_STATUS_LENGTH)
			parms->dwReturn = ORACLE_CD_LENGTH;
		else
			parms->dwReturn = parms->dwTrack == 1 ? MCI_CDA_TRACK_OTHER : MCI_CDA_TRACK_AUDIO;
		return 0;
	}
	return MCIERR_UNSUPPORTED_FUNCTION;
}

// mmsystem.h: mciSendCommand, the commands cd_win.c sends, each answered with MCI's error code (0 for success)
MCIERROR WINAPI mciSendCommandA (MCIDEVICEID id, UINT msg, DWORD_PTR flags, DWORD_PTR param)
{
	if (msg == MCI_OPEN)
	{
		((MCI_OPEN_PARMS *)param)->wDeviceID = ORACLE_CD_DEVICE;
		cd_open = 1;
		return 0;
	}
	if (id != ORACLE_CD_DEVICE || !cd_open)
		return MCIERR_INVALID_DEVICE_ID;

	switch (msg)
	{
	case MCI_SET:
		if (flags & MCI_SET_DOOR_OPEN)
		{
			cd_door_open = 1;
			cd_mode = CD_STOPPED;
		}
		if (flags & MCI_SET_DOOR_CLOSED)
			cd_door_open = 0;
		return 0;

	case MCI_STATUS:
		return Status ((DWORD)flags, (MCI_STATUS_PARMS *)param);

	case MCI_PLAY:
		if (cd_door_open)
			return MCIERR_HARDWARE;
		if (flags & MCI_FROM)
			cd_track = MCI_TMSF_TRACK (((MCI_PLAY_PARMS *)param)->dwFrom);
		cd_mode = CD_PLAYING;
		return 0;

	case MCI_STOP:
		cd_mode = CD_STOPPED;
		return 0;

	case MCI_PAUSE:
		if (cd_mode == CD_PLAYING)
			cd_mode = CD_PAUSED;
		return 0;

	case MCI_CLOSE:
		cd_open = 0;
		cd_mode = CD_STOPPED;
		return 0;
	}
	return MCIERR_UNSUPPORTED_FUNCTION;
}

// the trace's A line (trace.c): the drive's state as ints (open, door open, mode, track); returns how many
int Oracle_CDDrive (int *out)
{
	out[0] = cd_open;
	out[1] = cd_door_open;
	out[2] = cd_mode;
	out[3] = cd_track;
	return 4;
}
