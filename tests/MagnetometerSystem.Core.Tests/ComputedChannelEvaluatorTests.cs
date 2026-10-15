using MagnetometerSystem.Core.Processing;

namespace MagnetometerSystem.Core.Tests;

public class ComputedChannelEvaluatorTests
{
    [Fact]
    public void EvaluatesEachPointOfTheRequestedWindow()
    {
        double[][] channels = [[1, 2, 3, 4], [10, 20, 30, 40]];

        var values = new ComputedChannelEvaluator().Evaluate("CH0 + CH1", channels, start: 1, count: 2, missing: 0);

        Assert.Equal([22, 33], values);
    }

    [Fact]
    public void MissingChannelValuesUseTheFillTheCallerGives()
    {
        double[][] channels = [[1, 2, 3], [10]];
        var evaluator = new ComputedChannelEvaluator();

        // 绘图按 0 补，一键归零按 NaN 补（不计入均值）。
        Assert.Equal([11, 2, 3], evaluator.Evaluate("CH0 + CH1", channels, 0, 3, missing: 0));
        var forZero = evaluator.Evaluate("CH0 + CH1", channels, 0, 3, missing: double.NaN)!;
        Assert.Equal(11, forZero[0]);
        Assert.True(double.IsNaN(forZero[1]) && double.IsNaN(forZero[2]));
    }

    [Fact]
    public void ChannelsTheFormulaCannotReachEvaluateToNaN()
    {
        var values = new ComputedChannelEvaluator().Evaluate("CH5 * 2", [[1, 2]], 0, 2, missing: 0)!;

        Assert.All(values, v => Assert.True(double.IsNaN(v)));
    }

    [Fact]
    public void ParsedFormulasAreCachedUntilForgottenOrCleared()
    {
        var evaluator = new ComputedChannelEvaluator();
        var first = evaluator.GetOrCreate("sqrt(CH0*CH0 + CH1*CH1)");

        Assert.NotNull(first);
        Assert.Same(first, evaluator.GetOrCreate("sqrt(CH0*CH0 + CH1*CH1)"));

        evaluator.Forget("sqrt(CH0*CH0 + CH1*CH1)");
        var second = evaluator.GetOrCreate("sqrt(CH0*CH0 + CH1*CH1)");
        Assert.NotSame(first, second);

        evaluator.Clear();
        Assert.NotSame(second, evaluator.GetOrCreate("sqrt(CH0*CH0 + CH1*CH1)"));
    }
}
