namespace MagnetometerSystem.Core.Helpers;

/// <summary>
/// 简易公式求值器（Shunting-yard 算法）
/// 支持变量：CH0~CH7、Total
/// 支持运算：+ - * / sqrt abs pow
/// 示例公式：CH0 / CH1、sqrt(CH0*CH0 + CH1*CH1 + CH2*CH2)
/// </summary>
public class FormulaEvaluator
{
    private readonly string _formula;
    private readonly List<Token> _rpn; // 逆波兰表达式

    public FormulaEvaluator(string formula)
    {
        _formula = formula;
        _rpn = ToRPN(Tokenize(formula));
    }

    /// <summary>
    /// 使用给定通道值求值
    /// </summary>
    /// <param name="channelValues">CH0~CH7 的值</param>
    /// <param name="totalField">总场值（可选）</param>
    /// <returns>计算结果，失败返回 NaN</returns>
    public double Evaluate(double[] channelValues, double? totalField = null)
    {
        var stack = new Stack<double>();

        foreach (var token in _rpn)
        {
            switch (token.Type)
            {
                case TokenType.Number:
                    stack.Push(token.Value);
                    break;

                case TokenType.Variable:
                    if (token.Name == "Total" || token.Name == "TOTAL")
                    {
                        stack.Push(totalField ?? double.NaN);
                    }
                    else if (token.Name.StartsWith("CH", StringComparison.OrdinalIgnoreCase)
                             && int.TryParse(token.Name[2..], out int idx)
                             && idx >= 0 && idx < channelValues.Length)
                    {
                        stack.Push(channelValues[idx]);
                    }
                    else
                    {
                        return double.NaN;
                    }
                    break;

                case TokenType.Operator:
                    if (stack.Count < 2) return double.NaN;
                    double b = stack.Pop(), a = stack.Pop();
                    stack.Push(token.Name switch
                    {
                        "+" => a + b,
                        "-" => a - b,
                        "*" => a * b,
                        "/" => b != 0 ? a / b : double.NaN,
                        _ => double.NaN
                    });
                    break;

                case TokenType.Function:
                    if (token.Name == "pow")
                    {
                        if (stack.Count < 2) return double.NaN;
                        double exp = stack.Pop(), bas = stack.Pop();
                        stack.Push(Math.Pow(bas, exp));
                    }
                    else
                    {
                        if (stack.Count < 1) return double.NaN;
                        double arg = stack.Pop();
                        stack.Push(token.Name switch
                        {
                            "sqrt" => Math.Sqrt(arg),
                            "abs" => Math.Abs(arg),
                            "log" => Math.Log10(arg),
                            "ln" => Math.Log(arg),
                            "sin" => Math.Sin(arg),
                            "cos" => Math.Cos(arg),
                            _ => double.NaN
                        });
                    }
                    break;
            }
        }

        return stack.Count == 1 ? stack.Pop() : double.NaN;
    }

    /// <summary>验证公式是否合法</summary>
    public static bool TryValidate(string formula, out string? error)
    {
        try
        {
            var eval = new FormulaEvaluator(formula);
            var testValues = new double[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            double result = eval.Evaluate(testValues, 10);
            if (double.IsNaN(result))
            {
                error = "公式求值结果为 NaN（检查变量名和运算）";
                return false;
            }
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    #region Tokenizer & Parser

    private enum TokenType { Number, Variable, Operator, Function, LeftParen, RightParen, Comma }

    private record Token(TokenType Type, string Name = "", double Value = 0);

    private static List<Token> Tokenize(string formula)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < formula.Length)
        {
            char c = formula[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '(') { tokens.Add(new Token(TokenType.LeftParen)); i++; continue; }
            if (c == ')') { tokens.Add(new Token(TokenType.RightParen)); i++; continue; }
            if (c == ',') { tokens.Add(new Token(TokenType.Comma)); i++; continue; }

            if (c == '+' || c == '-' || c == '*' || c == '/')
            {
                // 处理负号（一元负号）
                if (c == '-' && (tokens.Count == 0 || tokens[^1].Type == TokenType.LeftParen || tokens[^1].Type == TokenType.Operator))
                {
                    // 一元负号，读取数字
                    i++;
                    int start = i;
                    while (i < formula.Length && (char.IsDigit(formula[i]) || formula[i] == '.'))
                        i++;
                    if (i > start && double.TryParse(formula[start..i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double negVal))
                    {
                        tokens.Add(new Token(TokenType.Number, Value: -negVal));
                    }
                    continue;
                }

                tokens.Add(new Token(TokenType.Operator, Name: c.ToString()));
                i++;
                continue;
            }

            if (char.IsDigit(c) || c == '.')
            {
                int start = i;
                while (i < formula.Length && (char.IsDigit(formula[i]) || formula[i] == '.'))
                    i++;
                if (double.TryParse(formula[start..i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double val))
                {
                    tokens.Add(new Token(TokenType.Number, Value: val));
                }
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < formula.Length && (char.IsLetterOrDigit(formula[i]) || formula[i] == '_'))
                    i++;
                string word = formula[start..i];

                // 检查是否是函数（后面跟着左括号）
                string[] functions = ["sqrt", "abs", "pow", "log", "ln", "sin", "cos"];
                if (functions.Contains(word.ToLower()) && i < formula.Length && formula[i] == '(')
                {
                    tokens.Add(new Token(TokenType.Function, Name: word.ToLower()));
                }
                else
                {
                    tokens.Add(new Token(TokenType.Variable, Name: word));
                }
                continue;
            }

            i++; // 跳过未识别字符
        }
        return tokens;
    }

    private static int Precedence(string op) => op switch
    {
        "+" or "-" => 1,
        "*" or "/" => 2,
        _ => 0
    };

    private static List<Token> ToRPN(List<Token> tokens)
    {
        var output = new List<Token>();
        var opStack = new Stack<Token>();

        foreach (var token in tokens)
        {
            switch (token.Type)
            {
                case TokenType.Number:
                case TokenType.Variable:
                    output.Add(token);
                    break;

                case TokenType.Function:
                    opStack.Push(token);
                    break;

                case TokenType.Comma:
                    while (opStack.Count > 0 && opStack.Peek().Type != TokenType.LeftParen)
                        output.Add(opStack.Pop());
                    break;

                case TokenType.Operator:
                    while (opStack.Count > 0
                           && opStack.Peek().Type == TokenType.Operator
                           && Precedence(opStack.Peek().Name) >= Precedence(token.Name))
                    {
                        output.Add(opStack.Pop());
                    }
                    opStack.Push(token);
                    break;

                case TokenType.LeftParen:
                    opStack.Push(token);
                    break;

                case TokenType.RightParen:
                    while (opStack.Count > 0 && opStack.Peek().Type != TokenType.LeftParen)
                        output.Add(opStack.Pop());
                    if (opStack.Count > 0) opStack.Pop(); // pop '('
                    if (opStack.Count > 0 && opStack.Peek().Type == TokenType.Function)
                        output.Add(opStack.Pop());
                    break;
            }
        }

        while (opStack.Count > 0)
            output.Add(opStack.Pop());

        return output;
    }

    #endregion
}
