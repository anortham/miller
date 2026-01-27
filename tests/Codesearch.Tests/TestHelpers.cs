namespace Codesearch.Tests;

public static class TestHelpers
{
    public static List<float> CreateTestVector(float seed)
    {
        var v = Enumerable.Range(0, 768).Select(i => (float)Math.Sin(seed + i * 0.001f)).ToList();
        var norm = (float)Math.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / norm).ToList();
    }
}
