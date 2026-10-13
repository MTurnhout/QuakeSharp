/*
 * Force-included into every Quake source file of the oracle (after musl_names.h).
 *
 * rand() and srand() are the Microsoft C runtime's generator, implemented here (sys_oracle.c) so the traces can
 * show its state; the sequence is the C runtime's (tools/oracle/numerics.c checks it).
 */
#ifndef ORACLE_H
#define ORACLE_H

#include <stdlib.h>

#define rand oracle_rand
#define srand oracle_srand

int oracle_rand (void);
void oracle_srand (unsigned int seed);

/*
 * host.c prints "Exe: "__TIME__" "__DATE__ on the console, which shows in the frames: the stamp of Winquake.exe 1.09
 * (as its binary holds it), not the oracle's own build time.
 */
#undef __TIME__
#define __TIME__ "09:30:49"
#undef __DATE__
#define __DATE__ "Mar 21 1997"

/*
 * The virtual machine's screen (vid_oracle.c's desktop, in_oracle.c's cursor): as the machine Winquake.exe was audited
 * on (design.md §3.4).
 */
#define ORACLE_SCREEN_WIDTH		1920
#define ORACLE_SCREEN_HEIGHT	1080

/*
 * sys_oracle.c: how many arguments from argv[j] are a switch of the harness's with its values (-trace..., -oracle...,
 * -netunplug), 0 for one of the game's: common.c leaves them out of the cmdline cvar (quake-oracle.patch), as
 * QuakeSharp's CommandLine does.
 */
int Oracle_HarnessSwitch (char **argv, int j, int argc);

#endif
