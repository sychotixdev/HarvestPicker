using System;
using Newtonsoft.Json;

namespace HarvestPicker.Api.Response;

public class CurrencyDetail
{
    [JsonProperty("id", NullValueHandling = NullValueHandling.Ignore)]
    public long? Id { get; set; }

    [JsonProperty("icon", NullValueHandling = NullValueHandling.Ignore)]
    public Uri Icon { get; set; }

    [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
    public string Name { get; set; }

    [JsonProperty("tradeId", NullValueHandling = NullValueHandling.Ignore)]
    public string TradeId { get; set; }
}