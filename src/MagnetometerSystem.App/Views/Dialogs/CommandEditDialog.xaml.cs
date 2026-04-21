using System.Collections.ObjectModel;
using System.Windows;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.App.Views.Dialogs;

/// <summary>
/// 命令编辑对话框 — 编辑命令名/描述/模板，可动态添加/删除参数
/// </summary>
public partial class CommandEditDialog : Window
{
    private readonly DeviceCommand _command;

    public ObservableCollection<CommandParameter> Parameters { get; }

    public CommandParameterType[] ParameterTypes { get; } = Enum.GetValues<CommandParameterType>();

    public CommandEditDialog(DeviceCommand command, string title)
    {
        _command = command;
        Title = title;
        Parameters = new ObservableCollection<CommandParameter>(command.Parameters.Select(Clone));

        InitializeComponent();
        DataContext = this;

        NameBox.Text = command.Name;
        DescriptionBox.Text = command.Description;
        TemplateBox.Text = command.Template;
        IsHexCheckBox.IsChecked = command.IsHex;
        AppendNewlineCheckBox.IsChecked = command.AppendNewline;
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
    };

    private void AddParameter_Click(object sender, RoutedEventArgs e)
    {
        Parameters.Add(new CommandParameter
        {
            Name = "参数" + (Parameters.Count + 1),
            Key = "p" + (Parameters.Count + 1),
            Type = CommandParameterType.String,
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

        // 校验参数 Key 唯一且非空
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
        _command.Template = TemplateBox.Text ?? "";
        _command.IsHex = IsHexCheckBox.IsChecked ?? false;
        _command.AppendNewline = AppendNewlineCheckBox.IsChecked ?? true;
        _command.Parameters = Parameters.ToList();

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
