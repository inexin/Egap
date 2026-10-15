using System.Runtime.CompilerServices;

namespace Egap.Pdf.Rendering;

/// <summary>
/// Which optional content shows in a render (8.11, design Q13): the default configuration (or one chosen by name) with its
/// base state and ON and OFF lists, then its /AS usage rules for the render's event (View or Print) applied through each
/// group's /Usage, then the caller's overrides. Membership dictionaries combine groups by their policy or visibility
/// expression. Anything that cannot be read shows, as viewers do.
/// </summary>
internal sealed class OptionalContent
{
    private const int MaxDepth = 32;

    private readonly ConditionalWeakTable<PdfDictionary, StrongBox<bool>> groups = [];
    private readonly bool defaultOn = true;

    public OptionalContent(PdfDocument document, PdfRenderIntent intent, IReadOnlyDictionary<PdfLayer, bool>? overrides, string? configuration)
    {
        PdfDictionary? properties = document.Catalog.Get<PdfDictionary>("OCProperties");
        PdfDictionary? config = properties?.Get<PdfDictionary>("D");
        if (configuration is not null && properties?.Get<PdfArray>("Configs") is PdfArray configs)
        {
            config = configs.Select(c => c.Resolve()).OfType<PdfDictionary>().FirstOrDefault(c => c.GetText("Name") == configuration) ?? config;
        }

        if (config is null)
        {
            Apply(overrides);
            return;
        }

        // The base state, then ON and OFF (8.11.4.3).
        defaultOn = config.GetName("BaseState")?.Value != "OFF";
        if (config.GetName("BaseState")?.Value == "Unchanged" && properties?.Get<PdfDictionary>("D") is PdfDictionary standard && !ReferenceEquals(standard, config))
        {
            defaultOn = standard.GetName("BaseState")?.Value != "OFF";
            Set(standard.Get<PdfArray>("ON"), true);
            Set(standard.Get<PdfArray>("OFF"), false);
        }

        Set(config.Get<PdfArray>("ON"), true);
        Set(config.Get<PdfArray>("OFF"), false);

        // Usage rules for this event (8.11.4.4): each listed group takes the state its usage gives for the categories named.
        string happening = intent == PdfRenderIntent.Print ? "Print" : "View";
        foreach (PdfDictionary rule in (config.Get<PdfArray>("AS") ?? []).Select(r => r.Resolve()).OfType<PdfDictionary>())
        {
            if (rule.GetName("Event")?.Value != happening)
            {
                continue;
            }

            string[] categories = [.. Names(rule.Get("Category"))];
            foreach (PdfDictionary group in (rule.Get<PdfArray>("OCGs") ?? []).Select(g => g.Resolve()).OfType<PdfDictionary>())
            {
                PdfDictionary? usage = group.Get<PdfDictionary>("Usage");
                foreach (string category in categories)
                {
                    string? state = category switch
                    {
                        "View" => usage?.Get<PdfDictionary>("View")?.GetName("ViewState")?.Value,
                        "Print" => usage?.Get<PdfDictionary>("Print")?.GetName("PrintState")?.Value,
                        "Export" => usage?.Get<PdfDictionary>("Export")?.GetName("ExportState")?.Value,
                        _ => null,
                    };
                    if (state is "ON" or "OFF")
                    {
                        groups.AddOrUpdate(group, new StrongBox<bool>(state == "ON"));
                    }
                }
            }
        }

        Apply(overrides);
    }

    /// <summary>Whether content marked with this group or membership dictionary shows; true for anything else.</summary>
    public bool IsVisible(PdfDictionary? optionalContent)
    {
        return optionalContent is null || Visible(optionalContent, 0);
    }

    private bool Visible(PdfDictionary oc, int depth)
    {
        if (depth > MaxDepth)
        {
            return true;
        }

        if (oc.GetName("Type")?.Value == "OCMD")
        {
            if (oc.Get("VE")?.Resolve() is PdfArray expression)
            {
                return Expression(expression, depth + 1);
            }

            PdfDictionary[] members = oc.Get("OCGs")?.Resolve() switch
            {
                PdfDictionary single => [single],
                PdfArray list => [.. list.Select(g => g.Resolve()).OfType<PdfDictionary>()],
                _ => [],
            };
            if (members.Length == 0)
            {
                return true;
            }

            return oc.GetName("P")?.Value switch
            {
                "AllOn" => members.All(Group),
                "AnyOff" => members.Any(m => !Group(m)),
                "AllOff" => members.All(m => !Group(m)),
                _ => members.Any(Group),
            };
        }

        return Group(oc);
    }

    /// <summary>A visibility expression (8.11.2.2): <c>[/And …]</c>, <c>[/Or …]</c> or <c>[/Not x]</c> over groups.</summary>
    private bool Expression(PdfArray expression, int depth)
    {
        if (depth > MaxDepth || expression.Count < 2 || expression.GetName(0)?.Value is not string op)
        {
            return true;
        }

        IEnumerable<bool> operands = expression.Skip(1).Select(o => o.Resolve() switch
        {
            PdfArray nested => Expression(nested, depth + 1),
            PdfDictionary group => Group(group),
            _ => true,
        });
        return op switch
        {
            "And" => operands.All(v => v),
            "Or" => operands.Any(v => v),
            "Not" => !operands.First(),
            _ => true,
        };
    }

    private bool Group(PdfDictionary group)
    {
        return groups.TryGetValue(group, out StrongBox<bool>? state) ? state.Value : defaultOn;
    }

    private void Set(PdfArray? list, bool on)
    {
        foreach (PdfDictionary group in (list ?? []).Select(g => g.Resolve()).OfType<PdfDictionary>())
        {
            groups.AddOrUpdate(group, new StrongBox<bool>(on));
        }
    }

    private void Apply(IReadOnlyDictionary<PdfLayer, bool>? overrides)
    {
        foreach ((PdfLayer layer, bool on) in overrides ?? new Dictionary<PdfLayer, bool>())
        {
            groups.AddOrUpdate(layer.Dictionary, new StrongBox<bool>(on));
        }
    }

    private static IEnumerable<string> Names(PdfObject? value)
    {
        return value?.Resolve() switch
        {
            PdfName name => [name.Value],
            PdfArray array => array.Select(v => v.Resolve()).OfType<PdfName>().Select(n => n.Value),
            _ => [],
        };
    }
}
