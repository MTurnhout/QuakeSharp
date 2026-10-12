/*
 * The oracle's C library functions that QuakeSharp ports from musl (design.md §3.1) get a musl_ prefix, in musl's
 * sources and in Quake's (this header is force-included into both), so they never clash with the C runtime's own.
 */
#ifndef MUSL_NAMES_H
#define MUSL_NAMES_H

#define sin musl_sin
#define cos musl_cos
#define tan musl_tan
#define atan musl_atan
#define atan2 musl_atan2
#define pow musl_pow
#define scalbn musl_scalbn

#define __sin musl___sin
#define __cos musl___cos
#define __tan musl___tan
#define __rem_pio2 musl___rem_pio2
#define __rem_pio2_large musl___rem_pio2_large
#define __math_xflow musl___math_xflow
#define __math_uflow musl___math_uflow
#define __math_oflow musl___math_oflow
#define __math_invalid musl___math_invalid
#define __pow_log_data musl___pow_log_data
#define __exp_data musl___exp_data

#endif
