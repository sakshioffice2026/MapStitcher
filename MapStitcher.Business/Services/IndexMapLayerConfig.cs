// MapStitcher.Business/Services/IndexMapLayerConfig.cs
using System;
using System.Collections.Generic;

namespace MapStitcher.Business.Services
{
    public static class IndexMapLayerConfig
    {
        public const string IndexGridLayer = "Sym_Hatch";
        public const string NeatlineLayer = "Poly_Survey_Bndry";
        public const string VillageBoundaryLayer = "Poly_Village_Bndry";
        public const string MarkingFrameLayer = "Line_Marking_Adusting_Sheet";  // note: "Adusting" not "Adjusting"

        public static readonly HashSet<string> NonSurveyLayers = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "Line_20_20_Grid",
            "Image"
        };

        public const string SurveyNumberLayer = "Text_Survey_No";

        public static readonly HashSet<string> CadastralKeepLayers = new(
            StringComparer.OrdinalIgnoreCase)
        {
            NeatlineLayer,
            VillageBoundaryLayer,
            SurveyNumberLayer
        };
    }
}