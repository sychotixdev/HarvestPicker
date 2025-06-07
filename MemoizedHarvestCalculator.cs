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

        // Add these fields to your class to track statistics
        private int _totalRecursiveCalls = 0;
        private int _totalPruningOccurred = 0;
        private Dictionary<int, int> _callsByDepth = new Dictionary<int, int>();

        private bool disableOptimizations = false;

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
    double chanceToNotWither,
    int depth = 0) // Add depth parameter
        {
            _totalRecursiveCalls++;

            // Track calls by depth
            if (!_callsByDepth.ContainsKey(depth))
                _callsByDepth[depth] = 0;
            _callsByDepth[depth]++;

            if (!remaining.Any())
            {
                _harvestPicker.Log($"Depth {depth}: Reached base case (no remaining crops)");
                return (0, new List<Entity>());
            }

            string cacheKey = CreateCacheKey(remaining);
            if (_memoCache.TryGetValue(cacheKey, out var cachedResult))
            {
                _cacheHits++;
                _harvestPicker.Log($"Depth {depth}: Cache hit for {remaining.Count} remaining crops");
                return cachedResult;
            }
            _cacheMisses++;

            _harvestPicker.Log($"Depth {depth}: Exploring {remaining.Count} remaining crops, trying {remaining.Count} choices");

            double bestValue = double.NegativeInfinity;
            List<Entity> bestSequence = null;
            int choicesTried = 0;
            int choicesPruned = 0;

            for (int i = 0; i < remaining.Count; i++)
            {
                var (chosenData, chosenEntity) = remaining[i];
                var valueChosen = _harvestPicker.CalculateIrrigatorValue(chosenData);
                var baseRemaining = remaining.Where((_, idx) => idx != i).ToList();
                Entity paired = _pairLookup.TryGetValue(chosenEntity, out var pairEntity) ? pairEntity : null;
                int pairIndex = baseRemaining.FindIndex(x => x.Entity == paired);
                bool hasPaired = pairIndex >= 0;

                _harvestPicker.Log($"Depth {depth}: Trying choice {i + 1}/{remaining.Count} - Entity {chosenEntity.Address}, Value: {valueChosen:F1}, HasPair: {hasPaired}");

                string surviveKey = null;
                double surviveValue = 0;
                List<Entity> surviveSequence = new List<Entity>();

                // --- Survival branch: pair survives, stays in place ---
                if (hasPaired && chanceToNotWither > 0)
                {
                    var survivingPair = baseRemaining[pairIndex];
                    var survivedRemaining = baseRemaining
                        .Select(x =>
                        {
                            var upgraded = x.Data.Type == chosenData.Type
                                ? x.Data
                                : _upgradeFunc(x.Data, chosenData.Type);
                            return (upgraded, x.Entity);
                        })
                        .ToList();

                    surviveKey = CreateCacheKey(survivedRemaining);

                    if (_memoCache.TryGetValue(surviveKey, out var surviveMemo))
                    {
                        surviveSequence = surviveMemo.Sequence;
                        surviveValue = surviveMemo.Value;
                    }
                    else
                    {
                        _harvestPicker.Log($"Depth {depth}: Survival branch has {survivedRemaining.Count} remaining crops");
                        (surviveValue, surviveSequence) = CalculateBestHarvestSequence(survivedRemaining, chanceToNotWither, depth + 1);

                        // Cache the survival result
                        if (!disableOptimizations)
                            _memoCache[surviveKey] = (surviveValue, surviveSequence);
                    }

                    // If we go down the survival branch and STILL can't beat our best, prune this branch
                    if ((valueChosen + surviveValue) <= bestValue)
                    {
                        choicesPruned++;
                        _totalPruningOccurred++;
                        _harvestPicker.Log($"Depth {depth}: PRUNED choice {i + 1} - Best case survial possible: {valueChosen + surviveValue:F1} <= Best so far: {bestValue:F1}");
                        if (!disableOptimizations)
                            continue; // prune
                    }
                }

                // --- Wilted branch: pair is removed ---
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

                _harvestPicker.Log($"Depth {depth}: Wilted branch has {wiltedRemaining.Count} remaining crops");
                var (wiltValue, wiltSequence) = CalculateBestHarvestSequence(wiltedRemaining, chanceToNotWither, depth + 1);

                double wiltContribution = (1 - chanceToNotWither) * wiltValue;
                double surviveContribution = hasPaired ? chanceToNotWither * surviveValue : 0;
                double totalExpected = valueChosen + wiltContribution + surviveContribution;

                // Choose the sequence from the branch that contributes more
                List<Entity> sequenceToUse = new List<Entity> { chosenEntity };
                if (hasPaired && chanceToNotWither > 0.0)
                {
                    sequenceToUse.AddRange(surviveSequence);
                }
                else
                {
                    // Use wilted sequence
                    sequenceToUse.AddRange(wiltSequence);
                }

                if (totalExpected > bestValue)
                {
                    bestValue = totalExpected;
                    bestSequence = sequenceToUse;
                    _harvestPicker.Log($"Depth {depth}: NEW BEST from choice {i + 1} (no pair): {bestValue:F1}");
                }

                choicesTried++;
            }

            _harvestPicker.Log($"Depth {depth}: Completed - Tried {choicesTried}/{remaining.Count} choices, Pruned: {choicesPruned}, Best: {bestValue:F1}");

            var result = (bestValue, bestSequence);
            if (!disableOptimizations)
                _memoCache[cacheKey] = result;
            return result;
        }

        // Add this method to log comprehensive statistics
        public void LogDetailedStats(Action<string> logAction)
        {
            logAction($"=== ALGORITHM STATISTICS ===");
            logAction($"Total recursive calls: {_totalRecursiveCalls}");
            logAction($"Total pruning events: {_totalPruningOccurred}");
            logAction($"Cache hits: {_cacheHits}");
            logAction($"Cache misses: {_cacheMisses}");
            logAction($"Cache hit rate: {(double)_cacheHits / (_cacheHits + _cacheMisses) * 100:F1}%");

            logAction($"Calls by depth:");
            foreach (var kvp in _callsByDepth.OrderBy(x => x.Key))
            {
                logAction($"  Depth {kvp.Key}: {kvp.Value} calls");
            }

            // Reset for next run
            _totalRecursiveCalls = 0;
            _totalPruningOccurred = 0;
            _callsByDepth.Clear();
        }

        private string CreateCacheKey(List<(SeedData Data, Entity Entity)> remaining)
        {
            Span<(int EntityHash, int Type, int T1, int T2, int T3, int T4)> values = stackalloc (int, int, int, int, int, int)[remaining.Count];

            for (int i = 0; i < remaining.Count; i++)
            {
                var (data, entity) = remaining[i];
                values[i] = (
                    entity.GetHashCode(),
                    data.Type,
                    (int)(data.T1Plants * 1000),
                    (int)(data.T2Plants * 1000),
                    (int)(data.T3Plants * 1000),
                    (int)(data.T4Plants * 1000)
                );
            }

            values.Sort((a, b) => a.EntityHash.CompareTo(b.EntityHash));

            var hash = new HashCode();
            foreach (var v in values)
            {
                hash.Add(v.EntityHash);
                hash.Add(v.Type);
                hash.Add(v.T1);
                hash.Add(v.T2);
                hash.Add(v.T3);
                hash.Add(v.T4);
            }

            return hash.ToHashCode().ToString();
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
