using GameTranslatorOCR.Core.Models;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Text;

namespace GameTranslatorOCR.Core.Recognition;

/// <summary>CTC greedy alignment; blank=0, dictionary indices 1..N, space=N+1.</summary>
public sealed class CtcDecoder
{
    private readonly string[] _chars;
    private readonly bool[] _allowedTurkishEnglish;

    // Only an opt-in decoder mode. Existing multilingual decoding remains unchanged.
    // Turkish+English game dialogue can include Turkish circumflex vowels.
    private const string TurkishEnglishLetters =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyzÇĞİÖŞÜçğıöşüÂÎÛâîû";

    private static bool IsTurkishEnglishToken(string token)
    {
        foreach (var rune in token.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.UppercaseLetter or
                System.Globalization.UnicodeCategory.LowercaseLetter or
                System.Globalization.UnicodeCategory.TitlecaseLetter or
                System.Globalization.UnicodeCategory.ModifierLetter or
                System.Globalization.UnicodeCategory.OtherLetter)
            {
                if (!TurkishEnglishLetters.Contains(rune.ToString(), StringComparison.Ordinal))
                    return false;
            }
        }
        return true;
    }

    public CtcDecoder(string dictionaryPath)
    {
        if (!File.Exists(dictionaryPath))
            throw new FileNotFoundException("Karakter sözlüğü bulunamadı.",dictionaryPath);
        _chars = File.ReadAllLines(dictionaryPath).Select(x=>x.TrimEnd('\r')).ToArray();
        if (_chars.Length==0)throw new InvalidDataException("Karakter sözlüğü boş.");
        _allowedTurkishEnglish = _chars.Select(IsTurkishEnglishToken).ToArray();
    }

    public RecognitionResult Decode(Tensor<float> probabilities) =>
        DecodeDetailed(probabilities,0).Recognition;

    /// <summary>Dinamik batch'teki tek çıktıyı ve kelime zaman indekslerini çözer.</summary>
    public DecodedRecognition DecodeDetailed(Tensor<float> probabilities,
        int batchIndex=0, bool turkishEnglishOnly=false)
    {
        var dims=probabilities.Dimensions;
        if(dims.Length!=3||batchIndex<0||batchIndex>=dims[0])
            throw new InvalidDataException("CTC output [batch,sequence,classes] olmalı.");
        int time=dims[1],classes=dims[2];
        if(classes!=_chars.Length+2)
            throw new InvalidDataException(
                $"Sözlük/model uyuşmazlığı: {classes} çıktı, {_chars.Length} karakter.");
        var sb=new StringBuilder();
        float confidenceSum=0;
        int tokens=0,previous=-1;
        var words=new List<WordTimeSpan>();
        var current=new StringBuilder();
        int start=-1, last=-1;
        float wordScore=0;
        int wordTokens=0;

        void Flush()
        {
            if(current.Length==0)return;
            words.Add(new WordTimeSpan(current.ToString(),start,last+1,
                wordTokens==0?0:wordScore/wordTokens));
            current.Clear();start=-1;last=-1;wordScore=0;wordTokens=0;
        }

        // Most ONNX Runtime outputs are contiguous DenseTensor<float>.
        // Read its flat span instead of performing a multidimensional tensor
        // index lookup for every CTC timestep and class. Preserve fallback.
        bool contiguous=probabilities is DenseTensor<float>;
        ReadOnlySpan<float> flat=probabilities is DenseTensor<float> dense
            ?dense.Buffer.Span:default;
        for(int t=0;t<time;t++)
        {
            int stepOffset=(batchIndex*time+t)*classes;
            float max=float.NegativeInfinity;int chosen=0;
            for(int cls=0;cls<classes;cls++)
            {
                if (turkishEnglishOnly && cls > 0 && cls < classes - 1 &&
                    !_allowedTurkishEnglish[cls - 1])
                    continue;
                float score=contiguous?flat[stepOffset+cls]
                    :probabilities[batchIndex,t,cls];
                if(float.IsNaN(score))continue;
                if(score>max){max=score;chosen=cls;}
            }
            if(chosen!=0&&chosen!=previous)
            {
                string value=chosen==classes-1?" ":_chars[chosen-1];
                sb.Append(value);
                confidenceSum+=float.IsFinite(max)?max:0f;
                tokens++;
                if(string.IsNullOrWhiteSpace(value))Flush();
                else
                {
                    if(start<0)start=t;
                    last=t;
                    current.Append(value);
                    wordScore+=float.IsFinite(max)?max:0;
                    wordTokens++;
                }
            }
            previous=chosen;
        }
        Flush();
        return new DecodedRecognition(
            new RecognitionResult(sb.ToString().Trim(),tokens==0?0:confidenceSum/tokens),
            words,time);
    }
}

public sealed record WordTimeSpan(string Text,int StartStep,int EndStep,float Confidence);

public sealed record DecodedRecognition(RecognitionResult Recognition,
    IReadOnlyList<WordTimeSpan> Words,int TotalSteps);
