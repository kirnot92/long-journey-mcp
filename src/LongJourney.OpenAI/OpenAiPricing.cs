using LongJourney.Core;

namespace LongJourney.OpenAI;

/// <summary>Standard API pricing; cached reads and cache writes are disjoint input categories.</summary>
public static class OpenAiPricing
{
    public static decimal Calculate(ModelOptions model, long input, long cached, long writes, long output)
    {
        if (input < 0 || cached < 0 || writes < 0 || output < 0 || cached > input || writes > input - cached)
        {
            throw new InvalidDataException("OpenAI returned inconsistent token usage.");
        }

        var isLongContext = input > model.LongContextThresholdTokens;
        var inputMultiplier = isLongContext ? model.LongContextInputMultiplier : 1m;
        var outputMultiplier = isLongContext ? model.LongContextOutputMultiplier : 1m;
        var uncachedInputTokens = input - cached - writes;
        var uncachedInputCost = uncachedInputTokens * model.InputUsdPerMillion * inputMultiplier;
        var cachedInputCost = cached * model.CachedInputUsdPerMillion * inputMultiplier;
        var cacheWriteCost = writes * model.CacheWriteUsdPerMillion * inputMultiplier;
        var outputCost = output * model.OutputUsdPerMillion * outputMultiplier;
        return (uncachedInputCost + cachedInputCost + cacheWriteCost + outputCost) / 1_000_000m;
    }

    public static decimal Reserve(ModelOptions model, long maximumInputTokens)
    {
        var maximumInputPrice = Math.Max(model.InputUsdPerMillion,
            Math.Max(model.CachedInputUsdPerMillion, model.CacheWriteUsdPerMillion));
        var isLongContext = maximumInputTokens > model.LongContextThresholdTokens;
        var inputMultiplier = isLongContext ? model.LongContextInputMultiplier : 1m;
        var outputMultiplier = isLongContext ? model.LongContextOutputMultiplier : 1m;
        var maximumInputCost = maximumInputTokens * maximumInputPrice * inputMultiplier;
        var maximumOutputCost = model.MaxOutputTokens * model.OutputUsdPerMillion * outputMultiplier;
        return (maximumInputCost + maximumOutputCost) / 1_000_000m;
    }
}
