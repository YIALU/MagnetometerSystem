using System.Collections.ObjectModel;
using System.Windows;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.App.Views.Dialogs;

/// <summary>
/// 命令编辑对话框 — 编辑命令名/描述/编码模式（ASCII 模板 / 二进制帧），
/// 二进制帧支持帧头/帧尾/校验���参数支持任意类型与字节序。
/// </summary>
public partial class CommandEditDialog : Window
{
    private readonly DeviceCommand _command;

    public ObservableCollection<CommandParameter> Parameters { get; }

    public CommandParameterType[] ParameterTypes { get; } = System.Enum.GetValues<CommandParameterType>();
    public Endianness[] Endians { get; } = System.Enum.GetValues<Endianness>();
    public ChecksumKind[] ChecksumKinds { get; } = System.Enum.GetValues<ChecksumKind>();

    public CommandEditDialog(DeviceCommand command, string title)
    {
        _command = command;
        Title = title;
        Parameters = new ObservableCollection<CommandParameter>(command.Parameters.Select(Clone));

        InitializeComponent();
        DataContext = this;

        NameBox.Text = command.Name;
        DescriptionBox.Text = command.Description;

        if (command.Encoding == CommandEncoding.AsciiTemplate)
            AsciiRadio.IsChecked = true;
        else
            BinaryRadio.IsChecked = true;

        TemplateBox.Text = command.Template;
        AppendNewlineCheckBox.IsChecked = command.AppendNewline;

        FrameHeaderBox.Text = command.FrameHeader;
        FrameTailBox.Text = command.FrameTail;
        ChecksumBox.SelectedItem = command.Checksum;

        UpdateModeVisibility();
    }

    private static CommandParameter Clone(CommandParameter p) => new()
    {
        Name = p.Name,
        Key = p.Key,
        Type = p.Type,
        DefaultValue = p.DefaultValue,
        Unit = p.Unit,
        Min = p.Min,
        Max = p.Max,
        EnumOptions = new List<string>(p.EnumOptions),
        Endian = p.Endian,
        ByteLength = p.ByteLength,
    };

    private void Mode_Checked(object sender, RoutedEventArgs e) => UpdateModeVisibility();

    private void UpdateModeVisibility()
    {
        if (AsciiPanel == null || BinaryPanel == null) return;
        bool isAscii = AsciiRadio.IsChecked == true;
        AsciiPanel.Visibility = isAscii ? Visibility.Visible : Visibility.Collapsed;
        BinaryPanel.Visibility = isAscii ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddParameter_Click(object sender, RoutedEventArgs e)
    {
        Parameters.Add(new CommandParameter
        {
            Name = "参数" + (Parameters.Count + 1),
            Key = "p" + (Parameters.Count + 1),
            Type = (BinaryRadio.IsChecked == true) ? CommandParameterType.U8 : CommandParameterType.String,
        });
    }

    private void RemoveParameter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is CommandParameter p)
        {
            Parameters.Remove(p);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show("命令名不能为空", "校验");
            return;
        }

        var keys = new HashSet<string>();
        foreach (var p in Parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Key))
            {
                MessageBox.Show("参数 key 不能为空", "校验");
                return;
            }
            if (!keys.Add(p.Key))
            {
                MessageBox.Show($"参数 key '{p.Key}' 重复", "校验");
                return;
            }
        }

        _command.Name = NameBox.Text.Trim();
        _command.Description = DescriptionBox.Text ?? "";
        _command.Encoding = (AsciiRadio.IsChecked == true)
            ? CommandEncoding.AsciiTemplate
            : CommandEncoding.BinaryFrame;
        _command.Template = TemplateBox.Text ?? "";
        _command.AppendNewline = AppendNewlineCheckBox.IsChecked ?? true;
        _command.FrameHeader = FrameHeaderBox.Text ?? "";
        _command.FrameTail = FrameTailBox.Text ?? "";
        _command.Checksum = (ChecksumKind)(ChecksumBox.SelectedItem ?? ChecksumKind.None);
        _command.Parameters = Parameters.ToList();

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
