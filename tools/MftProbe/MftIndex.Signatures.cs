using System.Numerics;

namespace MftProbe;

/// <summary>
/// Сигнатуры n-грамм — фильтр Блума, построенный над множеством n-грамм внутри каждого имени.
///
/// Классический Bloom над именами отвечает только на вопрос «есть ли файл с ТОЧНО таким
/// именем» и для поиска подстроки бесполезен. Здесь множество другое: имя "red_sofa.blend"
/// кладёт в свою сигнатуру все свои n-граммы, запрос "sofa" — свои. Если хотя бы один бит
/// запроса не установлен в сигнатуре имени, имя гарантированно не содержит подстроку.
///
/// Ложноотрицательных нет по построению; ложноположительные считаются как
///     fill = 1 - e^(-n/m),  p = fill^g
/// где n — число n-грамм имени, m — ширина сигнатуры, g — число n-грамм запроса.
///
/// ВАЖНО, что подтвердил первый замер на 1.2 млн реальных имён: формула систематически
/// занижает p в десятки раз. Причина не в ширине сигнатуры, а в допущении независимости:
/// биграммы естественных имён распределены крайне неравномерно, и запрос вроде "blend"
/// целиком собран из частотных биграмм (bl, le, en, nd), которые установлены почти у всех
/// имён. Отсюда параметризация: ширину и размер n-граммы задаёт вызывающий, а команда
/// "bench" в прототипе перебирает конфигурации и печатает измеренные значения рядом
/// с расчётными.
/// </summary>
public sealed partial class MftIndex
{
    /// <summary>Размер n-граммы: 2 — биграммы, 3 — триграммы.</summary>
    public int NgramSize { get; private set; } = 3;

    /// <summary>Ширина сигнатуры в 64-битных словах: 1, 2 или 4 (то есть 64, 128 или 256 бит).</summary>
    public int SignatureWords { get; private set; } = 2;

    public int SignatureBits => SignatureWords * 64;

    /// <summary>
    /// Сигнатуры в раскладке word-major: _signatures[w * _entryCount + i] — слово w записи i.
    ///
    /// Раскладка выбрана под характер доступа: слово 0 всех записей лежит непрерывно и
    /// сканируется векторно, а к остальным словам обращение идёт только для кандидатов,
    /// уже прошедших первое слово, — то есть редко.
    /// </summary>
    private ulong[] _signatures = [];

    /// <summary>Статистика одного поиска — материал для сравнения теории с практикой.</summary>
    public readonly record struct SearchStats(
        int Scanned,          // всего записей просмотрено
        int PassedFirstWord,  // прошли отсев по первому слову сигнатуры
        int Candidates,       // прошли сигнатуру целиком, дошли до сравнения строк
        int Matches,          // реально содержат подстроку
        int BlocksSkipped,    // векторных блоков отсеяно целиком
        bool FilterUsed)      // применялся ли фильтр (запрос короче n-граммы — нет)
    {
        /// <summary>Фактическая доля ложных срабатываний фильтра.</summary>
        public double FalsePositiveRate =>
            Candidates == 0 ? 0 : (double)(Candidates - Matches) / Candidates;

        /// <summary>Доля записей, не дошедших до посимвольного сравнения.</summary>
        public double RejectionRate =>
            Scanned == 0 ? 0 : 1.0 - (double)Candidates / Scanned;
    }

    /// <summary>
    /// Хеш n-граммы в номер бита. Регистр приводится здесь, поэтому в горячем цикле
    /// поиска никаких проверок регистра уже нет.
    ///
    /// FNV-1a по символам плюс финальное перемешивание: сам по себе FNV даёт слабо
    /// перемешанные старшие биты, а нам нужен именно остаток от ширины сигнатуры.
    /// </summary>
    private static int NgramBit(ReadOnlySpan<char> text, int start, int length, int bits)
    {
        const uint FnvOffset = 2166136261;
        const uint FnvPrime = 16777619;

        uint hash = FnvOffset;
        for (int i = 0; i < length; i++)
        {
            uint c = char.ToLowerInvariant(text[start + i]);
            hash = (hash ^ (c & 0xFF)) * FnvPrime;
            hash = (hash ^ (c >> 8)) * FnvPrime;
        }

        // Финальное перемешивание (murmur3 fmix32) — выравнивает распределение младших бит.
        hash ^= hash >> 16;
        hash *= 2246822507;
        hash ^= hash >> 13;
        hash *= 3266489909;
        hash ^= hash >> 16;

        return (int)(hash % (uint)bits);
    }

    /// <summary>Считает сигнатуру текста в предоставленный буфер из SignatureWords слов.</summary>
    private void ComputeSignature(ReadOnlySpan<char> text, Span<ulong> destination)
    {
        destination.Clear();

        int bits = SignatureBits;
        int last = text.Length - NgramSize;

        for (int i = 0; i <= last; i++)
        {
            int bit = NgramBit(text, i, NgramSize, bits);
            destination[bit >> 6] |= 1UL << (bit & 63);
        }
    }

    /// <summary>
    /// Строит сигнатуры для всех записей. Конфигурацию можно менять и перестраивать —
    /// на 1.2 млн записей это меньше секунды, что и делает возможным сравнение вариантов
    /// на живых данных.
    /// </summary>
    public void BuildSignatures(int ngramSize, int signatureWords)
    {
        if (ngramSize is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(ngramSize), "Поддерживаются n-граммы от 2 до 4.");
        if (signatureWords is not (1 or 2 or 4))
            throw new ArgumentOutOfRangeException(nameof(signatureWords), "Ширина: 1, 2 или 4 слова.");

        NgramSize = ngramSize;
        SignatureWords = signatureWords;

        long size = (long)_entryCount * signatureWords;
        if (size > int.MaxValue)
            throw new InvalidOperationException("Индекс слишком велик для выбранной ширины сигнатуры.");

        _signatures = new ulong[size];

        Span<ulong> scratch = stackalloc ulong[signatureWords];

        for (int i = 0; i < _entryCount; i++)
        {
            ref readonly Entry e = ref _entries[i];
            ComputeSignature(_names.AsSpan(e.NameOffset, e.NameLength), scratch);

            for (int w = 0; w < signatureWords; w++)
                _signatures[w * _entryCount + i] = scratch[w];
        }
    }

    public long SignatureBytes => (long)_signatures.Length * sizeof(ulong);

    /// <summary>
    /// Средняя доля установленных бит по всем сигнатурам — эмпирическое значение fill,
    /// на котором строится расчётная оценка ложных срабатываний.
    /// </summary>
    public double MeasureAverageFill()
    {
        if (_signatures.Length == 0) return 0;

        long bits = 0;
        for (int i = 0; i < _signatures.Length; i++)
            bits += BitOperations.PopCount(_signatures[i]);

        return (double)bits / _entryCount / SignatureBits;
    }

    /// <summary>Расчётная доля ложных срабатываний по формуле Блума для данного запроса.</summary>
    public double PredictFalsePositiveRate(string query)
    {
        int ngrams = query.Length - NgramSize + 1;
        if (ngrams <= 0) return 1.0;

        return Math.Pow(MeasureAverageFill(), ngrams);
    }

    /// <summary>
    /// Поиск с предварительным отсевом по сигнатурам.
    ///
    /// Первое слово всех сигнатур лежит непрерывно и сканируется блоками шириной
    /// Vector&lt;ulong&gt;: блок, в котором не прошла ни одна запись, пропускается целиком.
    /// Остальные слова проверяются только у прошедших — это дешёвая проверка, снимающая
    /// часть ложных срабатываний до дорогого сравнения строк.
    /// </summary>
    public List<int> SearchFiltered(string query, out SearchStats stats, int limit = int.MaxValue)
    {
        ReadOnlySpan<char> q = query;

        // Запрос короче n-граммы не порождает ни одной n-граммы — фильтровать нечем.
        if (q.Length < NgramSize || _signatures.Length != _entryCount * SignatureWords)
        {
            var fallback = Search(query, limit);
            stats = new SearchStats(_entryCount, _entryCount, _entryCount, fallback.Count, 0, false);
            return fallback;
        }

        Span<ulong> queryMask = stackalloc ulong[SignatureWords];
        ComputeSignature(q, queryMask);

        var results = new List<int>();
        int passedFirst = 0, candidates = 0, blocksSkipped = 0;

        ulong mask0 = queryMask[0];
        int width = Vector<ulong>.Count;
        var maskVector = new Vector<ulong>(mask0);

        int i = 0;
        while (i + width <= _entryCount && results.Count < limit)
        {
            var block = new Vector<ulong>(_signatures, i);
            var passed = Vector.Equals(Vector.BitwiseAnd(block, maskVector), maskVector);

            if (passed.Equals(Vector<ulong>.Zero))
            {
                blocksSkipped++;
                i += width;
                continue;
            }

            for (int j = 0; j < width && results.Count < limit; j++)
            {
                if (passed[j] == 0) continue;

                passedFirst++;
                if (!PassesRemainingWords(i + j, queryMask)) continue;

                candidates++;
                if (NameContains(i + j, q)) results.Add(i + j);
            }

            i += width;
        }

        // Хвост, не уложившийся в целый вектор.
        for (; i < _entryCount && results.Count < limit; i++)
        {
            if ((_signatures[i] & mask0) != mask0) continue;

            passedFirst++;
            if (!PassesRemainingWords(i, queryMask)) continue;

            candidates++;
            if (NameContains(i, q)) results.Add(i);
        }

        stats = new SearchStats(_entryCount, passedFirst, candidates, results.Count, blocksSkipped, true);
        return results;
    }

    /// <summary>
    /// Проверяет слова сигнатуры со второго и далее. Доступ здесь случайный, но выполняется
    /// только для записей, прошедших первое слово, — то есть для небольшой доли.
    /// </summary>
    private bool PassesRemainingWords(int entry, ReadOnlySpan<ulong> queryMask)
    {
        for (int w = 1; w < SignatureWords; w++)
        {
            ulong m = queryMask[w];
            if ((_signatures[w * _entryCount + entry] & m) != m) return false;
        }

        return true;
    }

    private bool NameContains(int entry, ReadOnlySpan<char> query)
    {
        ref readonly Entry e = ref _entries[entry];
        return _names.AsSpan(e.NameOffset, e.NameLength).Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
