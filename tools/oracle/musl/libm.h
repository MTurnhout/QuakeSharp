/*
 * Stands in for musl's src/internal/libm.h when musl's libm sources (reference/musl/src/math) are compiled into the
 * oracle with clang-cl for 32-bit Windows: the macros and declarations the ported functions use, from musl 1.2.5's
 * libm.h, without its long double and floating-point-exception machinery. long double is double on this target.
 *
 * Copyright (c) 2005-2020 Rich Felker, et al. (musl, MIT license: see reference/musl/COPYRIGHT)
 */
#ifndef _LIBM_H
#define _LIBM_H

#include "musl_names.h"
#include <stdint.h>
#include <float.h>
#include <math.h>

#define hidden

/* Support non-nearest rounding mode.  */
#define WANT_ROUNDING 1
#define issignalingf_inline(x) 0
#define issignaling_inline(x) 0
#define TOINT_INTRINSICS 0

#define predict_true(x) (x)
#define predict_false(x) (x)

static inline double eval_as_double(double x)
{
	double y = x;
	return y;
}

static inline double fp_barrier(double x)
{
	volatile double y = x;
	return y;
}

static inline void fp_force_eval(double x)
{
	volatile double y;
	y = x;
}

static inline void fp_force_evalf(float x)
{
	volatile float y;
	y = x;
}

#define FORCE_EVAL(x) do {                        \
	if (sizeof(x) == sizeof(float)) {         \
		fp_force_evalf(x);                \
	} else {                                  \
		fp_force_eval(x);                 \
	}                                         \
} while(0)

#define asuint64(f) ((union{double _f; uint64_t _i;}){f})._i
#define asdouble(i) ((union{uint64_t _i; double _f;}){i})._f

#define EXTRACT_WORDS(hi,lo,d)                    \
do {                                              \
  uint64_t __u = asuint64(d);                     \
  (hi) = __u >> 32;                               \
  (lo) = (uint32_t)__u;                           \
} while (0)

#define GET_HIGH_WORD(hi,d)                       \
do {                                              \
  (hi) = asuint64(d) >> 32;                       \
} while (0)

#define GET_LOW_WORD(lo,d)                        \
do {                                              \
  (lo) = (uint32_t)asuint64(d);                   \
} while (0)

#define INSERT_WORDS(d,hi,lo)                     \
do {                                              \
  (d) = asdouble(((uint64_t)(hi)<<32) | (uint32_t)(lo)); \
} while (0)

#define SET_LOW_WORD(d,lo)                        \
  INSERT_WORDS(d, asuint64(d)>>32, lo)

hidden int    __rem_pio2_large(double*,double*,int,int,int);
hidden int    __rem_pio2(double,double*);
hidden double __sin(double,double,int);
hidden double __cos(double,double);
hidden double __tan(double,double,int);

hidden double __math_xflow(uint32_t, double);
hidden double __math_uflow(uint32_t);
hidden double __math_oflow(uint32_t);
hidden double __math_invalid(double);

double scalbn(double, int);

#endif
