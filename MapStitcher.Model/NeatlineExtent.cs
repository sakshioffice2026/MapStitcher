// MapStitcher.Model/NeatlineExtent.cs
namespace MapStitcher.Model
{
    public class NeatlineExtent
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }

        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public bool IsValid => Width > 0 && Height > 0;
    }
}