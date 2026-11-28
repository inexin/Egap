using Egami;
using Egami.Metafiles;

namespace Egap.Documents;

/// <summary>The Egami context Egap decodes pictures with.</summary>
internal static class Imaging
{
    private static (EgamiContext Default, EgamiContext Context)? cached;

    /// <summary>
    /// <see cref="EgamiContext.Default"/> with Egami.Metafiles' format registered, so that an EPS picture whose only preview
    /// is a Windows metafile loads (Egami reads EPS, and a metafile preview through whatever format reads it).
    /// </summary>
    public static EgamiContext Context
    {
        get
        {
            EgamiContext current = EgamiContext.Default;
            if (cached is not { } known || !ReferenceEquals(known.Default, current))
            {
                known = (current, current.WithFormat(MetafileFormat.Info));
                cached = known;
            }

            return known.Context;
        }
    }
}
