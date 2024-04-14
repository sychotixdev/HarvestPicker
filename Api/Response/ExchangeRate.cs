using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;

namespace HarvestPicker.Api.Response;

public class ExchangeRate
{
    public int Id { get; set; }

    [JsonProperty(PropertyName = "league_id")]
    public string LeagueId { get; set; }

    [JsonProperty(PropertyName = "pay_currency_id")]
    public int PayCurrencyId { get; set; }

    [JsonProperty(PropertyName = "get_currency_id")]
    public int GetCurrencyId { get; set; }

    [JsonProperty(PropertyName = "sample_time_utc", ItemConverterType = typeof(IsoDateTimeConverter))]
    public DateTime SampleTimeUTC { get; set; }

    public int Count { get; set; }

    public double Value { get; set; }

    [JsonProperty(PropertyName = "data_point_count")]
    public int DataPointCount { get; set; }

    [JsonProperty(PropertyName = "includes_secondary")]
    public bool IncludesSecondary { get; set; }

    [JsonProperty(PropertyName = "listing_count")]
    public int ListingCount { get; set; }
}