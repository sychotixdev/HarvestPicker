using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using HarvestPicker.Api.Response;
using Newtonsoft.Json;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using static MoreLinq.Extensions.PermutationsExtension;
using System.Web;
using ExileCore.Shared.Helpers;

namespace HarvestPicker;

public class HarvestPrices
{
    public double YellowJuiceValue;
    public double PurpleJuiceValue;
    public double BlueJuiceValue;
    public double WhiteJuiceValue;
}

public class HarvestPicker : BaseSettingsPlugin<HarvestPickerSettings>
{
    public override bool Initialise()
    {
        _pricesGetter = LoadPricesFromDisk(false);
        Settings.ReloadPrices.OnPressed = () => { _pricesGetter = LoadPricesFromDisk(true); };
        return true;
    }

    private readonly Stopwatch _lastRetrieveStopwatch = new Stopwatch();
    private Task _pricesGetter;
    private HarvestPrices _prices;
    private List<((Entity, double), (Entity, double))> _irrigatorPairs;
    private List<Entity> _cropRotationPath;
    private double _cropRotationValue;
    private HashSet<(SeedData, SeedData)> _lastSeedData;
    private string CachePath => Path.Join(ConfigDirectory, "pricecache.json");

    public override void AreaChange(AreaInstance area)
    {
        _lastSeedData = null;
        _cropRotationPath = null;
        _cropRotationValue = 0;
        _irrigatorPairs = [];
        Settings.League.Values = (Settings.League.Values ?? []).Union([PlayerLeague, "Standard", "Hardcore"]).Where(x => x != null).ToList();
    }

    private string PlayerLeague
    {
        get
        {
            var playerLeague = GameController.IngameState.ServerData.League;
            if (string.IsNullOrWhiteSpace(playerLeague))
            {
                playerLeague = null;
            }
            else
            {
                if (playerLeague.StartsWith("SSF "))
                {
                    playerLeague = playerLeague["SSF ".Length..];
                }
            }

            return playerLeague;
        }
    }

    private HarvestPrices Prices
    {
        get
        {
            if (_pricesGetter is { IsCompleted: true })
            {
                _pricesGetter = null;
            }

            if ((!_lastRetrieveStopwatch.IsRunning ||
                 _lastRetrieveStopwatch.Elapsed >= TimeSpan.FromMinutes(Settings.PriceRefreshPeriodMinutes)) &&
                _pricesGetter == null)
            {
                _pricesGetter = FetchPrices();
                _lastRetrieveStopwatch.Reset();
            }

            return _prices;
        }
    }

    private async Task FetchPrices()
    {
        await Task.Yield();
        try
        {
            Log("Starting data update");
            using var client = new HttpClient();

            var query = HttpUtility.ParseQueryString("");
            query["league"] = Settings.League.Value;
            query["type"] = "Currency";

            UriBuilder builder = new UriBuilder();
            builder.Path = "/api/data/currencyoverview";
            builder.Host = "poe.ninja";
            builder.Scheme = "https";
            builder.Query = query.ToString();

            string uri = builder.ToString();

            var request = client.GetAsync(uri);
            Log($"Fetching data from poe.ninja with url: {uri}");

            var response = await request;
            response.EnsureSuccessStatusCode();

            var str = await response.Content.ReadAsStringAsync();
            var responseObject = JsonConvert.DeserializeObject<PoeNinjaCurrencyResponse>(str);

            var dataMap = responseObject.Lines.ToDictionary(x => x.CurrencyTypeName, x => responseObject.FindLine(x)?.ChaosEquivalent);
            if (dataMap.Any(x => x.Value is 0 or null) || dataMap.Count < 4)
            {
                Log($"Some data is missing: {str}");
            }

            _prices = new HarvestPrices
            {
                BlueJuiceValue = dataMap.GetValueOrDefault("Primal Crystallised Lifeforce") ?? 0,
                YellowJuiceValue = dataMap.GetValueOrDefault("Vivid Crystallised Lifeforce") ?? 0,
                PurpleJuiceValue = dataMap.GetValueOrDefault("Wild Crystallised Lifeforce") ?? 0,
                WhiteJuiceValue = dataMap.GetValueOrDefault("Sacred Crystallised Lifeforce") ?? 0,
            };
            await File.WriteAllTextAsync(CachePath, JsonConvert.SerializeObject(_prices));
            Log("Data update complete");
        }
        catch (Exception ex)
        {
            DebugWindow.LogError(ex.ToString());
        }
        finally
        {
            _lastRetrieveStopwatch.Restart();
        }
    }

    private async Task LoadPricesFromDisk(bool force)
    {
        await Task.Yield();
        try
        {
            Log("Loading data from disk");
            var cachePath = CachePath;
            if (File.Exists(cachePath))
            {
                _prices = JsonConvert.DeserializeObject<HarvestPrices>(await File.ReadAllTextAsync(cachePath));
                Log("Data loaded from disk");
                if (force)
                {
                    _lastRetrieveStopwatch.Reset();
                }
                else
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < TimeSpan.FromMinutes(Settings.PriceRefreshPeriodMinutes))
                    {
                        _lastRetrieveStopwatch.Restart();
                    }
                }
            }
            else
            {
                Log("Cached data doesn't exist");
                _lastRetrieveStopwatch.Reset();
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogError(ex.ToString());
        }
    }


    private void Log(string message)
    {
        LogMessage($"[HarvestPicker] {message}");
    }

    public override Job Tick()
    {
        _irrigatorPairs = new List<((Entity, double), (Entity, double))>();

        var irrigators = GameController.EntityListWrapper.ValidEntitiesByType[EntityType.MiscellaneousObjects]
            .Where(x => x.Path == "Metadata/MiscellaneousObjects/Harvest/Extractor" &&
                        x.TryGetComponent<StateMachine>(out var stateMachine) &&
                        stateMachine.States.FirstOrDefault(s => s.Name == "current_state")?.Value == 0).ToList();
        var irrigatorPairs = new List<((Entity, double), (Entity, double))>();
        while (irrigators.LastOrDefault() is { } entity1)
        {
            irrigators.RemoveAt(irrigators.Count - 1);
            var closestIrrigator = irrigators.MinBy(x => x.Distance(entity1));
            if (closestIrrigator == null || closestIrrigator.Distance(entity1) > 85)
            {
                irrigatorPairs.Add(((entity1, CalculateIrrigatorValue(entity1)), (default, default)));
            }
            else
            {
                irrigators.Remove(closestIrrigator);
                irrigatorPairs.Add(((entity1, CalculateIrrigatorValue(entity1)), (closestIrrigator, CalculateIrrigatorValue(closestIrrigator))));
            }
        }

        _irrigatorPairs = irrigatorPairs;

        if (GameController.IngameState.Data.MapStats.GetValueOrDefault(
                GameStat.MapHarvestSeedsOfOtherColoursHaveChanceToUpgradeOnCompletingPlot) != 0)
        {
            //crop rotation with weighted risk calculation
            List<((SeedData Data, Entity Entity) Plot1, (SeedData Data, Entity Entity) Plot2)> irrigatorSeedDataPairs =
                irrigatorPairs.Select(p => (
                        (ExtractSeedData(p.Item1.Item1), p.Item1.Item1),
                        (p.Item2.Item1 != null ? ExtractSeedData(p.Item2.Item1) : null, p.Item2.Item1)))
                    .ToList();
            var currentSet = irrigatorSeedDataPairs.Select(x => (x.Plot1.Data, x.Plot2.Data)).ToHashSet();
            if (_lastSeedData == null || !_lastSeedData.SetEquals(currentSet))
            {
                _cropRotationPath = null;
                _cropRotationValue = 0;

                SeedData Upgrade(SeedData source, int type) => source == null || type == source.Type
                    ? source
                    : new SeedData(source.Type,
                        source.T1Plants * (1 - Settings.CropRotationT1UpgradeChance),
                        source.T2Plants * (1 - Settings.CropRotationT2UpgradeChance) + source.T1Plants * Settings.CropRotationT1UpgradeChance,
                        source.T3Plants * (1 - Settings.CropRotationT3UpgradeChance) + source.T2Plants * Settings.CropRotationT2UpgradeChance,
                        source.T4Plants + source.T3Plants * Settings.CropRotationT3UpgradeChance);

                double maxExpectedValue = double.NegativeInfinity;
                List<Entity> selectedPath = null;

                foreach (var pairPermutation in irrigatorSeedDataPairs.Permutations())
                {
                    var expectedValue = CalculateExpectedValueForPath(pairPermutation.ToList(), Upgrade);
                    if (expectedValue > maxExpectedValue)
                    {
                        maxExpectedValue = expectedValue;
                        selectedPath = GetOptimalChoicesForPath(pairPermutation.ToList(), Upgrade);
                    }
                }

                _cropRotationPath = selectedPath;
                _cropRotationValue = maxExpectedValue;
                _lastSeedData = currentSet;
            }
        }

        return null;
    }

    /// <summary>
    /// Calculate the expected value for a given path considering the probability of crops not wilting
    /// </summary>
    private double CalculateExpectedValueForPath(
        List<((SeedData Data, Entity Entity) Plot1, (SeedData Data, Entity Entity) Plot2)> path,
        Func<SeedData, int, SeedData> upgradeFunc)
    {
        return CalculateExpectedValueRecursive(path, 0, upgradeFunc);
    }

    /// <summary>
    /// Recursively calculate expected value considering all possible outcomes
    /// </summary>
    private double CalculateExpectedValueRecursive(
        List<((SeedData Data, Entity Entity) Plot1, (SeedData Data, Entity Entity) Plot2)> remainingPath,
        int currentIndex,
        Func<SeedData, int, SeedData> upgradeFunc)
    {
        if (currentIndex >= remainingPath.Count)
            return 0;

        var currentPair = remainingPath[currentIndex];

        // If there's only one plot in this pair, we must choose it
        if (currentPair.Plot2.Data == null)
        {
            var chosenValue = CalculateIrrigatorValue(currentPair.Plot1.Data);
            var upgradedPath = ApplyUpgradeToRemainingPath(remainingPath, currentIndex + 1, currentPair.Plot1.Data.Type, upgradeFunc);
            var futureValue = CalculateExpectedValueRecursive(upgradedPath, currentIndex + 1, upgradeFunc);
            return chosenValue + futureValue;
        }

        // Calculate values for both choices
        var plot1Value = CalculateIrrigatorValue(currentPair.Plot1.Data);
        var plot2Value = CalculateIrrigatorValue(currentPair.Plot2.Data);

        // Calculate expected values for choosing plot1
        var upgradedPath1 = ApplyUpgradeToRemainingPath(remainingPath, currentIndex + 1, currentPair.Plot1.Data.Type, upgradeFunc);
        var futureValue1Normal = CalculateExpectedValueRecursive(upgradedPath1, currentIndex + 1, upgradeFunc);
        var futureValue1WithBonus = plot2Value + CalculateExpectedValueRecursive(upgradedPath1, currentIndex + 1, upgradeFunc);

        // Calculate expected values for choosing plot2
        var upgradedPath2 = ApplyUpgradeToRemainingPath(remainingPath, currentIndex + 1, currentPair.Plot2.Data.Type, upgradeFunc);
        var futureValue2Normal = CalculateExpectedValueRecursive(upgradedPath2, currentIndex + 1, upgradeFunc);
        var futureValue2WithBonus = plot1Value + CalculateExpectedValueRecursive(upgradedPath2, currentIndex + 1, upgradeFunc);

        // Calculate expected values considering wilt probability
        // Get chance for crops to not wilt
        var chanceToWither = (1.0 * GameController.IngameState.Data.MapStats.GetValueOrDefault(GameStat.MapHarvestChanceForOtherPlotToNotWitherPct)) / 100;
        var wiltChance = 1.0 - chanceToWither;
        var expectedValue1 = plot1Value + (wiltChance * futureValue1Normal) + (chanceToWither * futureValue1WithBonus);
        var expectedValue2 = plot2Value + (wiltChance * futureValue2Normal) + (chanceToWither * futureValue2WithBonus);

        return Math.Max(expectedValue1, expectedValue2);
    }

    /// <summary>
    /// Get the optimal choices for a given path
    /// </summary>
    private List<Entity> GetOptimalChoicesForPath(
        List<((SeedData Data, Entity Entity) Plot1, (SeedData Data, Entity Entity) Plot2)> path,
        Func<SeedData, int, SeedData> upgradeFunc)
    {
        var choices = new List<Entity>();
        var currentPath = path.ToList();

        for (int i = 0; i < path.Count; i++)
        {
            var currentPair = currentPath[i];

            if (currentPair.Plot2.Data == null)
            {
                choices.Add(currentPair.Plot1.Entity);
                continue;
            }

            // Calculate expected values for both choices
            var plot1Value = CalculateIrrigatorValue(currentPair.Plot1.Data);
            var plot2Value = CalculateIrrigatorValue(currentPair.Plot2.Data);

            var upgradedPath1 = ApplyUpgradeToRemainingPath(currentPath, i + 1, currentPair.Plot1.Data.Type, upgradeFunc);
            var futureValue1Normal = CalculateExpectedValueRecursive(upgradedPath1, i + 1, upgradeFunc);
            var futureValue1WithBonus = plot2Value + CalculateExpectedValueRecursive(upgradedPath1, i + 1, upgradeFunc);

            var upgradedPath2 = ApplyUpgradeToRemainingPath(currentPath, i + 1, currentPair.Plot2.Data.Type, upgradeFunc);
            var futureValue2Normal = CalculateExpectedValueRecursive(upgradedPath2, i + 1, upgradeFunc);
            var futureValue2WithBonus = plot1Value + CalculateExpectedValueRecursive(upgradedPath2, i + 1, upgradeFunc);

            var chanceToWither = (1.0 * GameController.IngameState.Data.MapStats.GetValueOrDefault(GameStat.MapHarvestChanceForOtherPlotToNotWitherPct)) / 100;
            var wiltChance = 1.0 - chanceToWither;
            var expectedValue1 = plot1Value + (wiltChance * futureValue1Normal) + (chanceToWither * futureValue1WithBonus);
            var expectedValue2 = plot2Value + (wiltChance * futureValue2Normal) + (chanceToWither * futureValue2WithBonus);

            if (expectedValue1 >= expectedValue2)
            {
                choices.Add(currentPair.Plot1.Entity);
                currentPath = upgradedPath1;
            }
            else
            {
                choices.Add(currentPair.Plot2.Entity);
                currentPath = upgradedPath2;
            }
        }

        return choices;
    }

    /// <summary>
    /// Apply upgrade effects to the remaining path based on the chosen crop type
    /// </summary>
    private List<((SeedData Data, Entity Entity) Plot1, (SeedData Data, Entity Entity) Plot2)> ApplyUpgradeToRemainingPath(
        List<((SeedData Data, Entity Entity) Plot1, (SeedData Data, Entity Entity) Plot2)> originalPath,
        int startIndex,
        int upgradeType,
        Func<SeedData, int, SeedData> upgradeFunc)
    {
        var upgradedPath = new List<((SeedData Data, Entity Entity) Plot1, (SeedData Data, Entity Entity) Plot2)>();

        for (int i = 0; i < originalPath.Count; i++)
        {
            if (i < startIndex)
            {
                upgradedPath.Add(originalPath[i]);
            }
            else
            {
                var originalPair = originalPath[i];
                var upgradedPair = (
                    (upgradeFunc(originalPair.Plot1.Data, upgradeType), originalPair.Plot1.Entity),
                    originalPair.Plot2.Data != null ?
                        (upgradeFunc(originalPair.Plot2.Data, upgradeType), originalPair.Plot2.Entity) :
                        (null, originalPair.Plot2.Entity)
                );
                upgradedPath.Add(upgradedPair);
            }
        }

        return upgradedPath;
    }

    private record SeedData(int Type, float T1Plants, float T2Plants, float T3Plants, float T4Plants);

    private SeedData ExtractSeedData(Entity e)
    {
        if (!e.TryGetComponent<HarvestWorldObject>(out var harvest))
        {
            Log($"Entity {e} has no harvest component");
            return new SeedData(1, 0, 0, 0, 0);
        }

        var seeds = harvest.Seeds;
        if (seeds.Any(x => x.Seed == null))
        {
            Log("Some seeds have no associated dat file");
            return new SeedData(1, 0, 0, 0, 0);
        }

        var type = seeds.GroupBy(x => x.Seed.Type).MaxBy(x => x.Count()).Key;
        var seedsByTier = seeds.ToLookup(x => x.Seed.Tier);
        return new SeedData(type,
            seedsByTier[1].Sum(x => x.Count),
            seedsByTier[2].Sum(x => x.Count),
            seedsByTier[3].Sum(x => x.Count),
            seedsByTier[4].Sum(x => x.Count));
    }

    private double CalculateIrrigatorValue(SeedData data)
    {
        var prices = Prices;
        if (prices == null)
        {
            LogMessage("Prices are still not loaded, unable to calculate values");
            return 0;
        }

        var typeToPrice = data.Type switch
        {
            1 => prices.PurpleJuiceValue,
            2 => prices.YellowJuiceValue,
            3 => prices.BlueJuiceValue,
            _ => LogWrongType(data.Type),
        };
        return Settings.SeedsPerT1Plant * typeToPrice * data.T1Plants +
               Settings.SeedsPerT2Plant * typeToPrice * data.T2Plants +
               Settings.SeedsPerT3Plant * typeToPrice * data.T3Plants +
               (Settings.SeedsPerT4Plant * typeToPrice + Settings.T4PlantWhiteSeedChance * prices.WhiteJuiceValue) * data.T4Plants;
    }

    private double CalculateIrrigatorValue(Entity e)
    {
        var prices = Prices;
        if (prices == null)
        {
            LogMessage("Prices are still not loaded, unable to calculate values");
            return 0;
        }

        if (!e.TryGetComponent<HarvestWorldObject>(out var harvest))
        {
            Log($"Entity {e} has no harvest component");
            return 0;
        }

        double TypeToPrice(int type) => type switch
        {
            1 => prices.PurpleJuiceValue,
            2 => prices.YellowJuiceValue,
            3 => prices.BlueJuiceValue,
            _ => LogWrongType(type),
        };

        var seeds = harvest.Seeds;
        if (seeds.Exists(x => x.Seed == null))
        {
            Log("Some seeds have no associated dat file");
            return 0;
        }

        return seeds.Sum(seed => seed.Seed.Tier switch
        {
            1 => Settings.SeedsPerT1Plant * TypeToPrice(seed.Seed.Type),
            2 => Settings.SeedsPerT2Plant * TypeToPrice(seed.Seed.Type),
            3 => Settings.SeedsPerT3Plant * TypeToPrice(seed.Seed.Type),
            4 => Settings.SeedsPerT4Plant * TypeToPrice(seed.Seed.Type) + Settings.T4PlantWhiteSeedChance * prices.WhiteJuiceValue,
            var tier => LogWrongTier(tier),
        } * seed.Count);
    }

    private double LogWrongType(int type)
    {
        Log($"Seed had unknown type {type}");
        return 0;
    }

    private double LogWrongTier(int tier)
    {
        Log($"Seed had unknown tier {tier}");
        return 0;
    }

    public void DrawIrrigatorValue(Entity e, double value, Color color)
    {
        var text = $"Value: {value:F1}";
        var textPosition = GameController.IngameState.Camera.WorldToScreen(e.PosNum);
        Graphics.DrawBox(textPosition, textPosition + Graphics.MeasureText(text), Color.Black);
        Graphics.DrawText(text, textPosition, color);
    }

    public override void Render()
    {
        foreach (var ((irrigator1, value1), (irrigator2, value2)) in _irrigatorPairs)
        {
            if (irrigator2 == null)
            {
                DrawIrrigatorValue(irrigator1, value1, Settings.NeutralColor);
            }
            else
            {
                var (color1, color2) = value1.CompareTo(value2) switch
                {
                    > 0 => (Settings.GoodColor, Settings.BadColor),
                    0 => (Settings.NeutralColor, Settings.NeutralColor),
                    < 0 => (Settings.BadColor, Settings.GoodColor),
                };
                DrawIrrigatorValue(irrigator1, value1, color1);
                DrawIrrigatorValue(irrigator2, value2, color2);
            }
        }

        void DrawIndex(Entity entity, Color color, int size, string text)
        {
            if (GameController.IngameState.IngameUi.Map.LargeMap.IsVisibleLocal)
            {

                var mapPos = GameController.IngameState.Data.GetGridMapScreenPosition(entity.PosNum.WorldToGrid());
                mapPos = mapPos + new Vector2(0, 10);
                if (text != null)
                {
                    var widthPadding = 3;
                    var boxOffset = Graphics.MeasureText(text) / 2f;
                    var textOffset = boxOffset;

                    boxOffset.X += widthPadding;

                    Graphics.DrawBox(mapPos - boxOffset, mapPos + boxOffset, Color.Black);
                    Graphics.DrawText(text, mapPos - textOffset, color);
                }
                else
                {
                    Graphics.DrawBox(new RectangleF(mapPos.X - size / 2, mapPos.Y - size / 2, size, size), color, 1f);
                }

            }
        }

        if (_cropRotationPath is { } path)
        {
            for (int i = 0; i < path.Count; i++)
            {
                var entity = path[i];
                var text = $"CR: this is your choice index {i}. Total EV: {_cropRotationValue:F1}";
                var textPosition = GameController.IngameState.Camera.WorldToScreen(entity.PosNum) + new Vector2(0, Graphics.MeasureText("V").Y);
                Graphics.DrawBox(textPosition, textPosition + Graphics.MeasureText(text), Color.Black);
                Graphics.DrawText(text, textPosition, i == 0 ? Settings.GoodColor : Settings.NeutralColor);

                DrawIndex(entity, i == 0 ? Settings.GoodColor : Settings.NeutralColor, 12, i.ToString());
            }
        }

    }

}
