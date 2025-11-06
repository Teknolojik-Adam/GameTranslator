using System;

namespace P5S_ceviri
{
    //
    public class PointerStabilityResult
    {
        public PointerPath Path { get; set; }
        public bool IsStable { get; set; }
        public string Message { get; set; }
        public IntPtr LastKnownAddress { get; set; }
        public double SuccessRate { get; set; }
        public double AddressConsistency { get; set; }
        public double ValueConsistency { get; set; }
        public double StabilityScore { get; set; }
    }
}