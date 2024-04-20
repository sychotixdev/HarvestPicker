using System.Collections.Generic;
using Newtonsoft.Json;

namespace HarvestPicker.Api.Response;

public class PoeNinjaCurrencyResponse
{
    public record SearchResult(double ChaosEquivalent, string DetailsId);

    [JsonProperty]
    public List<Line> Lines { get; set; }

    [JsonProperty("currencyDetails", NullValueHandling = NullValueHandling.Ignore)]
    public List<CurrencyDetail> CurrencyDetails { get; set; }

    public SearchResult FindLine(Line line)
    {
        if (line == null)
        {
            return null;
        }

        var value = line.ChaosEquivalent;
        if (line.Receive?.Value is { } receiveValue &&
            CurrencyDetails.Find(x => x.Id == line.Receive.PayCurrencyId)?.TradeId == "chaos")
        {
            value = receiveValue;
        }

        return new SearchResult(value ?? 0, line.DetailsId);
    }
}