using System.Globalization;

namespace RaycastPM.Services;

public static class ExpressionEvaluator
{
    public static bool LooksLikeCalculation(string input)
    {
        var normalized = Normalize(input);
        if (string.IsNullOrWhiteSpace(normalized) || !normalized.Any(char.IsDigit))
        {
            return false;
        }

        if (normalized.Any(ch => !IsAllowedCalculationCharacter(ch)))
        {
            return false;
        }

        return ContainsCalculationOperator(normalized) && Evaluate(normalized) is not null;
    }

    public static double? Evaluate(string input)
    {
        var normalized = Normalize(input);

        var parser = new Parser(normalized);
        var value = parser.ParseExpression();
        parser.SkipWhitespace();
        return value.HasValue && parser.IsAtEnd && double.IsFinite(value.Value) ? value : null;
    }

    private static string Normalize(string input)
    {
        var chars = new List<char>(input.Length);
        foreach (var ch in input.Trim())
        {
            if (ch is ',' or '，')
            {
                continue;
            }

            chars.Add(ch switch
            {
                '×' => '*',
                '÷' => '/',
                '－' or '−' => '-',
                '＋' => '+',
                '（' => '(',
                '）' => ')',
                '％' => '%',
                '。' => '.',
                >= '０' and <= '９' => (char)('0' + ch - '０'),
                _ => ch
            });
        }

        return new string(chars.ToArray());
    }

    private static bool IsAllowedCalculationCharacter(char ch)
    {
        return char.IsDigit(ch)
            || char.IsWhiteSpace(ch)
            || ch is '.' or '+' or '-' or '*' or '/' or '^' or '%' or '(' or ')';
    }

    private static bool ContainsCalculationOperator(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '*' or '/' or '^' or '%' or '(' or ')')
            {
                return true;
            }

            if (ch is '+' or '-' && !IsUnarySign(text, i))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnarySign(string text, int index)
    {
        var previous = PreviousNonWhitespace(text, index);
        if (previous is not null && previous is not ('+' or '-' or '*' or '/' or '^' or '('))
        {
            return false;
        }

        var next = NextNonWhitespace(text, index);
        return next is not null && (char.IsDigit(next.Value) || next == '.' || next == '(');
    }

    private static char? PreviousNonWhitespace(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return text[i];
            }
        }

        return null;
    }

    private static char? NextNonWhitespace(string text, int index)
    {
        for (var i = index + 1; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return text[i];
            }
        }

        return null;
    }

    private ref struct Parser
    {
        private readonly ReadOnlySpan<char> _text;
        private int _index;

        public Parser(string text)
        {
            _text = text.AsSpan();
            _index = 0;
        }

        public bool IsAtEnd => _index >= _text.Length;

        public double? ParseExpression()
        {
            var value = ParseTerm();
            if (value is null) return null;

            while (true)
            {
                SkipWhitespace();
                var op = Peek();
                if (op is not ('+' or '-')) break;
                Advance();
                var rhs = ParseTerm();
                if (rhs is null) return null;
                value = op == '+' ? value + rhs : value - rhs;
            }

            return value;
        }

        private double? ParseTerm()
        {
            var value = ParsePower();
            if (value is null) return null;

            while (true)
            {
                SkipWhitespace();
                var op = Peek();
                if (op is not ('*' or '/')) break;
                Advance();
                var rhs = ParsePower();
                if (rhs is null) return null;
                if (op == '/')
                {
                    if (rhs == 0) return null;
                    value /= rhs;
                }
                else
                {
                    value *= rhs;
                }
            }

            return value;
        }

        private double? ParsePower()
        {
            var value = ParseFactor();
            if (value is null) return null;

            SkipWhitespace();
            if (Peek() != '^') return value;

            Advance();
            var exponent = ParsePower();
            return exponent is null ? null : Math.Pow(value.Value, exponent.Value);
        }

        private double? ParseFactor()
        {
            SkipWhitespace();
            var op = Peek();
            if (op is '+' or '-')
            {
                Advance();
                var value = ParseFactor();
                return value is null ? null : op == '-' ? -value : value;
            }

            var primary = ParsePrimary();
            return primary is null ? null : ParsePostfixPercent(primary.Value);
        }

        private double? ParsePrimary()
        {
            var number = ParseNumber();
            if (number is not null) return number;

            SkipWhitespace();
            if (Peek() != '(') return null;

            Advance();
            var value = ParseExpression();
            SkipWhitespace();
            if (Peek() != ')') return null;
            Advance();
            return value;
        }

        private double ParsePostfixPercent(double value)
        {
            while (true)
            {
                SkipWhitespace();
                if (Peek() != '%') break;
                Advance();
                value /= 100;
            }

            return value;
        }

        private double? ParseNumber()
        {
            SkipWhitespace();
            var start = _index;
            var sawDigit = false;
            var sawDot = false;

            while (!IsAtEnd)
            {
                var ch = _text[_index];
                if (char.IsDigit(ch))
                {
                    sawDigit = true;
                    Advance();
                }
                else if (ch == '.' && !sawDot)
                {
                    sawDot = true;
                    Advance();
                }
                else
                {
                    break;
                }
            }

            if (!sawDigit)
            {
                _index = start;
                return null;
            }

            return double.TryParse(_text[start.._index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        public void SkipWhitespace()
        {
            while (!IsAtEnd && char.IsWhiteSpace(_text[_index]))
            {
                Advance();
            }
        }

        private char? Peek()
        {
            return IsAtEnd ? null : _text[_index];
        }

        private void Advance()
        {
            if (!IsAtEnd) _index++;
        }
    }
}
