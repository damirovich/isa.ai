using System.Linq;
using Shouldly;
using Xunit;

namespace ISC.AI.Speech.Worker.Tests;

/// <summary>
/// Группировка реплик по говорящим (ADR-0062 v0.3) на заданных «отпечатках»: похожие реплики — один диктор, порог и
/// заданное число говорящих, короткие реплики примыкают к самой похожей группе, реплика без отпечатка берёт диктора
/// соседней, номера — по первому появлению.
/// </summary>
public sealed class SpeakerClusteringTests
{
    // Два «голоса» — почти ортогональные направления с небольшим разбросом.
    private static readonly float[] A1 = [1f, 0.10f, 0f];
    private static readonly float[] A2 = [0.95f, 0.15f, 0.05f];
    private static readonly float[] B1 = [0.05f, 1f, 0.10f];
    private static readonly float[] B2 = [0.10f, 0.90f, 0f];

    [Fact(DisplayName = "Дикторы: похожие реплики — один диктор, разные — разные; номера по первому появлению")]
    public void Similar_replicas_share_speaker()
    {
        var labels = SpeakerClustering.Cluster([B1, A1, B2, A2], [2000, 2000, 2000, 2000], count: 0, threshold: 0.4);

        labels.ShouldBe([0, 1, 0, 1]);
    }

    [Fact(DisplayName = "Дикторы: порог выше сходства — каждый отдельно; заданное число говорящих важнее порога")]
    public void Threshold_and_count()
    {
        float[][] voices = [A1, A2, B1, B2];
        long[] durations = [2000, 2000, 2000, 2000];

        SpeakerClustering.Cluster(voices, durations, count: 0, threshold: 0.999).Distinct().Count().ShouldBe(4);
        SpeakerClustering.Cluster(voices, durations, count: 1, threshold: 0.999).ShouldAllBe(label => label == 0);
        SpeakerClustering.Cluster(voices, durations, count: 2, threshold: 0.999).ShouldBe([0, 0, 1, 1]);
        SpeakerClustering.Cluster(voices, durations, count: 10, threshold: 0.4).Distinct().Count().ShouldBe(4); // больше, чем реплик
    }

    [Fact(DisplayName = "Дикторы: короткая реплика («да», кашель) не заводит своего диктора, а примыкает к самой похожей группе")]
    public void Short_replica_joins_nearest_group()
    {
        float[] odd = [0.6f, 0.2f, 0.8f]; // ближе к A, но не похожа ни на кого настолько, чтобы слиться по порогу

        var labels = SpeakerClustering.Cluster([A1, B1, odd, A2], [2000, 2000, SpeakerClustering.MinClusterMs - 1, 2000], 0, 0.9);

        labels.ShouldBe([0, 1, 0, 0]);
    }

    [Fact(DisplayName = "Дикторы: реплика без отпечатка — диктор предыдущей (у первой — следующей); без отпечатков вовсе — один диктор")]
    public void Missing_embedding_takes_neighbour()
    {
        SpeakerClustering.Cluster([null, B1, A1, null], [300, 2000, 2000, 300], 0, 0.4).ShouldBe([0, 0, 1, 1]);
        SpeakerClustering.Cluster([null, null], [300, 300], 0, 0.4).ShouldBe([0, 0]);
        SpeakerClustering.Cluster([], [], 0, 0.4).ShouldBeEmpty();
    }
}
