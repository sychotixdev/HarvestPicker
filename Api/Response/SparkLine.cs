using Newtonsoft.Json;

namespace HarvestPicker.Api.Response;

public class SparkLine
{
    [JsonProperty( NullValueHandling = NullValueHandling.Ignore )]
    public float?[] Data { get; set; }
    public double TotalChange { get; set; }
}