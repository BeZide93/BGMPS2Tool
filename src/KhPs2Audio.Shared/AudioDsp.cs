namespace KhPs2Audio.Shared;

internal static class AudioDsp
{
    public static short[] ApplyPreEncodeConditioning(short[] input, int sampleRate, double preEqStrength, double preLowPassHz)
    {
        if (input.Length == 0)
        {
            return [];
        }

        var output = (short[])input.Clone();
        if (preEqStrength > 0.0001)
        {
            var clampedStrength = Math.Clamp(preEqStrength, 0.0, 1.0);
            output = ApplyLowShelf(output, sampleRate, 140.0, 1.2 * clampedStrength);
            output = ApplyPeakingEq(output, sampleRate, 3400.0, 0.85, -2.2 * clampedStrength);
            output = ApplyHighShelf(output, sampleRate, 7200.0, -3.5 * clampedStrength);
        }

        if (preLowPassHz > 20.0)
        {
            output = ApplyLowPass(output, sampleRate, preLowPassHz);
        }

        return output;
    }

    public static short[] MixToMono(short[] left, short[] right, int sampleRate)
    {
        var length = Math.Min(left.Length, right.Length);
        if (length == 0)
        {
            return [];
        }

        var output = new short[length];
        var crossoverHz = 180.0;
        var alpha = ComputeLowPassAlpha(sampleRate, crossoverHz);
        double lowLeft = 0;
        double lowRight = 0;

        for (var i = 0; i < length; i++)
        {
            var currentLeft = left[i];
            var currentRight = right[i];

            lowLeft += alpha * (currentLeft - lowLeft);
            lowRight += alpha * (currentRight - lowRight);

            var highLeft = currentLeft - lowLeft;
            var highRight = currentRight - lowRight;

            var lowMono = (lowLeft + lowRight) * 0.5;
            if ((lowLeft < 0 && lowRight > 0) || (lowLeft > 0 && lowRight < 0))
            {
                var dominant = Math.Abs(lowLeft) >= Math.Abs(lowRight) ? lowLeft : lowRight;
                lowMono = (dominant * 0.70) + (lowMono * 0.30);
            }

            var highMono = (highLeft + highRight) * 0.5;
            var sample = lowMono + (highMono * 0.92);
            output[i] = ClampToInt16(sample);
        }

        return output;
    }

    public static short[] ResampleMono(short[] input, int sourceRate, int targetRate)
    {
        if (input.Length == 0)
        {
            return [];
        }

        if (sourceRate == targetRate)
        {
            return (short[])input.Clone();
        }

        short[] filteredInput;
        if (targetRate < sourceRate)
        {
            filteredInput = ApplyLowPass(input, sourceRate, targetRate * 0.45);
        }
        else
        {
            filteredInput = input;
        }

        var outputLength = Math.Max(1, (int)Math.Round(filteredInput.Length * (targetRate / (double)sourceRate), MidpointRounding.AwayFromZero));
        var output = new short[outputLength];
        var step = sourceRate / (double)targetRate;

        for (var i = 0; i < outputLength; i++)
        {
            var position = i * step;
            output[i] = SampleHermite(filteredInput, position);
        }

        return output;
    }

    private static short[] ApplyLowPass(short[] input, int sampleRate, double cutoffHz)
    {
        if (input.Length == 0)
        {
            return [];
        }

        var q = 0.7071067811865476;
        var firstPass = ApplyBiquadLowPass(input, sampleRate, cutoffHz, q);
        return ApplyBiquadLowPass(firstPass, sampleRate, cutoffHz, q);
    }

    private static short[] ApplyLowShelf(short[] input, int sampleRate, double cutoffHz, double gainDb)
    {
        if (Math.Abs(gainDb) < 0.0001)
        {
            return (short[])input.Clone();
        }

        var coefficients = CreateLowShelf(sampleRate, cutoffHz, gainDb, 1.0);
        return ApplyBiquad(input, coefficients);
    }

    private static short[] ApplyHighShelf(short[] input, int sampleRate, double cutoffHz, double gainDb)
    {
        if (Math.Abs(gainDb) < 0.0001)
        {
            return (short[])input.Clone();
        }

        var coefficients = CreateHighShelf(sampleRate, cutoffHz, gainDb, 1.0);
        return ApplyBiquad(input, coefficients);
    }

    private static short[] ApplyPeakingEq(short[] input, int sampleRate, double centerHz, double q, double gainDb)
    {
        if (Math.Abs(gainDb) < 0.0001)
        {
            return (short[])input.Clone();
        }

        var coefficients = CreatePeakingEq(sampleRate, centerHz, q, gainDb);
        return ApplyBiquad(input, coefficients);
    }

    private static short[] ApplyBiquadLowPass(short[] input, int sampleRate, double cutoffHz, double q)
    {
        var omega = 2.0 * Math.PI * Math.Clamp(cutoffHz, 20.0, (sampleRate * 0.5) - 20.0) / sampleRate;
        var sin = Math.Sin(omega);
        var cos = Math.Cos(omega);
        var alpha = sin / (2.0 * q);

        var b0 = (1.0 - cos) * 0.5;
        var b1 = 1.0 - cos;
        var b2 = (1.0 - cos) * 0.5;
        var a0 = 1.0 + alpha;
        var a1 = -2.0 * cos;
        var a2 = 1.0 - alpha;

        b0 /= a0;
        b1 /= a0;
        b2 /= a0;
        a1 /= a0;
        a2 /= a0;

        var output = new short[input.Length];
        double x1 = 0;
        double x2 = 0;
        double y1 = 0;
        double y2 = 0;

        for (var i = 0; i < input.Length; i++)
        {
            var x0 = input[i];
            var y0 = (b0 * x0) + (b1 * x1) + (b2 * x2) - (a1 * y1) - (a2 * y2);
            output[i] = ClampToInt16(y0);
            x2 = x1;
            x1 = x0;
            y2 = y1;
            y1 = y0;
        }

        return output;
    }

    private static short[] ApplyBiquad(short[] input, BiquadCoefficients coefficients)
    {
        var output = new short[input.Length];
        double x1 = 0;
        double x2 = 0;
        double y1 = 0;
        double y2 = 0;

        for (var i = 0; i < input.Length; i++)
        {
            var x0 = input[i];
            var y0 = (coefficients.B0 * x0) + (coefficients.B1 * x1) + (coefficients.B2 * x2) - (coefficients.A1 * y1) - (coefficients.A2 * y2);
            output[i] = ClampToInt16(y0);
            x2 = x1;
            x1 = x0;
            y2 = y1;
            y1 = y0;
        }

        return output;
    }

    private static BiquadCoefficients CreatePeakingEq(int sampleRate, double centerHz, double q, double gainDb)
    {
        var omega = 2.0 * Math.PI * ClampFilterFrequency(sampleRate, centerHz) / sampleRate;
        var sin = Math.Sin(omega);
        var cos = Math.Cos(omega);
        var alpha = sin / (2.0 * Math.Max(0.1, q));
        var a = Math.Pow(10.0, gainDb / 40.0);

        var b0 = 1.0 + (alpha * a);
        var b1 = -2.0 * cos;
        var b2 = 1.0 - (alpha * a);
        var a0 = 1.0 + (alpha / a);
        var a1 = -2.0 * cos;
        var a2 = 1.0 - (alpha / a);

        return NormalizeBiquad(b0, b1, b2, a0, a1, a2);
    }

    private static BiquadCoefficients CreateLowShelf(int sampleRate, double cutoffHz, double gainDb, double slope)
    {
        var omega = 2.0 * Math.PI * ClampFilterFrequency(sampleRate, cutoffHz) / sampleRate;
        var sin = Math.Sin(omega);
        var cos = Math.Cos(omega);
        var a = Math.Pow(10.0, gainDb / 40.0);
        var safeSlope = Math.Max(0.1, slope);
        var alpha = sin / 2.0 * Math.Sqrt((a + (1.0 / a)) * ((1.0 / safeSlope) - 1.0) + 2.0);
        var beta = 2.0 * Math.Sqrt(a) * alpha;

        var b0 = a * ((a + 1.0) - ((a - 1.0) * cos) + beta);
        var b1 = 2.0 * a * ((a - 1.0) - ((a + 1.0) * cos));
        var b2 = a * ((a + 1.0) - ((a - 1.0) * cos) - beta);
        var a0 = (a + 1.0) + ((a - 1.0) * cos) + beta;
        var a1 = -2.0 * ((a - 1.0) + ((a + 1.0) * cos));
        var a2 = (a + 1.0) + ((a - 1.0) * cos) - beta;

        return NormalizeBiquad(b0, b1, b2, a0, a1, a2);
    }

    private static BiquadCoefficients CreateHighShelf(int sampleRate, double cutoffHz, double gainDb, double slope)
    {
        var omega = 2.0 * Math.PI * ClampFilterFrequency(sampleRate, cutoffHz) / sampleRate;
        var sin = Math.Sin(omega);
        var cos = Math.Cos(omega);
        var a = Math.Pow(10.0, gainDb / 40.0);
        var safeSlope = Math.Max(0.1, slope);
        var alpha = sin / 2.0 * Math.Sqrt((a + (1.0 / a)) * ((1.0 / safeSlope) - 1.0) + 2.0);
        var beta = 2.0 * Math.Sqrt(a) * alpha;

        var b0 = a * ((a + 1.0) + ((a - 1.0) * cos) + beta);
        var b1 = -2.0 * a * ((a - 1.0) + ((a + 1.0) * cos));
        var b2 = a * ((a + 1.0) + ((a - 1.0) * cos) - beta);
        var a0 = (a + 1.0) - ((a - 1.0) * cos) + beta;
        var a1 = 2.0 * ((a - 1.0) - ((a + 1.0) * cos));
        var a2 = (a + 1.0) - ((a - 1.0) * cos) - beta;

        return NormalizeBiquad(b0, b1, b2, a0, a1, a2);
    }

    private static BiquadCoefficients NormalizeBiquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        return new BiquadCoefficients(
            b0 / a0,
            b1 / a0,
            b2 / a0,
            a1 / a0,
            a2 / a0);
    }

    private static double ClampFilterFrequency(int sampleRate, double frequencyHz)
    {
        return Math.Clamp(frequencyHz, 20.0, Math.Max(40.0, (sampleRate * 0.5) - 20.0));
    }

    private static short SampleHermite(short[] input, double position)
    {
        var index1 = Math.Clamp((int)Math.Floor(position), 0, input.Length - 1);
        var index0 = Math.Max(0, index1 - 1);
        var index2 = Math.Min(input.Length - 1, index1 + 1);
        var index3 = Math.Min(input.Length - 1, index1 + 2);
        var fraction = position - index1;

        var y0 = input[index0];
        var y1 = input[index1];
        var y2 = input[index2];
        var y3 = input[index3];

        var c0 = y1;
        var c1 = 0.5 * (y2 - y0);
        var c2 = y0 - (2.5 * y1) + (2.0 * y2) - (0.5 * y3);
        var c3 = (0.5 * (y3 - y0)) + (1.5 * (y1 - y2));
        var sample = ((c3 * fraction + c2) * fraction + c1) * fraction + c0;
        return ClampToInt16(sample);
    }

    private static double ComputeLowPassAlpha(int sampleRate, double cutoffHz)
    {
        var clampedCutoff = Math.Clamp(cutoffHz, 20.0, (sampleRate * 0.5) - 20.0);
        var rc = 1.0 / (2.0 * Math.PI * clampedCutoff);
        var dt = 1.0 / sampleRate;
        return dt / (rc + dt);
    }

    private static short ClampToInt16(double sample)
    {
        return (short)Math.Clamp(Math.Round(sample, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
    }

    private readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2);
}
