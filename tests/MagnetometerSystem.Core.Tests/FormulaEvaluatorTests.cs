using MagnetometerSystem.Core.Helpers;

namespace MagnetometerSystem.Core.Tests;

public class FormulaEvaluatorTests
{
    private static readonly double[] DefaultChannels = { 100, 200, 300, 400, 500, 600, 700, 800 };

    [Fact]
    public void Evaluate_BasicAddition()
    {
        var eval = new FormulaEvaluator("CH0 + CH1");
        double result = eval.Evaluate(DefaultChannels);
        Assert.Equal(300.0, result, precision: 10);
    }

    [Fact]
    public void Evaluate_ArithmeticPrecedence()
    {
        // 100 + 200 * 300 = 100 + 60000 = 60100
        var eval = new FormulaEvaluator("CH0 + CH1 * CH2");
        double result = eval.Evaluate(DefaultChannels);
        Assert.Equal(60100.0, result, precision: 10);
    }

    [Fact]
    public void Evaluate_Parentheses()
    {
        // (100 + 200) * 300 = 90000
        var eval = new FormulaEvaluator("(CH0 + CH1) * CH2");
        double result = eval.Evaluate(DefaultChannels);
        Assert.Equal(90000.0, result, precision: 10);
    }

    [Fact]
    public void Evaluate_VariableSubstitution_Total()
    {
        var eval = new FormulaEvaluator("Total");
        double result = eval.Evaluate(DefaultChannels, totalField: 54321.0);
        Assert.Equal(54321.0, result, precision: 10);
    }

    [Fact]
    public void Evaluate_Sqrt()
    {
        // sqrt(400) = 20
        var eval = new FormulaEvaluator("sqrt(CH3)");
        double result = eval.Evaluate(DefaultChannels);
        Assert.Equal(20.0, result, precision: 10);
    }

    [Fact]
    public void Evaluate_Abs_NegativeResult()
    {
        // abs(100 - 300) = abs(-200) = 200
        var eval = new FormulaEvaluator("abs(CH0 - CH2)");
        double result = eval.Evaluate(DefaultChannels);
        Assert.Equal(200.0, result, precision: 10);
    }

    [Fact]
    public void Evaluate_Pow()
    {
        // pow(100, 2) = 10000
        var eval = new FormulaEvaluator("pow(CH0, 2)");
        double result = eval.Evaluate(DefaultChannels);
        Assert.Equal(10000.0, result, precision: 10);
    }

    [Fact]
    public void Evaluate_SinCos()
    {
        // sin(0) = 0, cos(0) = 1
        double[] channels = { 0, 0, 0, 0, 0, 0, 0, 0 };
        var sinEval = new FormulaEvaluator("sin(CH0)");
        var cosEval = new FormulaEvaluator("cos(CH0)");

        Assert.Equal(0.0, sinEval.Evaluate(channels), precision: 10);
        Assert.Equal(1.0, cosEval.Evaluate(channels), precision: 10);
    }

    [Fact]
    public void Evaluate_Log10()
    {
        // log(100) = 2
        var eval = new FormulaEvaluator("log(CH0)");
        double result = eval.Evaluate(DefaultChannels);
        Assert.Equal(2.0, result, precision: 10);
    }

    [Fact]
    public void Evaluate_DivisionByZero_ReturnsNaN()
    {
        double[] channels = { 100, 0, 0, 0, 0, 0, 0, 0 };
        var eval = new FormulaEvaluator("CH0 / CH1");
        double result = eval.Evaluate(channels);
        Assert.True(double.IsNaN(result));
    }

    [Fact]
    public void Evaluate_InvalidVariable_ReturnsNaN()
    {
        var eval = new FormulaEvaluator("CH99");
        double result = eval.Evaluate(DefaultChannels);
        Assert.True(double.IsNaN(result));
    }

    [Fact]
    public void Evaluate_ComplexFormula_MagnitudeCalculation()
    {
        // sqrt(CH0*CH0 + CH1*CH1 + CH2*CH2)
        // = sqrt(10000 + 40000 + 90000) = sqrt(140000)
        var eval = new FormulaEvaluator("sqrt(CH0*CH0 + CH1*CH1 + CH2*CH2)");
        double result = eval.Evaluate(DefaultChannels);
        double expected = Math.Sqrt(100 * 100 + 200 * 200 + 300 * 300);
        Assert.Equal(expected, result, precision: 6);
    }

    [Fact]
    public void Evaluate_NegativeNumberLiteral()
    {
        // -5 + CH0 = -5 + 100 = 95
        var eval = new FormulaEvaluator("-5 + CH0");
        double result = eval.Evaluate(DefaultChannels);
        Assert.Equal(95.0, result, precision: 10);
    }

    [Fact]
    public void TryValidate_ValidFormula_ReturnsTrue()
    {
        bool valid = FormulaEvaluator.TryValidate("CH0 + CH1", out string? error);
        Assert.True(valid);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidate_InvalidFormula_ReturnsFalse()
    {
        bool valid = FormulaEvaluator.TryValidate("CH99", out string? error);
        Assert.False(valid);
        Assert.NotNull(error);
    }
}
