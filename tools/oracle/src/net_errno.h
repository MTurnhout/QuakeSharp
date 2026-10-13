/*
 * Force-included into net_win.c, net_wins.c and net_wipx.c (Build-Oracle.ps1). Their reads keep Winsock's error in a
 * local named errno, as the single-threaded C runtime of 1996 allowed; today's runtime defines errno as a macro, which
 * its headers' inline functions use. So the headers those files include come first (each is #pragma once), and the
 * macro is taken away before their code.
 */
#include <math.h>
#include <string.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <setjmp.h>
#include <windows.h>
#include <ddraw.h>
#include <dsound.h>
#include <wsipx.h>
#include <errno.h>
#undef errno
