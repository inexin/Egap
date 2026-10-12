namespace Egap.Pdf;

/// <summary>
/// An action (12.6): a thin view over its dictionary, typed by <c>/S</c> through <see cref="FromDictionary"/>. JavaScript
/// and any action type Egap does not know are plain <see cref="PdfAction"/>s: kept and written back as they are, never run.
/// Two views are equal when they wrap the same dictionary.
/// </summary>
public class PdfAction : IEquatable<PdfAction>
{
    private protected static readonly PdfName TypeKey = "S";
    private protected static readonly PdfName NextKey = "Next";

    private protected PdfAction(PdfDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        Dictionary = dictionary;
    }

    /// <summary>A new action dictionary of the given type.</summary>
    private protected PdfAction(string type)
        : this(new PdfDictionary { ["Type"] = PdfName.Get("Action"), [TypeKey] = PdfName.Get(type) })
    {
    }

    /// <summary>The action's dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The action type, <c>/S</c>, such as <c>GoTo</c>, <c>URI</c> or <c>JavaScript</c>; empty when missing.</summary>
    public string ActionType => Dictionary.GetName(TypeKey)?.Value ?? "";

    /// <summary>The actions that run after this one (<c>/Next</c>), in order; dictionaries only.</summary>
    public IReadOnlyList<PdfAction> Next
    {
        get
        {
            return Dictionary.Get(NextKey) switch
            {
                PdfDictionary single => [FromDictionary(single)],
                PdfArray array => [.. Enumerable.Range(0, array.Count).Select(array.Get<PdfDictionary>).OfType<PdfDictionary>().Select(FromDictionary)],
                _ => [],
            };
        }
    }

    /// <summary>The action for a dictionary, typed by its <c>/S</c>.</summary>
    public static PdfAction FromDictionary(PdfDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        return dictionary.GetName(TypeKey)?.Value switch
        {
            "GoTo" => new PdfGoToAction(dictionary),
            "GoToR" => new PdfGoToRemoteAction(dictionary),
            "GoToE" => new PdfGoToEmbeddedAction(dictionary),
            "URI" => new PdfUriAction(dictionary),
            "Named" => new PdfNamedAction(dictionary),
            "Launch" => new PdfLaunchAction(dictionary),
            "SubmitForm" => new PdfSubmitFormAction(dictionary),
            "ResetForm" => new PdfResetFormAction(dictionary),
            "Hide" => new PdfHideAction(dictionary),
            "SetOCGState" => new PdfSetOcgStateAction(dictionary),
            _ => new PdfAction(dictionary),
        };
    }

    /// <summary>
    /// This action and every action after it, in the order they run (12.6.2: depth first through <c>/Next</c>). An action
    /// reached a second time, as in a <c>/Next</c> loop, is left out, so the list always ends.
    /// </summary>
    public IReadOnlyList<PdfAction> GetSequence()
    {
        List<PdfAction> sequence = [];
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        Stack<PdfAction> pending = new([this]);
        while (pending.TryPop(out PdfAction? action))
        {
            if (!seen.Add(action.Dictionary))
            {
                continue;
            }

            sequence.Add(action);
            IReadOnlyList<PdfAction> next = action.Next;
            for (int i = next.Count - 1; i >= 0; i--)
            {
                pending.Push(next[i]);
            }
        }

        return sequence;
    }

    /// <summary>
    /// Sets the actions that run after this one; none removes <c>/Next</c>. Each must be a new action, not yet stored
    /// anywhere; it is added to <paramref name="document"/> as an indirect object.
    /// </summary>
    public void SetNext(PdfDocument document, params IReadOnlyList<PdfAction> actions)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Any(a => ((Objects.IContainer)a.Dictionary).Parent is not null))
        {
            throw new ArgumentException("A next action must be a new action, not one already stored in a document.", nameof(actions));
        }

        Dictionary[NextKey] = actions.Count switch
        {
            0 => null,
            1 => document.Objects.Add(actions[0].Dictionary),
            _ => new PdfArray(actions.Select(a => (PdfObject)document.Objects.Add(a.Dictionary))),
        };
    }

    /// <summary>Whether both views wrap the same action dictionary.</summary>
    public bool Equals(PdfAction? other)
    {
        return other is not null && ReferenceEquals(Dictionary, other.Dictionary);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfAction);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Dictionary);
    }

    /// <summary>The action type, for debugging.</summary>
    public override string ToString()
    {
        return $"{ActionType} action";
    }

    /// <summary>A file specification's name (7.11): the string itself, or a dictionary's <c>/UF</c>, else <c>/F</c>.</summary>
    private protected static string? FileSpecName(PdfObject? value)
    {
        return value?.Resolve() switch
        {
            PdfString text => text.ToText(),
            PdfDictionary spec => spec.GetText("UF") ?? spec.GetText("F"),
            _ => null,
        };
    }

    /// <summary>An explicit destination in <c>/D</c>, or null.</summary>
    private protected PdfDestination? ExplicitDestination => Dictionary.Get("D") switch
    {
        PdfArray array => PdfDestination.FromArray(array),
        PdfDictionary dictionary => PdfDestination.FromArray(dictionary.Get<PdfArray>("D")),
        _ => null,
    };

    /// <summary>The name of a named destination in <c>/D</c>, or null.</summary>
    private protected string? DestinationNameValue => Dictionary.Get("D") switch
    {
        PdfName name => name.Value,
        PdfString text => text.ToText(),
        _ => null,
    };

}

/// <summary>A go-to action (12.6.4.2): goes to a destination in this document, given explicitly or by name.</summary>
public sealed class PdfGoToAction : PdfAction
{
    internal PdfGoToAction(PdfDictionary dictionary)
        : base(dictionary)
    {
    }

    /// <summary>A new action going to an explicit destination.</summary>
    public PdfGoToAction(PdfDestination destination)
        : base("GoTo")
    {
        ArgumentNullException.ThrowIfNull(destination);
        Dictionary["D"] = destination.ToArray();
    }

    /// <summary>A new action going to a named destination.</summary>
    public PdfGoToAction(string destinationName)
        : base("GoTo")
    {
        ArgumentNullException.ThrowIfNull(destinationName);
        Dictionary["D"] = PdfString.FromText(destinationName);
    }

    /// <summary>The explicit destination, or <see langword="null"/> when the action names one or has none.</summary>
    public PdfDestination? Destination => ExplicitDestination;

    /// <summary>The name of the destination, or <see langword="null"/> when it is explicit.</summary>
    public string? DestinationName => DestinationNameValue;

    /// <summary>The destination, a named one looked up in <paramref name="document"/>'s named destinations.</summary>
    public PdfDestination? ResolveDestination(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.NamedDestinations.Resolve(Dictionary["D"]);
    }
}

/// <summary>A remote go-to action (12.6.4.3): goes to a destination in another PDF file, whose pages are given by index.</summary>
public sealed class PdfGoToRemoteAction : PdfAction
{
    internal PdfGoToRemoteAction(PdfDictionary dictionary)
        : base(dictionary)
    {
    }

    /// <summary>A new action going to an explicit destination (a page index) in another file.</summary>
    public PdfGoToRemoteAction(string fileName, PdfDestination destination)
        : base("GoToR")
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(destination);
        Dictionary["F"] = PdfString.FromText(fileName);
        Dictionary["D"] = destination.ToArray();
    }

    /// <summary>A new action going to a named destination in another file.</summary>
    public PdfGoToRemoteAction(string fileName, string destinationName)
        : base("GoToR")
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(destinationName);
        Dictionary["F"] = PdfString.FromText(fileName);
        Dictionary["D"] = PdfString.FromText(destinationName);
    }

    /// <summary>The other file's name, from <c>/F</c>.</summary>
    public string? FileName => FileSpecName(Dictionary["F"]);

    /// <summary>The explicit destination, its page an index into the other file.</summary>
    public PdfDestination? Destination => ExplicitDestination;

    /// <summary>The name of the destination in the other file.</summary>
    public string? DestinationName => DestinationNameValue;

    /// <summary>Whether to open the file in a new window (<c>/NewWindow</c>); <see langword="null"/> leaves it to the viewer.</summary>
    public bool? NewWindow => Dictionary.GetBool("NewWindow");
}

/// <summary>An embedded go-to action (12.6.4.4): goes to a destination in an embedded or embedding PDF file.</summary>
public sealed class PdfGoToEmbeddedAction : PdfAction
{
    internal PdfGoToEmbeddedAction(PdfDictionary dictionary)
        : base(dictionary)
    {
    }

    /// <summary>The root file's name, from <c>/F</c>; <see langword="null"/> when the target is in this file.</summary>
    public string? FileName => FileSpecName(Dictionary["F"]);

    /// <summary>The explicit destination in the target file.</summary>
    public PdfDestination? Destination => ExplicitDestination;

    /// <summary>The name of the destination in the target file.</summary>
    public string? DestinationName => DestinationNameValue;

    /// <summary>The target dictionary (<c>/T</c>) that leads from the root file to the target file.</summary>
    public PdfDictionary? Target => Dictionary.Get<PdfDictionary>("T");

    /// <summary>Whether to open the file in a new window; <see langword="null"/> leaves it to the viewer.</summary>
    public bool? NewWindow => Dictionary.GetBool("NewWindow");
}

/// <summary>A URI action (12.6.4.8): opens a URI, usually a web address.</summary>
public sealed class PdfUriAction : PdfAction
{
    internal PdfUriAction(PdfDictionary dictionary)
        : base(dictionary)
    {
    }

    /// <summary>A new action opening a URI.</summary>
    public PdfUriAction(string uri)
        : base("URI")
    {
        Uri = uri;
    }

    /// <summary>
    /// The URI, from <c>/URI</c>. The spec makes it 7-bit ASCII; bytes beyond are read as UTF-8 when they are, else Latin-1,
    /// and written as UTF-8.
    /// </summary>
    public string? Uri
    {
        get
        {
            if (Dictionary.Get<PdfString>("URI") is not PdfString uri)
            {
                return null;
            }

            try
            {
                return new System.Text.UTF8Encoding(false, true).GetString(uri.Bytes);
            }
            catch (System.Text.DecoderFallbackException)
            {
                return System.Text.Encoding.Latin1.GetString(uri.Bytes);
            }
        }

        set => Dictionary["URI"] = value is null ? null : PdfString.FromBytes(System.Text.Encoding.UTF8.GetBytes(value));
    }

    /// <summary>Whether the click position is added to the URI, as for a server-side image map (<c>/IsMap</c>).</summary>
    public bool IsMap => Dictionary.GetBool("IsMap") ?? false;
}

/// <summary>A named action (12.6.4.11), such as <c>NextPage</c>, <c>PrevPage</c>, <c>FirstPage</c> or <c>LastPage</c>.</summary>
public sealed class PdfNamedAction : PdfAction
{
    internal PdfNamedAction(PdfDictionary dictionary)
        : base(dictionary)
    {
    }

    /// <summary>A new named action.</summary>
    public PdfNamedAction(string name)
        : base("Named")
    {
        ArgumentNullException.ThrowIfNull(name);
        Dictionary["N"] = PdfName.Get(name);
    }

    /// <summary>The action's name, from <c>/N</c>.</summary>
    public string? Name => Dictionary.GetName("N")?.Value;
}

/// <summary>A launch action (12.6.4.6): opens a file or starts an application. Kept as it is; Egap never launches anything.</summary>
public sealed class PdfLaunchAction : PdfAction
{
    internal PdfLaunchAction(PdfDictionary dictionary)
        : base(dictionary)
    {
    }

    /// <summary>A new action opening a file.</summary>
    public PdfLaunchAction(string fileName)
        : base("Launch")
    {
        ArgumentNullException.ThrowIfNull(fileName);
        Dictionary["F"] = PdfString.FromText(fileName);
    }

    /// <summary>The file, from <c>/F</c>, or from the Windows-specific <c>/Win</c> dictionary's <c>/F</c>.</summary>
    public string? FileName => FileSpecName(Dictionary["F"]) ?? Dictionary.Get<PdfDictionary>("Win")?.GetText("F");

    /// <summary>Whether to open a PDF file in a new window; <see langword="null"/> leaves it to the viewer.</summary>
    public bool? NewWindow => Dictionary.GetBool("NewWindow");
}

/// <summary>A submit-form action (12.7.6.2): sends field values to a URL.</summary>
public sealed class PdfSubmitFormAction : PdfAction
{
    internal PdfSubmitFormAction(PdfDictionary dictionary)
        : base(dictionary)
    {
    }

    /// <summary>Where the values are sent, from <c>/F</c>.</summary>
    public string? Url => FileSpecName(Dictionary["F"]);

    /// <summary>The fields to send or leave out (<c>/Fields</c>), by reference or name; <see langword="null"/> for all.</summary>
    public PdfArray? Fields => Dictionary.Get<PdfArray>("Fields");

    /// <summary>The submit flags (<c>/Flags</c>, table 239).</summary>
    public int Flags => (int)(Dictionary.GetInt("Flags") ?? 0);
}

/// <summary>A reset-form action (12.7.6.3): returns fields to their default values.</summary>
public sealed class PdfResetFormAction : PdfAction
{
    internal PdfResetFormAction(PdfDictionary dictionary)
        : base(dictionary)
    {
    }

    /// <summary>The fields to reset or leave out (<c>/Fields</c>); <see langword="null"/> for all.</summary>
    public PdfArray? Fields => Dictionary.Get<PdfArray>("Fields");

    /// <summary>The reset flags (<c>/Flags</c>): bit 1 set means every field except <see cref="Fields"/>.</summary>
    public int Flags => (int)(Dictionary.GetInt("Flags") ?? 0);
}

/// <summary>A hide action (12.6.4.10): hides or shows annotations or fields.</summary>
public sealed class PdfHideAction : PdfAction
{
    internal PdfHideAction(PdfDictionary dictionary)
        : base(dictionary)
    {
    }

    /// <summary>The annotations or fields (<c>/T</c>): a dictionary, a field name, or an array of them.</summary>
    public PdfObject? Targets => Dictionary.Get("T");

    /// <summary>Whether to hide (<c>/H</c>, the default) rather than show.</summary>
    public bool Hide => Dictionary.GetBool("H") ?? true;
}

/// <summary>A set-OCG-state action (12.6.4.13): turns optional content groups (layers) on or off.</summary>
public sealed class PdfSetOcgStateAction : PdfAction
{
    internal PdfSetOcgStateAction(PdfDictionary dictionary)
        : base(dictionary)
    {
    }

    /// <summary>The state changes (<c>/State</c>): <c>/ON</c>, <c>/OFF</c> or <c>/Toggle</c>, each followed by groups.</summary>
    public PdfArray? State => Dictionary.Get<PdfArray>("State");

    /// <summary>Whether radio-button relationships between groups are kept (<c>/PreserveRB</c>, default true).</summary>
    public bool PreserveRadioButtons => Dictionary.GetBool("PreserveRB") ?? true;
}
