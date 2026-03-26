using System.ComponentModel;

namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 计算通道类型
/// </summary>
public enum ComputedChannelType
{
    /// <summary>自定义公式</summary>
    Custom,

    /// <summary>总场: sqrt(a^2 + b^2 + c^2)</summary>
    TotalField,

    /// <summary>梯度: a - b</summary>
    Gradient,
}

/// <summary>
/// 向导中的通道源选项
/// </summary>
public class SourceOption
{
    public string Label { get; set; } = "";
    public string FormulaExpr { get; set; } = "";
    public override string ToString() => Label;
}

/// <summary>
/// 自定义计算通道定义
/// </summary>
public class ComputedChannelDefinition : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>显示名称</summary>
    public string Name { get; set; } = "Computed";

    /// <summary>计算通道类型（仅作为元数据记录）</summary>
    public ComputedChannelType ChannelType { get; set; } = ComputedChannelType.Custom;

    /// <summary>计算公式（如 "CH0 / CH1"、"sqrt(CH0*CH0+CH1*CH1+CH2*CH2)"）</summary>
    private string _formula = "";
    public string Formula
    {
        get => _formula;
        set
        {
            if (_formula != value)
            {
                _formula = value;
                Notify(nameof(Formula));
            }
        }
    }

    /// <summary>曲线颜色 (ARGB hex)</summary>
    public string ColorHex { get; set; } = "#FF000000";

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>线宽</summary>
    public float LineWidth { get; set; } = 1.5f;

    /// <summary>显示偏移（仅影响图表显示，不影响原始数据和运算）</summary>
    private double _displayOffset;
    public double DisplayOffset
    {
        get => _displayOffset;
        set
        {
            if (_displayOffset != value)
            {
                _displayOffset = value;
                Notify(nameof(DisplayOffset));
            }
        }
    }
}
