using System.Collections.Generic;
using System.Text;

namespace P5S_ceviri
{
    public static class OcrModelUtils
    {
        // 0-9, a-z, A-Z and common punctuation + Turkish characters
        // NOTE: This alphabet must match the one used during training of the model.
        // If the model was trained with a different alphabet, the output will be garbage.
        public static readonly string DefaultAlphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~ abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZÇçĞğİıÖöŞşÜü";

        // Slightly reduced alphabet often used in generic models (94 chars + blank)
        // Adjust this if you know the specific alphabet of your .pb models
        public static readonly string GenericAlphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

        /// <summary>
        /// Decodes CTC output from CRNN/PaddleOCR.
        /// CTC adds a 'blank' character (usually index 0 or last index) to handle repeated characters.
        /// </summary>
        public static string DecodeCTC(List<int> classIndices, string alphabet)
        {
            if (classIndices == null || classIndices.Count == 0) return string.Empty;

            StringBuilder sb = new StringBuilder();
            int lastIndex = -1;

            // Assume 0 is the blank character for this implementation (common in many frameworks)
            // Some implementations use the last index as blank. We might need to adjust based on the specific model.
            // For this implementation, we will treat -1 as "no character" effectively,
            // but we need to know WHICH index is the blank token.
            // Typically in CRNN implementations:
            // - Keras/TensorFlow often uses the last class as blank.
            // - PyTorch/others might use 0.

            // Heuristic: If we don't know the blank index, we assume it's NOT in the alphabet range if alphabet.Length == num_classes - 1
            // Here we will blindly implement logic that ignores duplicate adjacent indices (unless separated by blank).

            // Let's assume standard CTC decoding:
            // 1. Collapse repeated characters: "a", "a", "b" -> "a", "b"
            // 2. Remove blanks.
            // Note: "a", "blank", "a" -> "a", "a"

            // However, we need to know the blank index.
            // Since we can't inspect the model file structure here, we'll try a common convention:
            // If the index is out of bounds of the alphabet string, treat it as blank.

            foreach (int index in classIndices)
            {
                if (index != lastIndex)
                {
                    if (index >= 0 && index < alphabet.Length)
                    {
                        sb.Append(alphabet[index]);
                    }
                    // If index is out of alphabet range, we treat it as blank and don't append.
                    lastIndex = index;
                }
            }

            return sb.ToString();
        }
    }
}
