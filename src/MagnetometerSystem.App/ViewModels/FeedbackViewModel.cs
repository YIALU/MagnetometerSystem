using CommunityToolkit.Mvvm.ComponentModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Feedback;
using MagnetometerSystem.Infrastructure.Feedback;

namespace MagnetometerSystem.App.ViewModels;

public partial class FeedbackViewModel(IFeedbackClient client, FeedbackDraftStore drafts, IFeedbackLogSource? logs = null) : ObservableObject
{
    /// <summary>是否随反馈附带最近的本地日志（已脱敏，只保存在反馈服务器）。没有日志来源时不显示该选项。</summary>
    [ObservableProperty] private bool _includeLogs = logs is not null;
    public bool CanAttachLogs => logs is not null;
    [ObservableProperty] private string _scenario = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _contact = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isSubmitting;
    [ObservableProperty] private bool _hasSubmitted;
    [ObservableProperty] private string? _issueUrl;
    [ObservableProperty] private bool _isInitialized;
    private FeedbackSubmission? _attempt;
    public bool CanEdit => IsInitialized && !IsSubmitting && !HasSubmitted;
    partial void OnIsInitializedChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); SubmitCommand.NotifyCanExecuteChanged(); }
    private bool CanSubmit() => CanEdit;
    partial void OnIsSubmittingChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); SubmitCommand.NotifyCanExecuteChanged(); }
    partial void OnHasSubmittedChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); SubmitCommand.NotifyCanExecuteChanged(); }

    public async Task InitializeAsync()
    {
        try
        {
            var (saved, savedIncludeLogs) = await drafts.LoadDraftAsync(); if (saved is null) return;
            _attempt = saved; Scenario = saved.Scenario; Description = saved.Description;
            Name = saved.Name ?? ""; Contact = saved.Contact ?? "";
            // 恢复用户上次的勾选（取消勾选后重启不能又默认附带）；上次提交时已固定的日志随草稿保留，重试原样重发。
            if (savedIncludeLogs is { } include) IncludeLogs = include && CanAttachLogs;
            else if (saved.Logs is not null) IncludeLogs = CanAttachLogs;
        }
        catch { Status = "未能读取上次草稿，可以重新填写。"; }
        finally { IsInitialized = true; }
    }
    private FeedbackSubmission Snapshot()
    {
        // 日志在第一次点提交时取一次并随草稿保存；内容或勾选变化后换新编号，提交时重新取。
        var current = new FeedbackSubmission(_attempt?.FeedbackId ?? Guid.NewGuid(), Scenario, Description, Name, Contact,
            _attempt?.Version ?? AppVersion.DiagnosticVersion, IncludeLogs ? _attempt?.Logs : null);
        if (_attempt is not null && current != _attempt)
            current = current with { FeedbackId = Guid.NewGuid(), Version = AppVersion.DiagnosticVersion, Logs = null };
        return current;
    }
    public async Task SaveDraftAsync()
    {
        if (HasSubmitted || IsSubmitting) return;
        try { _attempt = Snapshot(); await drafts.SaveAsync(_attempt, IncludeLogs); }
        catch { Status = "草稿保存失败，请保留当前填写内容。"; }
    }
    /// <summary>取当前将要发送的日志文本供预览；已提交过一次的沿用当时固定的内容。</summary>
    public async Task<string?> PreviewLogsAsync()
    {
        if (Snapshot().Logs is { } fixedLogs)
            return FeedbackLogPayload.TryDecode(fixedLogs, out var text) ? text : null;
        if (logs is null) return null;
        try { return await logs.CollectAsync(); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "读取反馈预览日志失败"); return null; }
    }

    private async Task<string?> CollectEncodedLogsAsync()
    {
        // 空字符串表示“已尝试附带但没有可用日志”，与未勾选（null）区分，重试时不再重新收集。
        if (logs is null) return "";
        try { return await logs.CollectAsync() is { Length: > 0 } text ? FeedbackLogPayload.Encode(text) : ""; }
        catch (Exception ex)
        {
            // 日志读不到不影响文字反馈提交。
            Serilog.Log.Warning(ex, "收集反馈日志失败，本次只提交文字");
            return "";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        var request = Snapshot(); var error = FeedbackValidation.Error(request);
        if (error is not null) { Status = error; return; }
        IsSubmitting = true; Status = "正在提交…";
        var saved = false;
        try
        {
            // 首次附带日志时换一个从未发出过的编号：日志随草稿固定下来，之后的重试原样重发，
            // 不会出现同一编号先后发出不同内容（服务端会按冲突拒绝）。
            if (IncludeLogs && request.Logs is null)
                request = request with { FeedbackId = Guid.NewGuid(), Logs = await CollectEncodedLogsAsync() };
            _attempt = request; await drafts.SaveAsync(request, IncludeLogs); saved = true;
            var receipt = await client.SubmitAsync(request);
            HasSubmitted = true; IssueUrl = receipt.IssueUrl;
            Status = $"反馈已收到，编号 {receipt.FeedbackId.ToString()[..8]}。";
            try { await drafts.ClearAsync(); }
            catch { Status += " 本地草稿未能清理，重新提交仍会返回原回执。"; }
        }
        catch (Exception ex)
        {
            Status = !saved ? "草稿保存失败，本次未提交，请保留当前填写内容。" : ex is TaskCanceledException or HttpRequestException
                ? "暂时未能确认提交结果，内容已保留，请稍后重试。"
                : ex is InvalidOperationException ? ex.Message : "提交失败，内容已保留，请重试。";
        }
        finally { IsSubmitting = false; }
    }
}
