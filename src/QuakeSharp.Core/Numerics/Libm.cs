namespace QuakeSharp.Core.Numerics;

/// <summary>
/// The transcendental functions of the C library Quake calls, ported from musl 1.2.5 (MIT, reference/musl), so the
/// engine gets the same results on every platform and as the oracle, which is linked to the same C (design.md §3.1).
/// The names are the C library's. Every operation is IEEE double precision (musl's FLT_EVAL_METHOD 0 paths, no fused
/// multiply-add), in the C's order; the constants are generated from the C (Libm.g.cs). Floating-point exceptions
/// are not raised: the FORCE_EVAL lines that only raise them are left out.
/// </summary>
public static partial class Libm
{
    // sin.c: sin
    public static double sin(double x)
    {
        Span<double> y = stackalloc double[2];

        // High word of x.
        uint ix = GET_HIGH_WORD(x);
        ix &= 0x7fffffff;

        // |x| ~< pi/4
        if (ix <= 0x3fe921fb)
        {
            if (ix < 0x3e500000)
            {
                // |x| < 2**-26
                return x;
            }

            return __sin(x, 0.0, 0);
        }

        // sin(Inf or NaN) is NaN
        if (ix >= 0x7ff00000)
        {
            return x - x;
        }

        // argument reduction needed
        uint n = (uint)__rem_pio2(x, y);
        switch (n & 3)
        {
            case 0: return __sin(y[0], y[1], 1);
            case 1: return __cos(y[0], y[1]);
            case 2: return -__sin(y[0], y[1], 1);
            default: return -__cos(y[0], y[1]);
        }
    }

    // cos.c: cos
    public static double cos(double x)
    {
        Span<double> y = stackalloc double[2];

        uint ix = GET_HIGH_WORD(x);
        ix &= 0x7fffffff;

        // |x| ~< pi/4
        if (ix <= 0x3fe921fb)
        {
            if (ix < 0x3e46a09e)
            {
                // |x| < 2**-27 * sqrt(2)
                return 1.0;
            }

            return __cos(x, 0);
        }

        // cos(Inf or NaN) is NaN
        if (ix >= 0x7ff00000)
        {
            return x - x;
        }

        // argument reduction
        uint n = (uint)__rem_pio2(x, y);
        switch (n & 3)
        {
            case 0: return __cos(y[0], y[1]);
            case 1: return -__sin(y[0], y[1], 1);
            case 2: return -__cos(y[0], y[1]);
            default: return __sin(y[0], y[1], 1);
        }
    }

    // tan.c: tan
    public static double tan(double x)
    {
        Span<double> y = stackalloc double[2];

        uint ix = GET_HIGH_WORD(x);
        ix &= 0x7fffffff;

        // |x| ~< pi/4
        if (ix <= 0x3fe921fb)
        {
            if (ix < 0x3e400000)
            {
                // |x| < 2**-27
                return x;
            }

            return __tan(x, 0.0, 0);
        }

        // tan(Inf or NaN) is NaN
        if (ix >= 0x7ff00000)
        {
            return x - x;
        }

        // argument reduction
        uint n = (uint)__rem_pio2(x, y);
        return __tan(y[0], y[1], (int)(n & 1));
    }

    // atan.c: atan
    public static double atan(double x)
    {
        double w, s1, s2, z;
        int id;

        uint ix = GET_HIGH_WORD(x);
        uint sign = ix >> 31;
        ix &= 0x7fffffff;
        if (ix >= 0x44100000)
        {
            // if |x| >= 2^66
            if (double.IsNaN(x))
            {
                return x;
            }

            z = atan_c.atanhi[3] + TwoM120;
            return sign != 0 ? -z : z;
        }

        if (ix < 0x3fdc0000)
        {
            // |x| < 0.4375
            if (ix < 0x3e400000)
            {
                // |x| < 2^-27
                return x;
            }

            id = -1;
        }
        else
        {
            x = Math.Abs(x);
            if (ix < 0x3ff30000)
            {
                // |x| < 1.1875
                if (ix < 0x3fe60000)
                {
                    // 7/16 <= |x| < 11/16
                    id = 0;
                    x = ((2.0 * x) - 1.0) / (2.0 + x);
                }
                else
                {
                    // 11/16 <= |x| < 19/16
                    id = 1;
                    x = (x - 1.0) / (x + 1.0);
                }
            }
            else
            {
                if (ix < 0x40038000)
                {
                    // |x| < 2.4375
                    id = 2;
                    x = (x - 1.5) / (1.0 + (1.5 * x));
                }
                else
                {
                    // 2.4375 <= |x| < 2^66
                    id = 3;
                    x = -1.0 / x;
                }
            }
        }

        // end of argument reduction
        double[] aT = atan_c.aT;
        z = x * x;
        w = z * z;

        // break sum from i=0 to 10 aT[i]z**(i+1) into odd and even poly
        s1 = z * (aT[0] + (w * (aT[2] + (w * (aT[4] + (w * (aT[6] + (w * (aT[8] + (w * aT[10]))))))))));
        s2 = w * (aT[1] + (w * (aT[3] + (w * (aT[5] + (w * (aT[7] + (w * aT[9]))))))));
        if (id < 0)
        {
            return x - (x * (s1 + s2));
        }

        z = atan_c.atanhi[id] - (((x * (s1 + s2)) - atan_c.atanlo[id]) - x);
        return sign != 0 ? -z : z;
    }

    // atan2.c: atan2
    public static double atan2(double y, double x)
    {
        const double pi = atan2_c.pi;
        const double pi_lo = atan2_c.pi_lo;
        double z;

        if (double.IsNaN(x) || double.IsNaN(y))
        {
            return x + y;
        }

        (uint ix, uint lx) = EXTRACT_WORDS(x);
        (uint iy, uint ly) = EXTRACT_WORDS(y);
        if (((ix - 0x3ff00000) | lx) == 0)
        {
            // x = 1.0
            return atan(y);
        }

        uint m = ((iy >> 31) & 1) | ((ix >> 30) & 2); // 2*sign(x)+sign(y)
        ix &= 0x7fffffff;
        iy &= 0x7fffffff;

        // when y = 0
        if ((iy | ly) == 0)
        {
            switch (m)
            {
                case 0:
                case 1: return y; // atan(+-0,+anything)=+-0
                case 2: return pi; // atan(+0,-anything) = pi
                case 3: return -pi; // atan(-0,-anything) =-pi
            }
        }

        // when x = 0
        if ((ix | lx) == 0)
        {
            return (m & 1) != 0 ? -pi / 2 : pi / 2;
        }

        // when x is INF
        if (ix == 0x7ff00000)
        {
            if (iy == 0x7ff00000)
            {
                switch (m)
                {
                    case 0: return pi / 4; // atan(+INF,+INF)
                    case 1: return -pi / 4; // atan(-INF,+INF)
                    case 2: return 3 * pi / 4; // atan(+INF,-INF)
                    case 3: return -3 * pi / 4; // atan(-INF,-INF)
                }
            }
            else
            {
                switch (m)
                {
                    case 0: return 0.0; // atan(+...,+INF)
                    case 1: return -0.0; // atan(-...,+INF)
                    case 2: return pi; // atan(+...,-INF)
                    case 3: return -pi; // atan(-...,-INF)
                }
            }
        }

        // |y/x| > 0x1p64
        if (ix + (64 << 20) < iy || iy == 0x7ff00000)
        {
            return (m & 1) != 0 ? -pi / 2 : pi / 2;
        }

        // z = atan(|y/x|) without spurious underflow
        if ((m & 2) != 0 && iy + (64 << 20) < ix)
        {
            // |y/x| < 0x1p-64, x<0
            z = 0;
        }
        else
        {
            z = atan(Math.Abs(y / x));
        }

        switch (m)
        {
            case 0: return z; // atan(+,+)
            case 1: return -z; // atan(-,+)
            case 2: return pi - (z - pi_lo); // atan(+,-)
            default: return (z - pi_lo) - pi; // atan(-,-)
        }
    }

    // scalbn.c: scalbn
    public static double scalbn(double x, int n)
    {
        double y = x;

        if (n > 1023)
        {
            y *= TwoP1023;
            n -= 1023;
            if (n > 1023)
            {
                y *= TwoP1023;
                n -= 1023;
                if (n > 1023)
                {
                    n = 1023;
                }
            }
        }
        else if (n < -1022)
        {
            // make sure final n < -53 to avoid double rounding in the subnormal range
            y *= TwoM1022 * TwoP53;
            n += 1022 - 53;
            if (n < -1022)
            {
                y *= TwoM1022 * TwoP53;
                n += 1022 - 53;
                if (n < -1022)
                {
                    n = -1022;
                }
            }
        }

        x = y * asdouble((ulong)(0x3ff + n) << 52);
        return x;
    }

    // __sin.c: __sin
    private static double __sin(double x, double y, int iy)
    {
        const double S1 = __sin_c.S1, S2 = __sin_c.S2, S3 = __sin_c.S3, S4 = __sin_c.S4, S5 = __sin_c.S5, S6 = __sin_c.S6;

        double z = x * x;
        double w = z * z;
        double r = S2 + (z * (S3 + (z * S4))) + (z * w * (S5 + (z * S6)));
        double v = z * x;
        if (iy == 0)
        {
            return x + (v * (S1 + (z * r)));
        }

        return x - (((z * ((0.5 * y) - (v * r))) - y) - (v * S1));
    }

    // __cos.c: __cos
    private static double __cos(double x, double y)
    {
        const double C1 = __cos_c.C1, C2 = __cos_c.C2, C3 = __cos_c.C3, C4 = __cos_c.C4, C5 = __cos_c.C5, C6 = __cos_c.C6;

        double z = x * x;
        double w = z * z;
        double r = (z * (C1 + (z * (C2 + (z * C3))))) + (w * w * (C4 + (z * (C5 + (z * C6)))));
        double hz = 0.5 * z;
        w = 1.0 - hz;
        return w + (((1.0 - w) - hz) + ((z * r) - (x * y)));
    }

    // __tan.c: __tan
    private static double __tan(double x, double y, int odd)
    {
        double[] T = __tan_c.T;
        double z, r, v, w, s, a;
        double w0, a0;
        uint sign = 0;

        uint hx = GET_HIGH_WORD(x);
        bool big = (hx & 0x7fffffff) >= 0x3FE59428; // |x| >= 0.6744
        if (big)
        {
            sign = hx >> 31;
            if (sign != 0)
            {
                x = -x;
                y = -y;
            }

            x = (__tan_c.pio4 - x) + (__tan_c.pio4lo - y);
            y = 0.0;
        }

        z = x * x;
        w = z * z;

        // Break x^5*(T[1]+x^2*T[2]+...) into
        // x^5(T[1]+x^4*T[3]+...+x^20*T[11]) +
        // x^5(x^2*(T[2]+x^4*T[4]+...+x^22*[T12]))
        r = T[1] + (w * (T[3] + (w * (T[5] + (w * (T[7] + (w * (T[9] + (w * T[11])))))))));
        v = z * (T[2] + (w * (T[4] + (w * (T[6] + (w * (T[8] + (w * (T[10] + (w * T[12]))))))))));
        s = z * x;
        r = y + (z * ((s * (r + v)) + y)) + (s * T[0]);
        w = x + r;
        if (big)
        {
            s = 1 - (2 * odd);
            v = s - (2.0 * (x + (r - (w * w / (w + s)))));
            return sign != 0 ? -v : v;
        }

        if (odd == 0)
        {
            return w;
        }

        // -1.0/(x+r) has up to 2ulp error, so compute it accurately
        w0 = w;
        w0 = SET_LOW_WORD(w0, 0);
        v = r - (w0 - x); // w0+v = r+x
        a0 = a = -1.0 / w;
        a0 = SET_LOW_WORD(a0, 0);
        return a0 + (a * (1.0 + (a0 * w0) + (a0 * v)));
    }

    // __rem_pio2.c: __rem_pio2
    // Caller must handle the case when reduction is not needed: |x| ~<= pi/4.
    private static int __rem_pio2(double x, Span<double> y)
    {
        const double toint = __rem_pio2_c.toint, pio4 = __rem_pio2_c.pio4, invpio2 = __rem_pio2_c.invpio2;
        const double pio2_1 = __rem_pio2_c.pio2_1, pio2_1t = __rem_pio2_c.pio2_1t;
        const double pio2_2 = __rem_pio2_c.pio2_2, pio2_2t = __rem_pio2_c.pio2_2t;
        const double pio2_3 = __rem_pio2_c.pio2_3, pio2_3t = __rem_pio2_c.pio2_3t;

        ulong ui = asuint64(x);
        double z, w, t, r, fn;
        Span<double> tx = stackalloc double[3];
        Span<double> ty = stackalloc double[2];
        int n, ex, ey, i;

        int sign = (int)(ui >> 63);
        uint ix = (uint)(ui >> 32) & 0x7fffffff;

        // The C jumps to "medium" for the cases with cancellation; they all fall through to it here.
        if (ix <= 0x400f6a7a)
        {
            // |x| ~<= 5pi/4
            if ((ix & 0xfffff) != 0x921fb)
            {
                // not |x| ~= pi/2 or 2pi/2 (cancellation: use the medium case)
                if (ix <= 0x4002d97c)
                {
                    // |x| ~<= 3pi/4
                    if (sign == 0)
                    {
                        z = x - pio2_1; // one round good to 85 bits
                        y[0] = z - pio2_1t;
                        y[1] = (z - y[0]) - pio2_1t;
                        return 1;
                    }
                    else
                    {
                        z = x + pio2_1;
                        y[0] = z + pio2_1t;
                        y[1] = (z - y[0]) + pio2_1t;
                        return -1;
                    }
                }
                else
                {
                    if (sign == 0)
                    {
                        z = x - (2 * pio2_1);
                        y[0] = z - (2 * pio2_1t);
                        y[1] = (z - y[0]) - (2 * pio2_1t);
                        return 2;
                    }
                    else
                    {
                        z = x + (2 * pio2_1);
                        y[0] = z + (2 * pio2_1t);
                        y[1] = (z - y[0]) + (2 * pio2_1t);
                        return -2;
                    }
                }
            }
        }
        else if (ix <= 0x401c463b)
        {
            // |x| ~<= 9pi/4
            if (ix <= 0x4015fdbc)
            {
                // |x| ~<= 7pi/4
                if (ix != 0x4012d97c)
                {
                    // not |x| ~= 3pi/2
                    if (sign == 0)
                    {
                        z = x - (3 * pio2_1);
                        y[0] = z - (3 * pio2_1t);
                        y[1] = (z - y[0]) - (3 * pio2_1t);
                        return 3;
                    }
                    else
                    {
                        z = x + (3 * pio2_1);
                        y[0] = z + (3 * pio2_1t);
                        y[1] = (z - y[0]) + (3 * pio2_1t);
                        return -3;
                    }
                }
            }
            else
            {
                if (ix != 0x401921fb)
                {
                    // not |x| ~= 4pi/2
                    if (sign == 0)
                    {
                        z = x - (4 * pio2_1);
                        y[0] = z - (4 * pio2_1t);
                        y[1] = (z - y[0]) - (4 * pio2_1t);
                        return 4;
                    }
                    else
                    {
                        z = x + (4 * pio2_1);
                        y[0] = z + (4 * pio2_1t);
                        y[1] = (z - y[0]) + (4 * pio2_1t);
                        return -4;
                    }
                }
            }
        }

        if (ix < 0x413921fb)
        {
            // |x| ~< 2^20*(pi/2), medium size
            // rint(x/(pi/2))
            fn = (x * invpio2) + toint - toint;
            n = (int)fn;
            r = x - (fn * pio2_1);
            w = fn * pio2_1t; // 1st round, good to 85 bits

            // Matters with directed rounding.
            if (r - w < -pio4)
            {
                n--;
                fn--;
                r = x - (fn * pio2_1);
                w = fn * pio2_1t;
            }
            else if (r - w > pio4)
            {
                n++;
                fn++;
                r = x - (fn * pio2_1);
                w = fn * pio2_1t;
            }

            y[0] = r - w;
            ey = (int)((asuint64(y[0]) >> 52) & 0x7ff);
            ex = (int)(ix >> 20);
            if (ex - ey > 16)
            {
                // 2nd round, good to 118 bits
                t = r;
                w = fn * pio2_2;
                r = t - w;
                w = (fn * pio2_2t) - ((t - r) - w);
                y[0] = r - w;
                ey = (int)((asuint64(y[0]) >> 52) & 0x7ff);
                if (ex - ey > 49)
                {
                    // 3rd round, good to 151 bits, covers all cases
                    t = r;
                    w = fn * pio2_3;
                    r = t - w;
                    w = (fn * pio2_3t) - ((t - r) - w);
                    y[0] = r - w;
                }
            }

            y[1] = (r - y[0]) - w;
            return n;
        }

        // all other (large) arguments
        if (ix >= 0x7ff00000)
        {
            // x is inf or NaN
            y[0] = y[1] = x - x;
            return 0;
        }

        // set z = scalbn(|x|,-ilogb(x)+23)
        ui = asuint64(x);
        ui &= ulong.MaxValue >> 12;
        ui |= (ulong)(0x3ff + 23) << 52;
        z = asdouble(ui);
        for (i = 0; i < 2; i++)
        {
            tx[i] = (int)z;
            z = (z - tx[i]) * TwoP24;
        }

        tx[i] = z;

        // skip zero terms, first term is non-zero
        while (tx[i] == 0.0)
        {
            i--;
        }

        n = __rem_pio2_large(tx, ty, (int)(ix >> 20) - (0x3ff + 23), i + 1);
        if (sign != 0)
        {
            y[0] = -ty[0];
            y[1] = -ty[1];
            return -n;
        }

        y[0] = ty[0];
        y[1] = ty[1];
        return n;
    }

    // __rem_pio2_large.c: __rem_pio2_large, with prec 1 (double), the only precision __rem_pio2 asks for
    private static int __rem_pio2_large(ReadOnlySpan<double> x, Span<double> y, int e0, int nx)
    {
        const int prec = 1;
        int[] ipio2 = __rem_pio2_large_c.ipio2;
        double[] PIo2 = __rem_pio2_large_c.PIo2;

        int jz, jx, jv, jp, jk, carry, n, i, j, k, m, q0, ih;
        Span<int> iq = stackalloc int[20];
        double z, fw;
        Span<double> f = stackalloc double[20];
        Span<double> fq = stackalloc double[20];
        Span<double> q = stackalloc double[20];

        // initialize jk
        jk = __rem_pio2_large_c.init_jk[prec];
        jp = jk;

        // determine jx,jv,q0, note that 3>q0
        jx = nx - 1;
        jv = (e0 - 3) / 24;
        if (jv < 0)
        {
            jv = 0;
        }

        q0 = e0 - (24 * (jv + 1));

        // set up f[0] to f[jx+jk] where f[jx+jk] = ipio2[jv+jk]
        j = jv - jx;
        m = jx + jk;
        for (i = 0; i <= m; i++, j++)
        {
            f[i] = j < 0 ? 0.0 : ipio2[j];
        }

        // compute q[0],q[1],...q[jk]
        for (i = 0; i <= jk; i++)
        {
            for (j = 0, fw = 0.0; j <= jx; j++)
            {
                fw += x[j] * f[jx + i - j];
            }

            q[i] = fw;
        }

        jz = jk;

        while (true)
        {
            // recompute:
            // distill q[] into iq[] reversingly
            for (i = 0, j = jz, z = q[jz]; j > 0; i++, j--)
            {
                fw = (int)(TwoM24 * z);
                iq[i] = (int)(z - (TwoP24 * fw));
                z = q[j - 1] + fw;
            }

            // compute n
            z = scalbn(z, q0); // actual value of z
            z -= 8.0 * Math.Floor(z * 0.125); // trim off integer >= 8
            n = (int)z;
            z -= n;
            ih = 0;
            if (q0 > 0)
            {
                // need iq[jz-1] to determine n
                i = iq[jz - 1] >> (24 - q0);
                n += i;
                iq[jz - 1] -= i << (24 - q0);
                ih = iq[jz - 1] >> (23 - q0);
            }
            else if (q0 == 0)
            {
                ih = iq[jz - 1] >> 23;
            }
            else if (z >= 0.5)
            {
                ih = 2;
            }

            if (ih > 0)
            {
                // q > 0.5
                n += 1;
                carry = 0;
                for (i = 0; i < jz; i++)
                {
                    // compute 1-q
                    j = iq[i];
                    if (carry == 0)
                    {
                        if (j != 0)
                        {
                            carry = 1;
                            iq[i] = 0x1000000 - j;
                        }
                    }
                    else
                    {
                        iq[i] = 0xffffff - j;
                    }
                }

                if (q0 > 0)
                {
                    // rare case: chance is 1 in 12
                    switch (q0)
                    {
                        case 1:
                            iq[jz - 1] &= 0x7fffff;
                            break;
                        case 2:
                            iq[jz - 1] &= 0x3fffff;
                            break;
                    }
                }

                if (ih == 2)
                {
                    z = 1.0 - z;
                    if (carry != 0)
                    {
                        z -= scalbn(1.0, q0);
                    }
                }
            }

            // check if recomputation is needed
            if (z == 0.0)
            {
                j = 0;
                for (i = jz - 1; i >= jk; i--)
                {
                    j |= iq[i];
                }

                if (j == 0)
                {
                    // need recomputation
                    for (k = 1; iq[jk - k] == 0; k++)
                    {
                        // k = no. of terms needed
                    }

                    for (i = jz + 1; i <= jz + k; i++)
                    {
                        // add q[jz+1] to q[jz+k]
                        f[jx + i] = ipio2[jv + i];
                        for (j = 0, fw = 0.0; j <= jx; j++)
                        {
                            fw += x[j] * f[jx + i - j];
                        }

                        q[i] = fw;
                    }

                    jz += k;
                    continue;
                }
            }

            break;
        }

        // chop off zero terms
        if (z == 0.0)
        {
            jz -= 1;
            q0 -= 24;
            while (iq[jz] == 0)
            {
                jz--;
                q0 -= 24;
            }
        }
        else
        {
            // break z into 24-bit if necessary
            z = scalbn(z, -q0);
            if (z >= TwoP24)
            {
                fw = (int)(TwoM24 * z);
                iq[jz] = (int)(z - (TwoP24 * fw));
                jz += 1;
                q0 += 24;
                iq[jz] = (int)fw;
            }
            else
            {
                iq[jz] = (int)z;
            }
        }

        // convert integer "bit" chunk to floating-point value
        fw = scalbn(1.0, q0);
        for (i = jz; i >= 0; i--)
        {
            q[i] = fw * iq[i];
            fw *= TwoM24;
        }

        // compute PIo2[0,...,jp]*q[jz,...,0]
        for (i = jz; i >= 0; i--)
        {
            for (fw = 0.0, k = 0; k <= jp && k <= jz - i; k++)
            {
                fw += PIo2[k] * q[i + k];
            }

            fq[jz - i] = fw;
        }

        // compress fq[] into y[] (prec 1)
        fw = 0.0;
        for (i = jz; i >= 0; i--)
        {
            fw += fq[i];
        }

        y[0] = ih == 0 ? fw : -fw;
        fw = fq[0] - fw;
        for (i = 1; i <= jz; i++)
        {
            fw += fq[i];
        }

        y[1] = ih == 0 ? fw : -fw;
        return n & 7;
    }

    // Powers of two the C writes as hexadecimal floating point literals (0x1p24, 0x1p-24, ...)
    private static readonly double TwoP24 = Math.ScaleB(1.0, 24);
    private static readonly double TwoM24 = Math.ScaleB(1.0, -24);
    private static readonly double TwoP53 = Math.ScaleB(1.0, 53);
    private static readonly double TwoM120 = Math.ScaleB(1.0, -120);
    private static readonly double TwoP1023 = Math.ScaleB(1.0, 1023);
    private static readonly double TwoM1022 = Math.ScaleB(1.0, -1022);

    // libm.h: GET_HIGH_WORD
    private static uint GET_HIGH_WORD(double d) => (uint)(asuint64(d) >> 32);

    // libm.h: EXTRACT_WORDS
    private static (uint Hi, uint Lo) EXTRACT_WORDS(double d) => ((uint)(asuint64(d) >> 32), (uint)asuint64(d));

    // libm.h: SET_LOW_WORD
    private static double SET_LOW_WORD(double d, uint lo) => asdouble((asuint64(d) & 0xffffffff00000000UL) | lo);

    // libm.h: asuint64, asdouble
    private static ulong asuint64(double f) => BitConverter.DoubleToUInt64Bits(f);

    private static double asdouble(ulong i) => BitConverter.UInt64BitsToDouble(i);
}
