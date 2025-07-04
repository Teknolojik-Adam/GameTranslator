using System.Collections.Generic;

namespace P5S_ceviri
{
    public class PathInfo
    {
        public string BaseAddressModule { get; set; }
        public long BaseAddressOffset { get; set; }
        public List<int> PointerOffsets { get; set; } = new List<int>();
        public string Signature { get; set; }
        public int RipRelativeOffset { get; set; }
    }
}