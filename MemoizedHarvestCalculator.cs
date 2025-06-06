using ExileCore.PoEMemory.MemoryObjects;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HarvestPicker
{
    public class MemoizedHarvestCalculator
    {
        private readonly Dictionary<string, (double Value, List<Entity> Sequence)> _memoCache = new();
        private readonly Func<SeedData, int, SeedData> _upgradeFunc;
        private readonly Dictionary<Entity, Entity> _pairLookup;
        private readonly HarvestPicker _harvestPicker;
        private int _cacheHits = 0;
        private int _cacheMisses = 0;

        public MemoizedHarvestCalculator(Func<SeedData, int, SeedData> upgradeFunc,
                                            Dictionary<Entity, Entity> pairLookup,
                                            HarvestPicker harvestPicker)
        {
            _upgradeFunc = upgradeFunc;
            _pairLookup = pairLookup;
            _harvestPicker = harvestPicker;
        }

        public (double Value, List<Entity> Sequence) CalculateBestHarvestSequence(
    List<(SeedData Data, Entity Entity)> remaining,
    double chanceToNotWither)
        {
            if (!remaining.Any()) return (0, new List<Entity>());

            string cacheKey = CreateCacheKey(remaining);
            if (_memoCache.TryGetValue(cacheKey, out var cachedResult))
            {
                _cacheHits++;
                return cachedResult;
            }

            _cacheMisses++;

            double bestValue = double.NegativeInfinity;
            List<Entity> bestSequence = null;

            for (int i = 0; i < remaining.Count; i++)
            {
                var (chosenData, chosenEntity) = remaining[i];
                var valueChosen = _harvestPicker.CalculateIrrigatorValue(chosenData);
                var baseRemaining = remaining.Where((_, idx) => idx != i).ToList();

                Entity paired = _pairLookup.TryGetValue(chosenEntity, out var pairEntity) ? pairEntity : null;
                int pairIndex = baseRemaining.FindIndex(x => x.Entity == paired);
                bool hasPaired = pairIndex >= 0;

                // Wilted branch
                var wiltedRemaining = baseRemaining
                    .Where(x => !hasPaired || x.Entity != paired)
                    .Select(x =>
                    {
                        var upgraded = x.Data.Type == chosenData.Type
                            ? x.Data
                            : _upgradeFunc(x.Data, chosenData.Type);
                        return (upgraded, x.Entity);
                    })
                    .ToList();

                var (wiltValue, wiltSequence) = CalculateBestHarvestSequence(wiltedRemaining, chanceToNotWither);
                double expectedWilt = valueChosen + wiltValue;

                double totalExpected = (1 - chanceToNotWither) * expectedWilt;
                List<Entity> sequenceToUse = new List<Entity> { chosenEntity };
                sequenceToUse.AddRange(wiltSequence);

                // Survival branch
                if (hasPaired && chanceToNotWither > 0)
                {
                    var survivingPair = baseRemaining[pairIndex];

                    var survivedRemaining = baseRemaining
                        .Where((_, idx) => idx != pairIndex)
                        .Select(x =>
                        {
                            var upgraded = x.Data.Type == chosenData.Type
                                ? x.Data
                                : _upgradeFunc(x.Data, chosenData.Type);
                            return (upgraded, x.Entity);
                        })
                        .ToList();

                    // Apply upgrade to the surviving paired crop
                    var upgradedSurvivingPair = survivingPair.Data.Type == chosenData.Type
                        ? survivingPair.Data
                        : _upgradeFunc(survivingPair.Data, chosenData.Type);
                    survivedRemaining.Add((upgradedSurvivingPair, survivingPair.Entity));

                    string surviveKey = CreateCacheKey(survivedRemaining);
                    if (_memoCache.TryGetValue(surviveKey, out var surviveMemo))
                    {
                        double possibleSurviveTotal = valueChosen + surviveMemo.Value;
                        if (possibleSurviveTotal < bestValue)
                            continue; // prune
                    }

                    var (surviveValue, surviveSequence) = CalculateBestHarvestSequence(survivedRemaining, chanceToNotWither);
                    double expectedSurvive = valueChosen + surviveValue;

                    double combinedExpected = totalExpected + (chanceToNotWither * expectedSurvive);
                    if (combinedExpected > bestValue)
                    {
                        bestValue = combinedExpected;
                        bestSequence = new List<Entity> { chosenEntity };
                        bestSequence.AddRange(surviveSequence);
                    }
                }
                else if (totalExpected > bestValue)
                {
                    bestValue = totalExpected;
                    bestSequence = sequenceToUse;
                }
            }

            var result = (bestValue, bestSequence);
            _memoCache[cacheKey] = result;
            return result;
        }

        private string CreateCacheKey(List<(SeedData Data, Entity Entity)> remaining)
        {
            var sortedData = remaining
                .Select(x => new {
                    EntityHash = x.Entity.GetHashCode(),
                    Type = x.Data.Type,
                    T1 = Math.Round(x.Data.T1Plants, 3),
                    T2 = Math.Round(x.Data.T2Plants, 3),
                    T3 = Math.Round(x.Data.T3Plants, 3),
                    T4 = Math.Round(x.Data.T4Plants, 3)
                })
                .OrderBy(x => x.EntityHash)
                .ToList();

            return JsonConvert.SerializeObject(sortedData);
        }

        public void ClearCache()
        {
            _memoCache.Clear();
            _cacheHits = 0;
            _cacheMisses = 0;
        }

        public void LogCacheStats(Action<string> logger)
        {
            logger($"Cache stats - Hits: {_cacheHits}, Misses: {_cacheMisses}, Size: {_memoCache.Count}");
        }
    }
}
