namespace QuakeSharp.Core.Numerics;

// pow.c (musl 1.2.5, from Arm's optimized-routines), without fused multiply-add (__FP_FAST_FMA is not set for the
// oracle's SSE2 target): the "#else" paths. WANT_ROUNDING is 1, TOINT_INTRINSICS and EXP_USE_TOINT_NARROW 0.
public static partial class Libm
{
    private const int POW_LOG_TABLE_BITS = 7;
    private const int EXP_TABLE_BITS = 7;
    private const ulong OFF = 0x3fe6955500000000;
    private const uint SIGN_BIAS = 0x800 << EXP_TABLE_BITS;

    // pow.c: pow
    public static double pow(double x, double y)
    {
        uint sign_bias = 0;
        ulong ix = asuint64(x);
        ulong iy = asuint64(y);
        uint topx = top12(x);
        uint topy = top12(y);
        if (topx - 0x001 >= 0x7ff - 0x001 || (topy & 0x7ff) - 0x3be >= 0x43e - 0x3be)
        {
            // Note: if |y| > 1075 * ln2 * 2^53 ~= 0x1.749p62 then pow(x,y) = inf/0
            // and if |y| < 2^-54 / 1075 ~= 0x1.e7b6p-65 then pow(x,y) = +-1.
            // Special cases: (x < 0x1p-126 or inf or nan) or
            // (|y| < 0x1p-65 or |y| >= 0x1p63 or nan).
            if (zeroinfnan(iy))
            {
                if (2 * iy == 0)
                {
                    return 1.0; // issignaling_inline is 0
                }

                if (ix == asuint64(1.0))
                {
                    return 1.0;
                }

                if (2 * ix > 2 * asuint64(double.PositiveInfinity) || 2 * iy > 2 * asuint64(double.PositiveInfinity))
                {
                    return x + y;
                }

                if (2 * ix == 2 * asuint64(1.0))
                {
                    return 1.0;
                }

                if ((2 * ix < 2 * asuint64(1.0)) == ((iy >> 63) == 0))
                {
                    return 0.0; // |x|<1 && y==inf or |x|>1 && y==-inf.
                }

                return y * y;
            }

            if (zeroinfnan(ix))
            {
                double x2 = x * x;
                if ((ix >> 63) != 0 && checkint(iy) == 1)
                {
                    x2 = -x2;
                }

                return (iy >> 63) != 0 ? 1 / x2 : x2;
            }

            // Here x and y are non-zero finite.
            if ((ix >> 63) != 0)
            {
                // Finite x < 0.
                int yint = checkint(iy);
                if (yint == 0)
                {
                    return __math_invalid(x);
                }

                if (yint == 1)
                {
                    sign_bias = SIGN_BIAS;
                }

                ix &= 0x7fffffffffffffff;
                topx &= 0x7ff;
            }

            if ((topy & 0x7ff) - 0x3be >= 0x43e - 0x3be)
            {
                // Note: sign_bias == 0 here because y is not odd.
                if (ix == asuint64(1.0))
                {
                    return 1.0;
                }

                if ((topy & 0x7ff) < 0x3be)
                {
                    // |y| < 2^-65, x^y ~= 1 + y*log(x).
                    return ix > asuint64(1.0) ? 1.0 + y : 1.0 - y;
                }

                return (ix > asuint64(1.0)) == (topy < 0x800) ? __math_oflow(0) : __math_uflow(0);
            }

            if (topx == 0)
            {
                // Normalize subnormal x so exponent becomes negative.
                ix = asuint64(x * TwoP52);
                ix &= 0x7fffffffffffffff;
                ix -= 52UL << 52;
            }
        }

        double hi = log_inline(ix, out double lo);
        double ehi, elo;
        double yhi = asdouble(iy & (ulong.MaxValue << 27));
        double ylo = y - yhi;
        double lhi = asdouble(asuint64(hi) & (ulong.MaxValue << 27));
        double llo = hi - lhi + lo;
        ehi = yhi * lhi;
        elo = (ylo * lhi) + (y * llo); // |elo| < |ehi| * 2^-25.
        return exp_inline(ehi, elo, sign_bias);
    }

    // pow.c: top12 (the top 12 bits of a double: sign and exponent)
    private static uint top12(double x) => (uint)(asuint64(x) >> 52);

    // pow.c: log_inline
    // Compute y+TAIL = log(x) where the rounded result is y and TAIL has about additional 15 bits precision. IX is the
    // bit representation of x, but normalized in the subnormal range using the sign bit for the exponent.
    private static double log_inline(ulong ix, out double tail)
    {
        const int N = 1 << POW_LOG_TABLE_BITS;
        double[] A = __pow_log_data.poly;
        const double Ln2hi = __pow_log_data.ln2hi;
        const double Ln2lo = __pow_log_data.ln2lo;

        double z, r, y, invc, logc, logctail, kd, hi, t1, t2, lo, lo1, lo2, p;
        ulong iz, tmp;
        int k, i;

        // x = 2^k z; where z is in range [OFF,2*OFF) and exact.
        // The range is split into N subintervals.
        // The ith subinterval contains z and c is near its center.
        tmp = ix - OFF;
        i = (int)((tmp >> (52 - POW_LOG_TABLE_BITS)) % N);
        k = (int)((long)tmp >> 52); // arithmetic shift
        iz = ix - (tmp & (0xfffUL << 52));
        z = asdouble(iz);
        kd = k;

        // log(x) = k*Ln2 + log(c) + log1p(z/c-1).
        invc = __pow_log_data.invc[i];
        logc = __pow_log_data.logc[i];
        logctail = __pow_log_data.logctail[i];

        // Note: 1/c is j/N or j/N/2 where j is an integer in [N,2N) and
        // |z/c - 1| < 1/N, so r = z/c - 1 is exactly representible.
        // Split z such that rhi, rlo and rhi*rhi are exact and |rlo| <= |r|.
        double zhi = asdouble((iz + (1UL << 31)) & (ulong.MaxValue << 32));
        double zlo = z - zhi;
        double rhi = (zhi * invc) - 1.0;
        double rlo = zlo * invc;
        r = rhi + rlo;

        // k*Ln2 + log(c) + r.
        t1 = (kd * Ln2hi) + logc;
        t2 = t1 + r;
        lo1 = (kd * Ln2lo) + logctail;
        lo2 = t1 - t2 + r;

        // Evaluation is optimized assuming superscalar pipelined execution.
        double ar, ar2, ar3, lo3, lo4;
        ar = A[0] * r; // A[0] = -0.5.
        ar2 = r * ar;
        ar3 = r * ar2;

        // k*Ln2 + log(c) + r + A[0]*r*r.
        double arhi = A[0] * rhi;
        double arhi2 = rhi * arhi;
        hi = t2 + arhi2;
        lo3 = rlo * (ar + arhi);
        lo4 = t2 - hi + arhi2;

        // p = log1p(r) - r - A[0]*r*r.
        p = ar3 * (A[1] + (r * A[2]) + (ar2 * (A[3] + (r * A[4]) + (ar2 * (A[5] + (r * A[6]))))));
        lo = lo1 + lo2 + lo3 + lo4 + p;
        y = hi + lo;
        tail = hi - y + lo;
        return y;
    }

    // pow.c: specialcase
    // Handle cases that may overflow or underflow when computing the result that is scale*(1+TMP) without
    // intermediate rounding. The bit representation of scale is in SBITS, however it has a computed exponent that
    // may have overflown into the sign bit so that needs to be adjusted before using it as a double. (int32_t)KI is
    // the k used in the argument reduction and exponent adjustment of scale, positive k here means the result may
    // overflow and negative k means the result may underflow.
    private static double specialcase(double tmp, ulong sbits, ulong ki)
    {
        double scale, y;

        if ((ki & 0x80000000) == 0)
        {
            // k > 0, the exponent of scale might have overflowed by <= 460.
            sbits -= 1009UL << 52;
            scale = asdouble(sbits);
            y = TwoP1009 * (scale + (scale * tmp));
            return y;
        }

        // k < 0, need special care in the subnormal range.
        sbits += 1022UL << 52;

        // Note: sbits is signed scale.
        scale = asdouble(sbits);
        y = scale + (scale * tmp);
        if (Math.Abs(y) < 1.0)
        {
            // Round y to the right precision before scaling it into the subnormal range to avoid double rounding that
            // can cause 0.5+E/2 ulp error where E is the worst-case ulp error outside the subnormal range. So this is
            // only useful if the goal is better than 1 ulp worst-case error.
            double hi, lo, one = 1.0;
            if (y < 0.0)
            {
                one = -1.0;
            }

            lo = scale - y + (scale * tmp);
            hi = one + y;
            lo = one - hi + y + lo;
            y = hi + lo - one;

            // Fix the sign of 0.
            if (y == 0.0)
            {
                y = asdouble(sbits & 0x8000000000000000);
            }
        }

        y = TwoM1022 * y;
        return y;
    }

    // pow.c: exp_inline
    // Computes sign*exp(x+xtail) where |xtail| < 2^-8/N and |xtail| <= |x|. The sign_bias argument is SIGN_BIAS or 0
    // and sets the sign to -1 or 1.
    private static double exp_inline(double x, double xtail, uint sign_bias)
    {
        const int N = 1 << EXP_TABLE_BITS;
        const double InvLn2N = __exp_data.invln2N;
        const double NegLn2hiN = __exp_data.negln2hiN;
        const double NegLn2loN = __exp_data.negln2loN;
        const double Shift = __exp_data.shift;
        ulong[] T = __exp_data.tab;
        double C2 = __exp_data.poly[0]; // poly[5 - EXP_POLY_ORDER]
        double C3 = __exp_data.poly[1];
        double C4 = __exp_data.poly[2];
        double C5 = __exp_data.poly[3];

        uint abstop;
        ulong ki, idx, top, sbits;
        double kd, z, r, r2, scale, tail, tmp;

        abstop = top12(x) & 0x7ff;
        if (abstop - top12(TwoM54) >= top12(512.0) - top12(TwoM54))
        {
            if (abstop - top12(TwoM54) >= 0x80000000)
            {
                // Avoid spurious underflow for tiny x.
                // Note: 0 is common input.
                double one = 1.0 + x; // WANT_ROUNDING
                return sign_bias != 0 ? -one : one;
            }

            if (abstop >= top12(1024.0))
            {
                // Note: inf and nan are already handled.
                if ((asuint64(x) >> 63) != 0)
                {
                    return __math_uflow(sign_bias);
                }
                else
                {
                    return __math_oflow(sign_bias);
                }
            }

            // Large x is special cased below.
            abstop = 0;
        }

        // exp(x) = 2^(k/N) * exp(r), with exp(r) in [2^(-1/2N),2^(1/2N)].
        // x = ln2/N*k + r, with int k and r in [-ln2/2N, ln2/2N].
        z = InvLn2N * x;

        // z - kd is in [-1, 1] in non-nearest rounding modes.
        kd = z + Shift;
        ki = asuint64(kd);
        kd -= Shift;
        r = x + (kd * NegLn2hiN) + (kd * NegLn2loN);

        // The code assumes 2^-200 < |xtail| < 2^-8/N.
        r += xtail;

        // 2^(k/N) ~= scale * (1 + tail).
        idx = 2 * (ki % N);
        top = (ki + sign_bias) << (52 - EXP_TABLE_BITS);
        tail = asdouble(T[idx]);

        // This is only a valid scale when -1023*N < k < 1024*N.
        sbits = T[idx + 1] + top;

        // exp(x) = 2^(k/N) * exp(r) ~= scale + scale * (tail + exp(r) - 1).
        // Evaluation is optimized assuming superscalar pipelined execution.
        r2 = r * r;

        // Without fma the worst case error is 0.25/N ulp larger.
        // Worst case error is less than 0.5+1.11/N+(abs poly error * 2^53) ulp.
        tmp = tail + r + (r2 * (C2 + (r * C3))) + (r2 * r2 * (C4 + (r * C5)));
        if (abstop == 0)
        {
            return specialcase(tmp, sbits, ki);
        }

        scale = asdouble(sbits);

        // Note: tmp == 0 or |tmp| > 2^-200 and scale > 2^-739, so there
        // is no spurious underflow here even without fma.
        return scale + (scale * tmp);
    }

    // pow.c: checkint
    // Returns 0 if not int, 1 if odd int, 2 if even int. The argument is the bit representation of a non-zero finite
    // floating-point value.
    private static int checkint(ulong iy)
    {
        int e = (int)((iy >> 52) & 0x7ff);
        if (e < 0x3ff)
        {
            return 0;
        }

        if (e > 0x3ff + 52)
        {
            return 2;
        }

        if ((iy & ((1UL << (0x3ff + 52 - e)) - 1)) != 0)
        {
            return 0;
        }

        if ((iy & (1UL << (0x3ff + 52 - e))) != 0)
        {
            return 1;
        }

        return 2;
    }

    // pow.c: zeroinfnan
    // Returns 1 if input is the bit representation of 0, infinity or nan.
    private static bool zeroinfnan(ulong i) => (2 * i) - 1 >= (2 * asuint64(double.PositiveInfinity)) - 1;

    // __math_xflow.c, __math_oflow.c, __math_uflow.c, __math_invalid.c
    private static double __math_xflow(uint sign, double y) => (sign != 0 ? -y : y) * y;

    private static double __math_oflow(uint sign) => __math_xflow(sign, TwoP769);

    private static double __math_uflow(uint sign) => __math_xflow(sign, TwoM767);

    private static double __math_invalid(double x) => (x - x) / (x - x);

    private static readonly double TwoP52 = Math.ScaleB(1.0, 52);
    private static readonly double TwoM54 = Math.ScaleB(1.0, -54);
    private static readonly double TwoP769 = Math.ScaleB(1.0, 769);
    private static readonly double TwoM767 = Math.ScaleB(1.0, -767);
    private static readonly double TwoP1009 = Math.ScaleB(1.0, 1009);
}
