using CommunityToolkit.Mvvm.ComponentModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Feedback;
using MagnetometerSystem.Infrastructure.Feedback;

namespace MagnetometerSystem.App.ViewModels;

public partial class FeedbackViewModel(IFeedbackClient client, FeedbackDraftStore drafts) : ObservableObject
{
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
            var saved = await drafts.LoadAsync(); if (saved is null) return;
            _attempt = saved; Scenario = saved.Scenario; Description = saved.Description;
            Name = saved.Name ?? ""; Contact = saved.Contact ?? "";
        }
        catch { Status = "未能读取上次草稿，可以重新填写。"; }
        finally { IsInitialized = true; }
    }
    private FeedbackSubmission Snapshot()
    {
        var current = new FeedbackSubmission(_attempt?.FeedbackId ?? Guid.NewGuid(), Scenario, Description, Name, Contact,
            _attempt?.Version ?? AppVersion.DiagnosticVersion);
        if (_attempt is not null && current != _attempt) current = current with { FeedbackId = Guid.NewGuid(), Version = AppVersion.DiagnosticVersion };
        return current;
    }
    public async Task SaveDraftAsync()
    {
        if (HasSubmitted || IsSubmitting) return;
        try { _attempt = Snapshot(); await drafts.SaveAsync(_attempt); }
        catch { Status = "草稿保存失败，请保留当前填写内容。"; }
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
            _attempt = request; await drafts.SaveAsync(request); saved = true;
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
