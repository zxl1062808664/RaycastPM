using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RaycastPM.Models;

namespace RaycastPM.Services;

public static partial class CurrencyConverter
{
    public static CurrencyResult? Convert(string query, IReadOnlyDictionary<string, double> ratesToCny)
    {
        var match = CurrencyPattern().Match(query);
        if (!match.Success)
        {
            return null;
        }

        var rawAmount = match.Groups["amount1"].Success ? match.Groups["amount1"].Value : match.Groups["amount2"].Value;
        var rawCode = match.Groups["code1"].Success ? match.Groups["code1"].Value : match.Groups["code2"].Value;

        if (!double.TryParse(rawAmount, out var amount))
        {
            return null;
        }

        var code = CurrencyCode(rawCode);
        if (code is null || !ratesToCny.TryGetValue(code, out var rate))
        {
            return null;
        }

        return new CurrencyResult(amount, amount * rate, DisplayName(code), code);
    }

    public static async Task<Dictionary<string, double>?> FetchRatesToCnyAsync()
    {
        using var client = new HttpClient();
        var snapshot = await client.GetFromJsonAsync<ExchangeRatesResponse>("https://open.er-api.com/v6/latest/CNY");
        if (snapshot?.Rates is null)
        {
            return null;
        }

        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["CNY"] = 1
        };

        foreach (var pair in snapshot.Rates)
        {
            var code = pair.Key;
            var rateFromCny = pair.Value;
            if (rateFromCny > 0)
            {
                result[code.ToUpperInvariant()] = 1 / rateFromCny;
            }
        }

        return result;
    }

    private static string? CurrencyCode(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length == 3 && normalized.All(char.IsAsciiLetter))
        {
            return normalized;
        }

        return normalized switch
        {
            "美元" or "美金" or "刀" or "$" => "USD",
            "人民币" or "¥" or "￥" => "CNY",
            "日元" or "日币" => "JPY",
            "欧元" or "€" => "EUR",
            "英镑" or "£" => "GBP",
            "港币" or "港元" => "HKD",
            "澳元" => "AUD",
            "加元" => "CAD",
            "韩元" or "₩" => "KRW",
            "台币" or "新台币" => "TWD",
            "新加坡元" or "新币" => "SGD",
            "泰铢" or "฿" => "THB",
            "马币" or "林吉特" => "MYR",
            "卢布" or "₽" => "RUB",
            "瑞郎" or "瑞士法郎" => "CHF",
            "印度卢比" => "INR",
            "越南盾" => "VND",
            _ => null
        };
    }

    private static string DisplayName(string code)
    {
        return code.ToUpperInvariant() switch
        {
            "USD" => "美元",
            "CNY" => "人民币",
            "JPY" => "日元",
            "EUR" => "欧元",
            "GBP" => "英镑",
            "HKD" => "港币",
            "AUD" => "澳元",
            "CAD" => "加元",
            "KRW" => "韩元",
            "TWD" => "新台币",
            "SGD" => "新加坡元",
            "THB" => "泰铢",
            "MYR" => "马币",
            "RUB" => "卢布",
            "CHF" => "瑞郎",
            "INR" => "印度卢比",
            "VND" => "越南盾",
            _ => code
        };
    }

    [GeneratedRegex(@"(?ix)^\s*(?:(?<code1>[A-Z]{3}|美元|美金|刀|人民币|日元|日币|欧元|英镑|港币|港元|澳元|加元|韩元|台币|新台币|新加坡元|新币|泰铢|马币|林吉特|卢布|瑞郎|瑞士法郎|印度卢比|越南盾|¥|￥|\$|€|£|₩|₽|฿)\s*(?<amount1>[0-9]+(?:\.[0-9]+)?)|(?<amount2>[0-9]+(?:\.[0-9]+)?)\s*(?<code2>[A-Z]{3}|美元|美金|刀|人民币|日元|日币|欧元|英镑|港币|港元|澳元|加元|韩元|台币|新台币|新加坡元|新币|泰铢|马币|林吉特|卢布|瑞郎|瑞士法郎|印度卢比|越南盾|¥|￥|\$|€|£|₩|₽|฿))\s*$")]
    private static partial Regex CurrencyPattern();

    private sealed class ExchangeRatesResponse
    {
        [JsonPropertyName("rates")]
        public Dictionary<string, double>? Rates { get; set; }
    }
}
