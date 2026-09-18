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

        // Print/plot scaffolding layers (scale rulers, reference grid,
        // raster underlay) that never belong in a stitched village map.
        public static readonly HashSet<string> NonSurveyLayers = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "Grid",
            "Line_20_20_Grid",
            "Image"
        };
    }
}