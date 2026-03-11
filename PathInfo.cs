using System.Collections.Generic;

namespace GameTranslatorUltimate
{
    public class PathInfo
    {
        public string BaseAddressModule { get; set; } = string.Empty;
        public long BaseAddressOffset { get; set; }
        public List<int> PointerOffsets { get; set; } = new List<int>();
    }
}
