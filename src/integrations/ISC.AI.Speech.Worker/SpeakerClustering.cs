namespace ISC.AI.Speech.Worker;

/// <summary>
/// Группировка реплик по говорящим (ADR-0062 v0.3): «голосовой отпечаток» каждой реплики распознавателя сравнивается с
/// остальными по косинусному сходству, похожие сливаются агломеративно со средней связью — пока сходство групп выше
/// порога либо пока групп больше заданного числа говорящих.
/// </summary>
/// <remarks>
/// <para>
/// Почему по целым репликам, а не по кускам сегментации pyannote: на записи с телефона (шум, эхо) отпечатки кусков по
/// 0,5 с у одного человека получались слишком разными — модель резала его на 5 дикторов; отпечаток реплики в 2–6 с
/// устойчивее, и на той же записи рассказ остался одним диктором (проверка 22.10.2026, ADR-0062 v0.3). Цена: если двое
/// говорят без паузы и распознаватель склеил их в одну реплику, она достанется одному диктору.
/// </para>
/// <para>
/// Короткие реплики (короче <see cref="MinClusterMs"/>: «да», кашель) групп не образуют — их отпечаток ненадёжен; они
/// примыкают к самой похожей группе. Реплика без отпечатка (слишком короткая для модели) берёт диктора соседней по
/// времени. Чистая функция без sherpa-onnx — проверяется модульными тестами.
/// </para>
/// </remarks>
internal static class SpeakerClustering
{
    /// <summary>Реплики короче этого групп не образуют, а примыкают к самой похожей, мс.</summary>
    public const long MinClusterMs = 500;

    /// <summary>
    /// Номера дикторов реплик: с нуля, по первому появлению. Пусто на входе — пусто на выходе.
    /// </summary>
    /// <param name="embeddings">Отпечатки реплик по порядку времени; <see langword="null"/> — отпечатка нет.</param>
    /// <param name="durationsMs">Длительности реплик, мс (тот же порядок).</param>
    /// <param name="count">Сколько говорящих; 0 — по порогу.</param>
    /// <param name="threshold">Порог среднего косинусного сходства групп для слияния (при <paramref name="count"/> = 0).</param>
    public static int[] Cluster(IReadOnlyList<float[]?> embeddings, IReadOnlyList<long> durationsMs, int count, double threshold)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(durationsMs);
        if (embeddings.Count != durationsMs.Count)
        {
            throw new ArgumentException("Число отпечатков и длительностей различается.", nameof(durationsMs));
        }

        var n = embeddings.Count;
        var labels = Enumerable.Repeat(-1, n).ToArray();
        var vectors = embeddings.Select(Normalize).ToArray();
        var withVector = Enumerable.Range(0, n).Where(i => vectors[i] is not null).ToList();
        if (withVector.Count == 0)
        {
            return n == 0 ? labels : new int[n]; // отпечатков нет ни у одной — все одним диктором
        }

        // Опорные реплики — достаточно длинные; если таких нет, опорные все (иначе нечего группировать).
        var anchors = withVector.Where(i => durationsMs[i] >= MinClusterMs).ToList();
        if (anchors.Count == 0)
        {
            anchors = withVector;
        }

        var groups = Agglomerate(anchors.Select(i => vectors[i]!).ToList(), count, threshold);
        for (var g = 0; g < groups.Count; g++)
        {
            foreach (var member in groups[g])
            {
                labels[anchors[member]] = g;
            }
        }

        // Короткие с отпечатком — к группе с наибольшим средним сходством.
        foreach (var i in withVector.Where(i => labels[i] < 0))
        {
            var best = 0;
            var bestSimilarity = double.MinValue;
            for (var g = 0; g < groups.Count; g++)
            {
                var similarity = groups[g].Average(member => (double)Dot(vectors[i]!, vectors[anchors[member]]!));
                if (similarity > bestSimilarity)
                {
                    (best, bestSimilarity) = (g, similarity);
                }
            }

            labels[i] = best;
        }

        // Без отпечатка — диктор соседней по времени (предыдущей, у первой — следующей).
        for (var i = 0; i < n; i++)
        {
            if (labels[i] < 0)
            {
                labels[i] = i > 0 && labels[i - 1] >= 0 ? labels[i - 1] : NextLabel(labels, i);
            }
        }

        return Renumber(labels);
    }

    // Агломеративная кластеризация со средней связью: матрица сходств групп обновляется по Лансу — Уильямсу, у каждой
    // группы хранится самый похожий сосед — обычно O(n²) вместо наивного O(n³).
    private static List<List<int>> Agglomerate(List<float[]> vectors, int count, double threshold)
    {
        var m = vectors.Count;
        var similarity = new float[m * m];
        for (var a = 0; a < m; a++)
        {
            for (var b = a + 1; b < m; b++)
            {
                similarity[a * m + b] = similarity[b * m + a] = Dot(vectors[a], vectors[b]);
            }
        }

        var members = Enumerable.Range(0, m).Select(i => new List<int> { i }).ToArray();
        var active = Enumerable.Repeat(true, m).ToArray();
        var best = new int[m];
        for (var a = 0; a < m; a++)
        {
            best[a] = BestNeighbour(a);
        }

        var groups = m;
        var target = count > 0 ? count : 1;
        while (groups > target)
        {
            var a = -1;
            var top = float.MinValue;
            for (var i = 0; i < m; i++)
            {
                if (active[i] && best[i] >= 0 && similarity[i * m + best[i]] > top)
                {
                    (a, top) = (i, similarity[i * m + best[i]]);
                }
            }

            if (a < 0 || (count <= 0 && top <= threshold))
            {
                break;
            }

            var b = best[a];
            var sizeA = members[a].Count;
            var sizeB = members[b].Count;
            for (var k = 0; k < m; k++)
            {
                if (active[k] && k != a && k != b)
                {
                    var merged = (sizeA * similarity[a * m + k] + sizeB * similarity[b * m + k]) / (sizeA + sizeB);
                    similarity[a * m + k] = similarity[k * m + a] = merged;
                }
            }

            members[a].AddRange(members[b]);
            active[b] = false;
            groups--;

            // Соседей пересчитать у самой группы и у тех, чьим лучшим был слитый; остальным — сравнить с новой группой.
            best[a] = BestNeighbour(a);
            for (var k = 0; k < m; k++)
            {
                if (!active[k] || k == a)
                {
                    continue;
                }

                if (best[k] == a || best[k] == b)
                {
                    best[k] = BestNeighbour(k);
                }
                else if (best[k] < 0 || similarity[k * m + a] > similarity[k * m + best[k]])
                {
                    best[k] = a;
                }
            }
        }

        return Enumerable.Range(0, m).Where(i => active[i]).Select(i => members[i]).ToList();

        int BestNeighbour(int row)
        {
            var found = -1;
            var value = float.MinValue;
            for (var k = 0; k < m; k++)
            {
                if (k != row && active[k] && similarity[row * m + k] > value)
                {
                    (found, value) = (k, similarity[row * m + k]);
                }
            }

            return found;
        }
    }

    private static int NextLabel(int[] labels, int from)
    {
        for (var j = from + 1; j < labels.Length; j++)
        {
            if (labels[j] >= 0)
            {
                return labels[j];
            }
        }

        return 0;
    }

    // Номера по первому появлению: «Диктор 1» — тот, кто заговорил первым (адаптер нумерует так же).
    private static int[] Renumber(int[] labels)
    {
        var map = new Dictionary<int, int>();
        return labels.Select(label => map.TryGetValue(label, out var mapped) ? mapped : map[label] = map.Count).ToArray();
    }

    private static float[]? Normalize(float[]? vector)
    {
        if (vector is null || vector.Length == 0)
        {
            return null;
        }

        var norm = MathF.Sqrt(Dot(vector, vector));
        return norm > 0 && float.IsFinite(norm) ? vector.Select(x => x / norm).ToArray() : null;
    }

    private static float Dot(float[] a, float[] b)
    {
        var sum = 0f;
        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}
