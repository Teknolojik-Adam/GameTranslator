using System;

namespace GameTranslatorUltimate
{
    public sealed class PointerStabilityResult
    {
        public PointerPath Path { get; set; }

        public bool IsStable { get; set; }

        public string Message { get; set; } = string.Empty;

        public IntPtr LastKnownAddress { get; set; } = IntPtr.Zero;

        public double SuccessRate { get; set; }

        public double AddressConsistency { get; set; }

        public double ValueConsistency { get; set; }

        public double StabilityScore { get; set; }
    }
}