namespace Miller.Core.Telemetry;

/// <summary>
/// The frozen statistical estimators of <c>canary-telemetry-v1</c> §Frozen analysis parameters, as pure
/// functions with zero I/O: the Welch two-sample 95% t-interval over per-unit rates, a one-sample 95% t-interval
/// for the shadow margins, the nearest-rank p95, and the export's bucketed-p95 approximation. The Student-t
/// critical value is Hill's Algorithm 396, seeded by Acklam's inverse-normal — deterministic and offline.
/// </summary>
public static class CanaryGateMath
{
    /// <summary>The frozen <c>latency_bucket</c> ladder in ascending order, excluding the sentinel <c>none</c>.</summary>
    public static readonly IReadOnlyList<string> LatencyLadder =
        ["lt_10", "lt_25", "lt_50", "lt_100", "lt_250", "lt_500", "lt_1000", "lt_3000", "gte_3000"];

    /// <summary>
    /// The Welch two-sample two-sided 95% t-interval for <c>mean(a) − mean(b)</c> using Welch–Satterthwaite
    /// degrees of freedom. <c>a</c> is the treatment arm's per-unit rates, <c>b</c> the control arm's.
    /// </summary>
    public static (double Lower, double Upper, double Effect) WelchInterval(
        IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count < 2 || b.Count < 2)
            throw new ArgumentException("Welch interval needs at least two observations per arm.");

        double meanA = Mean(a);
        double meanB = Mean(b);
        double varA = SampleVariance(a, meanA);
        double varB = SampleVariance(b, meanB);
        double effect = meanA - meanB;

        double sa = varA / a.Count;
        double sb = varB / b.Count;
        double se = Math.Sqrt(sa + sb);
        if (se == 0.0)
            return (effect, effect, effect);

        double df = (sa + sb) * (sa + sb)
            / (sa * sa / (a.Count - 1) + sb * sb / (b.Count - 1));
        double t = StudentTCritical(0.05, df);
        return (effect - t * se, effect + t * se, effect);
    }

    /// <summary>The one-sample two-sided 95% t-interval for the mean of <paramref name="xs"/>.</summary>
    public static (double Lower, double Upper, double Mean) OneSampleInterval(IReadOnlyList<double> xs)
    {
        ArgumentNullException.ThrowIfNull(xs);
        if (xs.Count < 2)
            throw new ArgumentException("One-sample interval needs at least two observations.");

        double mean = Mean(xs);
        double se = Math.Sqrt(SampleVariance(xs, mean) / xs.Count);
        if (se == 0.0)
            return (mean, mean, mean);

        double t = StudentTCritical(0.05, xs.Count - 1);
        return (mean - t * se, mean + t * se, mean);
    }

    /// <summary>
    /// Nearest-rank p95 over an ascending list of integer latencies: the value at 1-based index
    /// <c>ceil(0.95 × n)</c>, with no interpolation. Callers pass an already-sorted list.
    /// </summary>
    public static long NearestRankP95(IReadOnlyList<long> ascending)
    {
        ArgumentNullException.ThrowIfNull(ascending);
        if (ascending.Count == 0)
            throw new ArgumentException("Nearest-rank p95 needs at least one observation.");

        int rank = (int)Math.Ceiling(0.95 * ascending.Count);
        return ascending[rank - 1];
    }

    /// <summary>
    /// The export's bucketed p95 (§Warm-latency clause, export approximation): the first ladder rung whose
    /// cumulative count reaches <c>ceil(0.95 × calls)</c>, walking the ladder in ascending order.
    /// </summary>
    public static string BucketedP95(IReadOnlyDictionary<string, int> bucketCounts, int calls)
    {
        ArgumentNullException.ThrowIfNull(bucketCounts);
        if (calls <= 0)
            throw new ArgumentException("Bucketed p95 needs at least one call.");

        int target = (int)Math.Ceiling(0.95 * calls);
        int cumulative = 0;
        foreach (string rung in LatencyLadder)
        {
            if (bucketCounts.TryGetValue(rung, out int count))
                cumulative += count;
            if (cumulative >= target)
                return rung;
        }

        return LatencyLadder[^1];
    }

    /// <summary>
    /// The two-tailed Student-t critical value: the <c>t</c> with two-tailed area <paramref name="twoTailedAlpha"/>
    /// at <paramref name="df"/> degrees of freedom (Hill 1970, Algorithm 396). <c>StudentTCritical(0.05, df)</c> is
    /// the 95% two-sided multiplier; it converges to the normal <c>1.95996</c> as <c>df → ∞</c>.
    /// </summary>
    public static double StudentTCritical(double twoTailedAlpha, double df)
    {
        if (twoTailedAlpha is <= 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(twoTailedAlpha));
        if (df < 1.0)
            throw new ArgumentOutOfRangeException(nameof(df));

        double p = twoTailedAlpha;
        if (df == 1.0)
        {
            double half = p * Math.PI / 2.0;
            return Math.Cos(half) / Math.Sin(half);
        }
        if (df == 2.0)
            return Math.Sqrt(2.0 / (p * (2.0 - p)) - 2.0);

        double a = 1.0 / (df - 0.5);
        double b = 48.0 / (a * a);
        double c = ((20700.0 * a / b - 98.0) * a - 16.0) * a + 96.36;
        double d = ((94.5 / (b + c) - 3.0) / b + 1.0) * Math.Sqrt(a * Math.PI / 2.0) * df;
        double y = Math.Pow(d * p, 2.0 / df);

        if (y > 0.05 + a)
        {
            double x = InverseNormalCdf(1.0 - 0.5 * p);
            y = x * x;
            if (df < 5.0)
                c += 0.3 * (df - 4.5) * (x + 0.6);
            c = (((0.05 * d * x - 5.0) * x - 7.0) * x - 2.0) * x + b + c;
            y = (((((0.4 * y + 6.3) * y + 36.0) * y + 94.5) / c - y - 3.0) / b + 1.0) * x;
            y = a * y * y;
            y = y > 0.002 ? Math.Exp(y) - 1.0 : 0.5 * y * y + y;
        }
        else
        {
            y = ((1.0 / (((df + 6.0) / (df * y) - 0.089 * d - 0.822) * (df + 2.0) * 3.0)
                + 0.5 / (df + 4.0)) * y - 1.0) * (df + 1.0) / (df + 2.0) + 1.0 / y;
        }

        return Math.Sqrt(df * y);
    }

    private static double Mean(IReadOnlyList<double> values)
    {
        double sum = 0.0;
        for (int i = 0; i < values.Count; i++)
            sum += values[i];
        return sum / values.Count;
    }

    private static double SampleVariance(IReadOnlyList<double> values, double mean)
    {
        double sum = 0.0;
        for (int i = 0; i < values.Count; i++)
        {
            double delta = values[i] - mean;
            sum += delta * delta;
        }
        return sum / (values.Count - 1);
    }

    private static double InverseNormalCdf(double p)
    {
        const double a1 = -3.969683028665376e+01, a2 = 2.209460984245205e+02, a3 = -2.759285104469687e+02;
        const double a4 = 1.383577518672690e+02, a5 = -3.066479806614716e+01, a6 = 2.506628277459239e+00;
        const double b1 = -5.447609879822406e+01, b2 = 1.615858368580409e+02, b3 = -1.556989798598866e+02;
        const double b4 = 6.680131188771972e+01, b5 = -1.328068155288572e+01;
        const double c1 = -7.784894002430293e-03, c2 = -3.223964580411365e-01, c3 = -2.400758277161838e+00;
        const double c4 = -2.549732539343734e+00, c5 = 4.374664141464968e+00, c6 = 2.938163982698783e+00;
        const double d1 = 7.784695709041462e-03, d2 = 3.224671290700398e-01, d3 = 2.445134137142996e+00;
        const double d4 = 3.754408661907416e+00;
        const double pLow = 0.02425;
        const double pHigh = 1.0 - pLow;

        if (p < pLow)
        {
            double q = Math.Sqrt(-2.0 * Math.Log(p));
            return (((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6)
                / ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0);
        }
        if (p <= pHigh)
        {
            double q = p - 0.5;
            double r = q * q;
            return (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q
                / (((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1.0);
        }
        else
        {
            double q = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
            return -(((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6)
                / ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0);
        }
    }
}
