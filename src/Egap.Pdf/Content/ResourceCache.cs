using System.Runtime.CompilerServices;

namespace Egap.Pdf.Content;

/// <summary>
/// The objects a document has written for drawing, by what they were made from (Phase 3 design Q6): a raster, a picture, a
/// typeface or a graphics state. Keys that are objects the caller owns, such as rasters, are held weakly, so the cache never
/// keeps a caller's pixels alive; keys that are values, such as an opacity and blend mode, are held by value. Some objects,
/// such as font subsets, are only complete when the document is saved: they register what to write then.
/// </summary>
internal sealed class ResourceCache
{
    private readonly ConditionalWeakTable<object, object> byInstance = [];
    private readonly Dictionary<object, object> byValue = [];
    private readonly List<Action> finishers = [];
    private readonly Lock gate = new();

    /// <summary>What was made for <paramref name="key"/> (compared by identity), made by <paramref name="create"/> the first time.</summary>
    public T ForInstance<T>(object key, Func<T> create)
        where T : class
    {
        lock (gate)
        {
            if (!byInstance.TryGetValue(key, out object? value))
            {
                value = create();
                byInstance.Add(key, value);
            }

            return (T)value;
        }
    }

    /// <summary>What was made for <paramref name="key"/> (compared by value), made by <paramref name="create"/> the first time.</summary>
    public T ForValue<T>(object key, Func<T> create)
        where T : class
    {
        lock (gate)
        {
            if (!byValue.TryGetValue(key, out object? value))
            {
                value = create();
                byValue[key] = value;
            }

            return (T)value;
        }
    }

    /// <summary>Registers what to write before the document is saved, such as a font subset once its glyphs are known.</summary>
    public void OnFinish(Action finish)
    {
        lock (gate)
        {
            finishers.Add(finish);
        }
    }

    /// <summary>Runs every registered finisher, before a save; each writes only what changed since it last ran.</summary>
    public void Finish()
    {
        Action[] pending;
        lock (gate)
        {
            pending = [.. finishers];
        }

        foreach (Action finish in pending)
        {
            finish();
        }
    }

    /// <summary>Forgets what was made for a key, as when it was flushed and must not be referenced again by mistake.</summary>
    public void Forget(object key)
    {
        lock (gate)
        {
            byInstance.Remove(key);
            byValue.Remove(key);
        }
    }
}
