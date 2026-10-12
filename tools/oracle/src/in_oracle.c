/*
 * in_oracle.c: what in_null.c leaves out of in_win.c's interface. The oracle has no mouse; scripted input comes
 * through -traceevents (trace.c).
 */
#include "quakedef.h"

// in_win.c: IN_Accumulate, called by snd_dma.c's S_ExtraUpdate to keep the mouse moving during long loads
void IN_Accumulate (void)
{
}

// in_win.c: IN_ClearStates, which clears the mouse's accumulated movement and buttons
void IN_ClearStates (void)
{
}
