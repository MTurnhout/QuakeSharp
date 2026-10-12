/*
 * snd_oracle.c: the oracle's sound device, standing in for snd_win.c's DirectSound buffer (16-bit stereo at 11025 Hz,
 * 64 KB). Its play position follows the virtual clock, so the mixer paints the same samples at the same frames in
 * every run: which matters to the game, because S_StartSound draws from rand() when an identical sound has not yet
 * been painted (design.md §7.1).
 */
#include "quakedef.h"
#include "winquake.h"
#include "trace.h"

extern double oracle_time;

// winquake.h
LPDIRECTSOUND pDS;
LPDIRECTSOUNDBUFFER pDSBuf;
DWORD gSndBufSize;

#define SECONDARY_BUFFER_SIZE	0x10000

static byte	dma_buffer[SECONDARY_BUFFER_SIZE];

qboolean SNDDMA_Init (void)
{
	shm = &sn;
	shm->channels = 2;
	shm->samplebits = 16;
	shm->speed = 11025;
	shm->soundalive = true;
	shm->splitbuffer = false;
	gSndBufSize = SECONDARY_BUFFER_SIZE;
	shm->samples = gSndBufSize / (shm->samplebits / 8);
	shm->samplepos = 0;
	shm->submission_chunk = 1;
	shm->buffer = dma_buffer;
	memset (dma_buffer, 0, sizeof(dma_buffer));
	return true;
}

// the current position in mono samples, wrapping in the buffer: one sample frame per 1/speed s of virtual time
int SNDDMA_GetDMAPos (void)
{
	int		s;

	s = (int)(oracle_time * shm->speed) * shm->channels;
	s &= (shm->samples - 1);
	return s;
}

void SNDDMA_Submit (void)
{
}

void SNDDMA_Shutdown (void)
{
}

void S_BlockSound (void)
{
}

void S_UnblockSound (void)
{
}
