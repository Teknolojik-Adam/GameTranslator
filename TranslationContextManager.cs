using System.Collections.Generic;
using System.Linq;

namespace GameTranslatorUltimate
{
    public class TranslationContextManager
    {
        private readonly int _maxHistorySize;
        private readonly Queue<string> _history;

        public TranslationContextManager(int maxHistorySize = 3) // Geçmişi 3 ile sınırlama
        {
            _maxHistorySize = maxHistorySize;
            _history = new Queue<string>();
        }

        public void AddToHistory(string originalText)
        {
            if (string.IsNullOrWhiteSpace(originalText)) return;
            if (_history.Count >= _maxHistorySize)
            {
                _history.Dequeue(); // En eski kaydı sil
            }
            // Sadece orijinal metni eklemek için
            _history.Enqueue(originalText.Replace("\n", " ").Trim());
        }

        public string GetContextualPrompt(string newText)
        {
            if (_history.Count == 0)
            {
                return newText;
            }
            // Geçmişi ve yeni metni birleştirerek tek bir paragraf oluştur
            return string.Join(". ", _history) + ". " + newText;
        }

        public void Clear()
        {
            _history.Clear();
        }
    }
}
