using MagnetometerSystem.Core.Calibration;

namespace MagnetometerSystem.Core.Tests;

/// <summary>拟合样本集：换一批时样本、单位、来源和代号一起替换，带旧代号的修改不生效，两组样本逐点对应。</summary>
public class CalibrationSampleSetTests
{
    [Fact]
    public void ReplaceSwapsSamplesUnitSourceAndMapTogetherAndAdvancesTheGeneration()
    {
        var set = new CalibrationSampleSet();
        Assert.Equal(0, set.Generation);
        Assert.Equal("", set.Unit);
        Assert.Equal("", set.SourceKey);
        Assert.Null(set.Map);

        int[] map = [3, 1, 2];
        var generation = set.Replace([[1, 2, 3], [4, 5, 6]], [[7, 8, 9]], "uT", 6, "session\u0001a", map);
        map[0] = 0;   // 调用方之后改自己的数组，不影响记下的通道

        Assert.Equal(1, generation);
        Assert.Equal(generation, set.Generation);
        Assert.Equal(2, set.Count);
        Assert.Equal("uT", set.Unit);
        Assert.Equal(6, set.ChannelCount);
        Assert.Equal("session\u0001a", set.SourceKey);
        Assert.Equal(new[] { 3, 1, 2 }, set.Map);
        var snapshot = set.Snapshot();
        Assert.Equal([[1d, 2, 3], [4d, 5, 6]], snapshot.First);
        Assert.Equal([[7d, 8, 9]], snapshot.Second);
        Assert.Equal(("uT", 6, generation), (snapshot.Unit, snapshot.ChannelCount, snapshot.Generation));

        // 导入文件：没有通道映射，默认不记来源。
        Assert.Equal(2, set.Replace([[1, 1, 1]], [], "nT", 3));
        Assert.Null(set.Map);
        Assert.Equal("", set.SourceKey);
        Assert.Empty(set.Snapshot().Second);
    }

    [Fact]
    public void ChangesWithAnOldGenerationAreIgnored()
    {
        var set = new CalibrationSampleSet();
        var old = set.Replace([], [], "nT", 3);
        Assert.Equal(1, set.TryAdd(old, [1, 2, 3]));

        var current = set.Replace([], [], "nT", 3);
        Assert.Equal(-1, set.TryAdd(old, [4, 5, 6]));
        Assert.Equal(-1, set.TryRemoveLast(old));
        Assert.Equal(-1, set.TryClear(old));
        Assert.Equal(0, set.Count);

        Assert.Equal(1, set.TryAdd(current, [7, 8, 9]));
        set.Invalidate();   // 释放时作废：之后带这一批代号的追加也不生效
        Assert.Equal(-1, set.TryAdd(current, [1, 1, 1]));
        Assert.Equal(0, set.Count);
        Assert.Equal(current + 1, set.Generation);
    }

    [Fact]
    public void UndoAndClearKeepTheTwoGroupsAligned()
    {
        var set = new CalibrationSampleSet();
        var generation = set.Replace([], [], "nT", 6);
        for (int i = 1; i <= 3; i++) set.TryAdd(generation, [i, i, i], [10 * i, 10 * i, 10 * i]);

        Assert.Equal(2, set.TryRemoveLast(generation));
        var snapshot = set.Snapshot();
        Assert.Equal([[1d, 1, 1], [2d, 2, 2]], snapshot.First);
        Assert.Equal([[10d, 10, 10], [20d, 20, 20]], snapshot.Second);

        Assert.Equal(2, set.TryClear(generation));
        Assert.Equal(-1, set.TryRemoveLast(generation));   // 没有样本可撤销
        Assert.Empty(set.Snapshot().Second);
        Assert.Equal(1, set.TryAdd(generation, [4, 4, 4], [40, 40, 40]));   // 清空后同一批仍可继续追加
        Assert.Equal("nT", set.Unit);
    }

    [Fact]
    public void UndoOnlyRemovesTheSecondGroupWhenItIsLonger()
    {
        // 导入的双三轴文件里第二组可能缺行，第二组比第一组短。
        var set = new CalibrationSampleSet();
        var generation = set.Replace([[1, 1, 1], [2, 2, 2], [3, 3, 3]], [[10, 10, 10]], "nT", 6);

        Assert.Equal(2, set.TryRemoveLast(generation));
        Assert.Equal([[10d, 10, 10]], set.Snapshot().Second);
    }

    [Fact]
    public void SnapshotIsACopy()
    {
        var set = new CalibrationSampleSet();
        var generation = set.Replace([[1, 2, 3]], [], "nT", 3);
        var snapshot = set.Snapshot();
        snapshot.First.Clear();
        set.TryAdd(generation, [4, 5, 6]);

        Assert.Empty(snapshot.First);
        Assert.Equal(2, set.Snapshot().First.Count);
    }

    [Fact]
    public async Task ConcurrentAddsAndSnapshotsStayConsistent()
    {
        var set = new CalibrationSampleSet();
        var generation = set.Replace([], [], "nT", 6);
        const int perWriter = 2000;
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < perWriter; i++) set.TryAdd(generation, [w, i, 0], [w, i, 1]);
        })).ToArray();
        while (!writers.All(t => t.IsCompleted))
        {
            var snapshot = set.Snapshot();
            Assert.Equal(snapshot.First.Count, snapshot.Second.Count);
        }
        await Task.WhenAll(writers);

        Assert.Equal(4 * perWriter, set.Count);
        var final = set.Snapshot();
        Assert.All(final.First.Zip(final.Second), pair => Assert.Equal(pair.First[..2], pair.Second[..2]));
    }
}
