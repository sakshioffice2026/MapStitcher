// MapStitcher.Business/Services/IndexMapLayerConfig.cs
using System;
using System.Collections.Generic;

namespace MapStitcher.Business.Services
{
    public static class IndexMapLayerConfig
    {
        public const string IndexGridLayer = "Sym_Hatch";
        public const string NeatlineLayer = "Poly_Survey_Bndry";

        // Master village outline. Individual edge sheets carry the portion
        // of the overall village perimeter that passes through them on
        // this layer; once every sheet is placed in the grid, the union of
        // these per-sheet rings reconstructs the full village boundary and
        // is used to trim the stitched output's outer edge.
        public const string VillageBoundaryLayer = "Poly_Village_Bndry";

        // Print/plot scaffolding layers (raster underlay only) that never
        // belong in a stitched village map.
        public static readonly HashSet<string> NonSurveyLayers = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "Line_20_20_Grid",
            "Image"
        };

        public const string SurveyNumberLayer = "Text_Survey_No";

        // Whitelist for the final stitched village export: actual
        // cadastral parcel geometry, the master village boundary, and the
        // per-parcel survey/sheet number labels are kept. Everything else
        // — sheet titles/legends, scale/direction symbols, other
        // coordinate/reference text tables, grid frames, built-up/off/
        // cancelled polygons, point symbols (wells, trees, stones, poles,
        // temples) — is marginal sheet furniture and is dropped so the
        // merged map contains clean, numbered parcel lines only.
        public static readonly HashSet<string> CadastralKeepLayers = new(
            StringComparer.OrdinalIgnoreCase)
        {
            NeatlineLayer,          // "Poly_Survey_Bndry" — parcel geometry
            VillageBoundaryLayer,   // "Poly_Village_Bndry" — village outline
            SurveyNumberLayer       // "Text_Survey_No" — sheet/survey numbers
        };
    }
}