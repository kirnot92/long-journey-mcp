using LongJourney.Core;

namespace LongJourney.OpenAI;

/// <summary>Standard API pricing; cached reads and cache writes are disjoint input categories.</summary>
public static class OpenAiPricing
{
    public static decimal Calculate(ModelOptions model, long input, long cached, long writes, long output)
    {
        if (input < 0 || cached < 0 || writes < 0 || output < 0 || cached > input || writes > input - cached)
            throw new InvalidDataException("OpenAI returned inconsistent token usage.");

        var isLongContext = input > model.LongContextThresholdTokens;
        var inputMultiplier = isLongContext ? model.LongContextInputMultiplier : 1m;
        var outputMultiplier = isLongContext ? model.LongContextOutputMultiplier : 1m;
        return ((input - cached - writes) * model.InputUsdPerMillion * inputMultiplier +
                cached * model.CachedInputUsdPerMillion * inputMultiplier +
                writes * model.CacheWriteUsdPerMillion * inputMultiplier +
                output * model.OutputUsdPerMillion * outputMultiplier) / 1_000_000m;
    }

    public static decimal Reserve(ModelOptions model, long maximumInputTokens)
    {
        var inputPrice = Math.Max(model.InputUsdPerMillion,
            Math.Max(model.CachedInputUsdPerMillion, model.CacheWriteUsdPerMillion));
        var longContext = maximumInputTokens > model.LongContextThresholdTokens;
        return (maximumInputTokens * inputPrice * (longContext ? model.LongContextInputMultiplier : 1m) +
                model.MaxOutputTokens * model.OutputUsdPerMillion * (longContext ? model.LongContextOutputMultiplier : 1m)) /
               1_000_000m;
    }
}
